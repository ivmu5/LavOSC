using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Laveyana.LavOSC;

internal sealed record VJoySetupOptions(string? DllOverride, int RequestedId, int PreferredId,
    VJoyAxes RequiredAxes, bool AllowDeviceCreation);
internal sealed record VJoyPreparation(VJoyScan Scan, int Id, bool Created);
internal sealed record VJoyProgress(string Message, VJoyScan? Scan = null);

internal static class VJoySetup
{
    private const string DllName = "vJoyInterface.dll";
    private const string InstallerKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8E31F76F-74C3-47F1-9550-E041EEDC5FBB}_is1";

    public static async Task<VJoyPreparation> PrepareAsync(VJoySetupOptions options,
        IProgress<VJoyProgress> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] installed = FindInstalledLibraries();
        VJoyScan scan = ScanInstallation(options.DllOverride, installed);
        if (!scan.DriverEnabled)
        {
            progress.Report(new("Проверка состояния vJoy в Windows…", scan));
            var devices = VJoyWindowsDevices.Read();
            var disabled = devices.Where(device => device.Disabled).ToArray();
            if (disabled.Length != 1 || devices.Any(device => device.Started))
                throw new InvalidOperationException(DescribeUnavailableDriver(devices));

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                throw new InvalidOperationException("vJoy отключён (код 22). Включи vJoy Device в диспетчере устройств.");

            progress.Report(new("vJoy отключён в Windows (код 22). Включение через PnPUtil…", scan));
            string pnpUtil = Path.Combine(Environment.SystemDirectory, "pnputil.exe");
            await RunElevatedAsync(pnpUtil, cancellationToken, "/enable-device", disabled[0].InstanceId)
                .ConfigureAwait(false);

            var recovery = await WaitForAsync(() => ScanInstallation(options.DllOverride, installed),
                state => state.DriverEnabled, cancellationToken).ConfigureAwait(false);
            scan = recovery ?? throw new InvalidOperationException(DescribeUnavailableDriver(VJoyWindowsDevices.Read()));
        }

        string requiredAxes = VJoyOutput.FormatAxes(options.RequiredAxes);
        progress.Report(new($"Поиск доступных осей {requiredAxes}…", scan));
        var available = SelectAvailable(scan.Devices, options);
        if (available is not null)
            return new(scan, available.Id, false);

        if (!options.AllowDeviceCreation)
            throw new InvalidOperationException($"Оси {requiredAxes} недоступны. Отключи активную передачу в vJoy " +
                "перед созданием устройства или выбери устройство с нужными осями.");

        var empty = scan.Devices.FirstOrDefault(device => device.State == VJoyDeviceState.Missing &&
            (options.RequestedId == 0 || device.Id == options.RequestedId));
        if (empty is null)
        {
            if (options.RequestedId != 0)
                throw new InvalidOperationException($"Устройство №{options.RequestedId} занято или у него нет осей {requiredAxes}. " +
                    "Проверь его в Configure vJoy, выбери другой номер или «Автоматически».");

            throw new InvalidOperationException($"Нет свободных осей {requiredAxes} и свободного номера для создания устройства.");
        }

        string path = scan.DllPath;
        progress.Report(new($"Создание устройства №{empty.Id} с осями X, Y, Z…", scan));
        await ConfigureAsync(path, installed, cancellationToken, empty.Id.ToString(CultureInfo.InvariantCulture),
            "-a", "x", "y", "z", "-b", "0").ConfigureAwait(false);

        scan = await WaitForAsync(() => VJoyOutput.Scan(path), state => state.DriverEnabled && state.Devices.Any(device =>
                device.Id == empty.Id && (device.Axes & VJoyAxes.All) == VJoyAxes.All), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Устройство №{empty.Id} с осями X, Y, Z не появилось. " +
                "Проверь этот номер в Configure vJoy.");
        progress.Report(new($"Проверка устройства №{empty.Id}…", scan));
        if (SelectAvailable(scan.Devices, options with { RequestedId = empty.Id }) is null)
            throw new InvalidOperationException($"Устройство №{empty.Id} занято или оси {requiredAxes} недоступны.");

        return new(scan, empty.Id, true);
    }

    internal static VJoyDevice? SelectAvailable(IEnumerable<VJoyDevice> devices, VJoySetupOptions options) =>
        devices.Where(device => (device.AvailableAxes & options.RequiredAxes) == options.RequiredAxes &&
                                (options.RequestedId == 0 || device.Id == options.RequestedId))
            .OrderBy(device => device.Id != options.PreferredId)
            .ThenBy(device => device.Id)
            .FirstOrDefault();

    internal static string DescribeUnavailableDriver(VJoyWindowsDevice[] devices)
    {
        if (devices.Length == 0)
            return "Windows не обнаружила устройство драйвера vJoy. Проверь установку vJoy в диспетчере устройств.";

        var codes = devices.Where(device => device.ProblemCode.HasValue)
            .Select(device => device.ProblemCode.GetValueOrDefault()).Distinct().Order().ToArray();
        if (codes is [22])
            return "vJoy отключён в Windows (код 22). Включи vJoy Device в диспетчере устройств и повтори подготовку.";
        if (codes is [52])
            return "Windows не может проверить цифровую подпись драйвера vJoy (код 52). " +
                "Установи подписанную версию vJoy.";
        if (codes.Length > 0)
            return $"Windows сообщает об ошибке устройства vJoy (код: {string.Join(", ", codes)}). " +
                "Открой vJoy Device в диспетчере устройств и проверь «Состояние устройства».";
        if (devices.Any(device => device.Started))
            return "Windows запустила устройство vJoy, но интерфейс драйвера не доступен этой DLL. " +
                "Очисти ручной путь к DLL и повтори подготовку. При необходимости проверь версию установки vJoy.";

        return "Устройство vJoy найдено, но Windows ещё не запустила его. Проверь состояние vJoy Device в диспетчере устройств.";
    }

    private static async Task<VJoyScan?> WaitForAsync(Func<VJoyScan> read, Func<VJoyScan, bool> ready,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scan = read();
            if (ready(scan))
                return scan;
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        } while (timer.Elapsed < TimeSpan.FromSeconds(12));

        return null;
    }

    private static Task ConfigureAsync(string dllPath, string[] installed, CancellationToken cancellationToken,
        params string[] arguments)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? tool = installed.Contains(dllPath, StringComparer.OrdinalIgnoreCase)
            ? FindConfigTool(dllPath)
            : null;
        if (tool is null)
            throw new InvalidOperationException("Не найден vJoyConfig.exe рядом с DLL установленного vJoy. " +
                "Установи компонент Configure vJoy или настрой нужные оси вручную.");

        return RunElevatedAsync(tool, cancellationToken, arguments);
    }

    private static async Task RunElevatedAsync(string tool, CancellationToken cancellationToken,
        params string[] arguments)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = Path.GetFileName(tool);
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(tool)!
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start) ??
                throw new InvalidOperationException($"Не удалось запустить {name}.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"{name} ещё работает. Дождись его завершения и нажми «Подготовить vJoy».");
            }

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{name} завершился с кодом {process.ExitCode}. Проверь vJoy в диспетчере устройств.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("Настройка vJoy отменена в запросе Windows. Нажми «Подготовить vJoy», чтобы повторить.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Не удалось запустить {name}. {exception.Message}", exception);
        }
    }

    private static VJoyScan ScanInstallation(string? pathOverride, string[] installed)
    {
        string path = (pathOverride ?? "").Trim().Trim('"');
        if (path.Length > 0)
        {
            if (Path.IsPathFullyQualified(path) && Path.GetFileName(path).Equals(DllName, StringComparison.OrdinalIgnoreCase)
                                                && File.Exists(path))
                return VJoyOutput.Scan(Path.GetFullPath(path));

            throw new InvalidOperationException($"Укажи полный путь к 64-битной {DllName} или оставь поле пустым для автопоиска.");
        }

        VJoyScan? firstScan = null;
        InvalidOperationException? firstError = null;
        foreach (string candidate in installed)
        {
            try
            {
                var scan = VJoyOutput.Scan(candidate);
                if (scan.DriverEnabled)
                    return scan;
                firstScan ??= scan;
            }
            catch (InvalidOperationException exception)
            {
                firstError ??= exception;
            }
        }

        return firstScan ?? throw (firstError ??
            new InvalidOperationException("vJoy не найден. Установи драйвер с компонентом Configure vJoy и нажми «Подготовить vJoy»."));
    }

    private static string[] FindInstalledLibraries()
    {
        var directories = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(InstallerKey);
            if (key?.GetValue("DllX64Location") is string x64 && Path.IsPathFullyQualified(x64))
                directories.Add(x64);

            if (key?.GetValue("InstallLocation") is string root && Path.IsPathFullyQualified(root))
            {
                directories.Add(Path.Combine(root, "x64"));
                directories.Add(root);
            }
        }

        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string root = Environment.GetFolderPath(folder);
            if (Path.IsPathFullyQualified(root))
                directories.Add(Path.Combine(root, "vJoy", "x64"));
        }

        return directories.Select(directory => Path.GetFullPath(Path.Combine(directory, DllName)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindConfigTool(string dllPath)
    {
        string directory = Path.GetDirectoryName(dllPath)!;
        string tool = Path.Combine(directory, "vJoyConfig.exe");
        return File.Exists(tool) ? tool : null;
    }
}
