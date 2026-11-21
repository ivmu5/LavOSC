using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;
using Laveyana.LavOSC.Core;
using Laveyana.LavOSC.Modules.Wheel;

namespace Laveyana.LavOSC;

internal sealed class OscReceiverPage : ContentPage
{
    private static readonly Color Canvas = Color.FromArgb("#111519");
    private static readonly Color Panel = Color.FromArgb("#1A2128");
    private static readonly Color Outline = Color.FromArgb("#333F4B");
    private static readonly Color Text = Color.FromArgb("#F2F5F7");
    private static readonly Color Muted = Color.FromArgb("#B1BEC9");
    private static readonly Color Accent = Color.FromArgb("#94E1D0");
    private static readonly Color Warning = Color.FromArgb("#F4C788");

    private readonly Entry port = MakeEntry(Keyboard.Numeric);
    private readonly Picker device = new() { TextColor = Text, FontSize = 14 };
    private readonly Entry dllPath = MakeEntry(Keyboard.Plain);
    private readonly Button listen = MakeButton("Начать приём");
    private readonly Button calibrate = MakeButton("Выставить центр");
    private readonly Button drive = MakeButton("Включить руль", primary: true);
    private readonly Button prepare = MakeButton("Подготовить vJoy");
    private readonly Label networkStatus = Caption();
    private readonly Label routingStatus = Caption();
    private readonly Label sampleStatus = Caption();
    private readonly Label vJoyStatus = Caption();
    private readonly Label vJoyDevices = Caption();
    private readonly Label vJoyLibrary = Caption();
    private readonly Label localAddresses = Caption();
    private readonly Label sensorValues = Caption();
    private readonly Label angle = new() { FontSize = 40, FontAttributes = FontAttributes.Bold, TextColor = Text };
    private readonly Label axisValue = Caption();
    private readonly Label notice = Caption();
    private readonly Switch invert = new() { OnColor = Accent };
    private readonly Switch autoSetup = new() { OnColor = Accent };
    private readonly Switch wheelEnabled = new() { OnColor = Accent };
    private readonly Slider lockDegrees = MakeSlider(25, 110);
    private readonly Slider deadZone = MakeSlider(0, 10);
    private readonly Slider smoothing = MakeSlider(0, 150);
    private readonly SteeringGauge gauge = new();
    private readonly GraphicsView gaugeView = new() { HeightRequest = 40 };
    private readonly IDispatcherTimer timer;
    private readonly Stopwatch rateClock = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private readonly OscRouter router = new();
    private readonly List<IDisposable> moduleRegistrations = [];
    private OscUdpReceiver? receiver;
    private IDisposable? wheelRegistration;
    private WheelSession? session;
    private long previousCount;
    private double frequency;
    private string? error;
    private string? moduleRegistrationErrors;
    private string? vJoyError;
    private string? vJoyMessage;
    private VJoyScan? vJoyScan;
    private int? selectedDevice;
    private bool working;
    private bool initialized;
    private bool closed;
    private bool armAfterCalibration;

    public OscReceiverPage(IReadOnlyList<IOscModule> modules)
    {
        BackgroundColor = Canvas;
        var preferences = Preferences.Default;
        port.Text = preferences.Get("OscPort", 8000).ToString(CultureInfo.InvariantCulture);
        device.Items.Add("Автоматически");
        for (int id = 1; id <= 16; id++)
            device.Items.Add($"Устройство №{id}");
        device.SelectedIndex = Math.Clamp(preferences.Get("VJoyRequestedId", 0), 0, 16);
        dllPath.Placeholder = "Автопоиск установленной DLL";
        string oldDllPath = preferences.Get("VJoyDll", "");
        dllPath.Text = preferences.Get("VJoyDllOverride", File.Exists(oldDllPath) ? oldDllPath : "");
        autoSetup.IsToggled = preferences.Get("VJoyAutoSetup", true);
        wheelEnabled.IsToggled = preferences.Get("WheelModuleEnabled", true);

        var settings = new SteeringSettings(preferences.Get("LockDegrees", 60d),
            preferences.Get("DeadZoneDegrees", 2d), preferences.Get("SmoothingMs", 55d),
            preferences.Get("Invert", false)).Normalize();
        lockDegrees.Value = settings.LockDegrees;
        deadZone.Value = settings.DeadZoneDegrees;
        smoothing.Value = settings.SmoothingMs;
        invert.IsToggled = settings.Invert;

        gaugeView.Drawable = gauge;
        SemanticProperties.SetDescription(gaugeView, "Положение оси руля");
        listen.Clicked += (_, _) => ToggleListening();
        calibrate.Clicked += (_, _) =>
        {
            session?.Calibrate();
            Refresh();
        };
        drive.Clicked += async (_, _) =>
        {
            armAfterCalibration = false;
            if (session?.Snapshot().OutputActive == true)
                session.DisableOutput();
            else
                await PrepareVJoyAsync(arm: true);
            Refresh();
        };
        prepare.Clicked += async (_, _) => await PrepareVJoyAsync(arm: false);
        wheelEnabled.Toggled += async (_, args) =>
        {
            preferences.Set("WheelModuleEnabled", args.Value);
            if (args.Value)
            {
                if (receiver?.Snapshot().Listening == true)
                    AttachWheel();
                if (autoSetup.IsToggled)
                {
                    armAfterCalibration = true;
                    await PrepareVJoyAsync(arm: false);
                }
            }
            else
            {
                armAfterCalibration = false;
                DetachWheel();
                ClearPreparedDevice();
            }
            Refresh();
        };
        invert.Toggled += (_, _) => SaveSteeringSettings();
        autoSetup.Toggled += (_, args) =>
        {
            preferences.Set("VJoyAutoSetup", args.Value);
            if (!args.Value)
                armAfterCalibration = false;
        };
        device.SelectedIndexChanged += (_, _) =>
        {
            preferences.Set("VJoyRequestedId", Math.Max(0, device.SelectedIndex));
            ClearPreparedDevice();
        };
        dllPath.TextChanged += (_, args) =>
        {
            preferences.Set("VJoyDllOverride", args.NewTextValue ?? "");
            ClearPreparedDevice();
        };

        var layout = new Grid
        {
            Padding = new Thickness(18),
            ColumnSpacing = 16,
            ColumnDefinitions = { new ColumnDefinition(new GridLength(360)), new ColumnDefinition(GridLength.Star) }
        };
        layout.Add(new ScrollView { Content = MakeControls() }, 0, 0);
        layout.Add(new ScrollView { Content = MakeDiagnostics() }, 1, 0);
        Content = layout;

        ShowAddresses();
        Refresh();
        timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += async (_, _) =>
        {
            Refresh();
            if (armAfterCalibration && !working && !closed &&
                session?.Snapshot() is { Fresh: true, Calibrated: true, OutputActive: false })
                await PrepareVJoyAsync(arm: true);
        };
        timer.Start();
        Loaded += async (_, _) =>
        {
            if (initialized || closed)
                return;

            initialized = true;
            RegisterModules(modules);
            ToggleListening();
            if (wheelEnabled.IsToggled && autoSetup.IsToggled)
            {
                armAfterCalibration = true;
                await PrepareVJoyAsync(arm: false);
            }
        };
    }

    public void Close()
    {
        if (closed)
            return;

        closed = true;
        timer.Stop();
        lifetime.Cancel();
        if (!working)
            lifetime.Dispose();
        StopListening();
        foreach (IDisposable registration in moduleRegistrations)
            registration.Dispose();
        moduleRegistrations.Clear();
    }

    private void RegisterModules(IReadOnlyList<IOscModule> modules)
    {
        var errors = new List<string>();
        foreach (IOscModule module in modules)
        {
            try
            {
                if (module.Name is "wheel" or "whell")
                    throw new InvalidOperationException("Адреса /wheel и /whell закреплены за модулем руля.");

                moduleRegistrations.Add(router.Register(module));
            }
            catch (Exception exception)
            {
                errors.Add($"{module.GetType().Name}: {exception.Message}");
            }
        }

        if (errors.Count > 0)
            moduleRegistrationErrors = string.Join(Environment.NewLine, errors);
    }

    private View MakeControls()
    {
        var reload = MakeButton("Обновить IP");
        reload.Clicked += (_, _) => ShowAddresses();

        return new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                Heading("Управление", 27),
                Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Heading("Приём OSC", 18),
                        Field("UDP-порт", port),
                        Row(listen, reload)
                    }
                }),
                Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Heading("Руль · /wheel", 18),
                        Row(Caption("Модуль включён"), wheelEnabled),
                        Row(calibrate, drive),
                        SliderField("Угол от центра до упора", lockDegrees, "°"),
                        SliderField("Мёртвая зона", deadZone, "°"),
                        SliderField("Сглаживание", smoothing, " мс"),
                        Row(Caption("Инверсия руля"), invert)
                    }
                }),
                Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        Heading("vJoy", 18),
                        Row(Caption("Автоподготовка при запуске"), autoSetup),
                        Field("Устройство с осью X", device),
                        Field("Путь к vJoyInterface.dll", dllPath),
                        prepare
                    }
                })
            }
        };
    }

    private View MakeDiagnostics() => new VerticalStackLayout
    {
        Spacing = 12,
        Children =
        {
            Heading("Показания", 27),
            Card(new VerticalStackLayout
            {
                Spacing = 7,
                Children = { Heading("Приём OSC", 18), networkStatus, localAddresses, routingStatus }
            }),
            Card(new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    Heading("Ось руля", 18),
                    Row(angle, axisValue),
                    gaugeView,
                    Row(Caption("Влево"), Caption("Вправо")),
                    sensorValues,
                    sampleStatus,
                    notice
                }
            }),
            Card(new VerticalStackLayout
            {
                Spacing = 7,
                Children = { Heading("Состояние vJoy", 18), vJoyStatus, vJoyDevices, vJoyLibrary }
            })
        }
    };

    private void ToggleListening()
    {
        error = null;
        if (receiver?.Snapshot().Listening == true)
        {
            StopListening();
        }
        else if (!TryNumber(port.Text, 1, 65535, out int value))
        {
            error = "Введи UDP-порт от 1 до 65535.";
        }
        else
        {
            StopListening();
            try
            {
                if (wheelEnabled.IsToggled)
                    AttachWheel();
                receiver = new OscUdpReceiver(value, router);
                Preferences.Default.Set("OscPort", value);
            }
            catch (SocketException exception)
            {
                StopListening();
                error = $"Не удалось открыть UDP-порт {value}. {exception.Message}";
            }
        }

        Refresh();
    }

    private void AttachWheel()
    {
        var next = new WheelSession(CurrentSettings());
        try
        {
            wheelRegistration = router.Register(next, "whell");
            session = next;
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    private void StopListening()
    {
        receiver?.Dispose();
        receiver = null;
        DetachWheel();
    }

    private void DetachWheel()
    {
        wheelRegistration?.Dispose();
        wheelRegistration = null;
        session?.Dispose();
        session = null;
        previousCount = 0;
        frequency = 0;
        rateClock.Restart();
    }

    private async Task PrepareVJoyAsync(bool arm)
    {
        if (working || closed || !wheelEnabled.IsToggled || session?.Snapshot().OutputActive == true)
            return;

        working = true;
        vJoyError = null;
        vJoyMessage = "Поиск установленного vJoy…";
        selectedDevice = null;
        vJoyScan = null;
        Refresh();
        var token = lifetime.Token;
        try
        {
            var options = new VJoySetupOptions(dllPath.Text, Math.Max(0, device.SelectedIndex),
                Preferences.Default.Get("VJoyDevice", 0));
            var progress = new Progress<VJoyProgress>(update =>
            {
                if (closed || !working)
                    return;
                vJoyMessage = update.Message;
                vJoyScan = update.Scan;
                Refresh();
            });
            var prepared = await Task.Run(() => VJoySetup.PrepareAsync(options, progress, token), token);
            if (closed)
                return;

            vJoyScan = prepared.Scan;
            selectedDevice = prepared.Id;
            vJoyMessage = prepared.Created ? $"Создано устройство №{prepared.Id} · ось X" :
                $"Выбрано устройство №{prepared.Id} · ось X";

            if (session is { } current &&
                (arm || armAfterCalibration) &&
                current.Snapshot() is { Fresh: true, Calibrated: true })
            {
                armAfterCalibration = false;
                vJoyMessage = $"Подключение к устройству №{prepared.Id}…";
                Refresh();
                await AttachAsync(current, prepared, token);
                vJoyMessage = $"Устройство №{prepared.Id} · ось X";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            vJoyError = exception.Message;
            armAfterCalibration = false;
        }
        finally
        {
            working = false;
            if (closed)
                lifetime.Dispose();
            else
                Refresh();
        }
    }

    private async Task AttachAsync(WheelSession current, VJoyPreparation prepared, CancellationToken token)
    {
        VJoyOutput? pending = await Task.Run(() => VJoyOutput.Connect((uint)prepared.Id, prepared.Scan.DllPath), token);
        try
        {
            if (closed || !ReferenceEquals(session, current))
                return;

            current.EnableOutput(() => pending);
            pending = null;
            Preferences.Default.Set("VJoyDevice", prepared.Id);
        }
        finally
        {
            pending?.Dispose();
        }
    }

    private void ClearPreparedDevice()
    {
        selectedDevice = null;
        vJoyError = null;
        vJoyMessage = null;
        vJoyScan = null;
        Refresh();
    }

    private void Refresh()
    {
        WheelSnapshot? snapshot = session?.Snapshot();
        bool receiving = snapshot?.Fresh == true;
        bool calibrated = snapshot?.Calibrated == true;
        bool active = snapshot?.OutputActive == true;
        if (rateClock.Elapsed.TotalSeconds >= 1)
        {
            long count = snapshot?.Samples ?? 0;
            frequency = (count - previousCount) / rateClock.Elapsed.TotalSeconds;
            previousCount = count;
            rateClock.Restart();
        }

        var input = receiver?.Snapshot();
        var routing = router.Snapshot();
        networkStatus.Text = error ?? (input switch
        {
            { Error: { } failure } => $"Приём UDP остановлен · {failure}",
            { Listening: true, LastSender: { } sender } =>
                $"UDP {input.Port} · {sender.IpAddress}:{sender.Port}\n" +
                $"Сообщений: {input.Messages:N0} · Пакетов: {input.Packets:N0}\n" +
                $"Последний адрес: {input.LastAddress ?? "—"} · Ошибки пакетов: {input.MalformedPackets:N0}",
            { Listening: true } => $"Ожидаю OSC на UDP {input.Port}",
            _ => "Приём OSC выключен"
        });
        networkStatus.TextColor = error is not null || input?.Error is not null ? Warning
            : input?.LastSender is not null ? Accent : Muted;
        routingStatus.IsVisible = moduleRegistrationErrors is not null ||
            routing.DroppedDeliveries > 0 || routing.HandlerErrors > 0;
        routingStatus.TextColor = Warning;
        routingStatus.Text = $"Пропущено доставок: {routing.DroppedDeliveries:N0} · Ошибок модулей: {routing.HandlerErrors:N0}" +
            (routing.LastHandlerError is { } handlerError ? $"\n{handlerError}" : "") +
            (moduleRegistrationErrors is { } registrationErrors ? $"\nПодключение модулей:\n{registrationErrors}" : "");
        sampleStatus.Text = receiving
            ? $"/accxyz · {snapshot!.Sender} · ≈ {frequency:F0} Гц"
            : "Ожидаю /accxyz";
        vJoyStatus.Text = active ? $"●  Передача в vJoy №{selectedDevice} · ось X"
            : vJoyError ?? vJoyMessage ?? "Ожидание настройки";
        vJoyStatus.TextColor = active ? Accent : vJoyError is null ? Muted : Warning;
        vJoyLibrary.Text = vJoyScan?.DllPath ?? "";
        vJoyLibrary.IsVisible = vJoyScan is not null;
        vJoyDevices.Text = DescribeDevices(vJoyScan, active);
        vJoyDevices.IsVisible = vJoyScan?.DriverEnabled == true;
        angle.Text = snapshot?.Angle is { } value ? $"{value:+0.0;-0.0;0.0}°" : "—°";
        axisValue.Text = calibrated && receiving ? $"{snapshot!.Position * 100:+0;-0;0}%" : "—%";
        sensorValues.Text = receiving ? $"X {snapshot!.X:F2}     Y {snapshot.Y:F2}     Z {snapshot.Z:F2}" : "X —     Y —     Z —";
        string? issue = snapshot?.Notice;
        notice.Text = issue ?? (!wheelEnabled.IsToggled ? "Модуль выключен" :
            calibrated ? "Центр выставлен" : "Держи телефон как руль и выставь центр.");
        notice.TextColor = issue is null ? Muted : Warning;

        double position = snapshot?.Position ?? 0;
        if (gauge.Position != position || gauge.Active != active)
        {
            gauge.Position = position;
            gauge.Active = active;
            gaugeView.Invalidate();
        }
        port.IsEnabled = input?.Listening != true;
        wheelEnabled.IsEnabled = !working;
        device.IsEnabled = wheelEnabled.IsToggled && !active && !working;
        dllPath.IsEnabled = device.IsEnabled;
        prepare.IsEnabled = device.IsEnabled;
        listen.Text = input?.Listening == true ? "Остановить приём" : "Начать приём";
        calibrate.IsEnabled = receiving;
        drive.IsEnabled = active || receiving && calibrated && !working;
        drive.Text = active ? "Выключить руль" : "Включить руль";
    }

    private string DescribeDevices(VJoyScan? scan, bool active)
    {
        if (scan?.DriverEnabled != true)
            return "";

        var devices = scan.Devices.Where(item => item.State != VJoyDeviceState.Missing).Select(item =>
        {
            string status = item.State switch
            {
                VJoyDeviceState.Free when item.Id == selectedDevice && active => "используется",
                VJoyDeviceState.Free => "свободно при проверке",
                VJoyDeviceState.Owned or VJoyDeviceState.Busy => "занято при проверке",
                _ => "состояние неизвестно"
            };
            return $"№{item.Id} · {(item.HasXAxis ? "ось X" : "ось X недоступна")} · {status}";
        }).ToArray();
        return devices.Length == 0 ? "Виртуальные устройства отсутствуют" : string.Join(Environment.NewLine, devices);
    }

    private void ShowAddresses()
    {
        try
        {
            var addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                    item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(item => item.GetIPProperties().UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork &&
                                      !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(address => $"{address.Address} ({item.Name})"))
                .ToArray();

            localAddresses.Text = addresses.Length == 0
                ? "IPv4-адрес не найден"
                : "IPv4 ПК" + Environment.NewLine + string.Join(Environment.NewLine, addresses);
        }
        catch (NetworkInformationException)
        {
            localAddresses.Text = "IPv4 можно посмотреть в ipconfig.";
        }
    }

    private SteeringSettings CurrentSettings() =>
        new(Math.Round(lockDegrees.Value), Math.Round(deadZone.Value), Math.Round(smoothing.Value), invert.IsToggled);

    private void SaveSteeringSettings()
    {
        SteeringSettings value = CurrentSettings();
        session?.Configure(value);
        Preferences.Default.Set("LockDegrees", value.LockDegrees);
        Preferences.Default.Set("DeadZoneDegrees", value.DeadZoneDegrees);
        Preferences.Default.Set("SmoothingMs", value.SmoothingMs);
        Preferences.Default.Set("Invert", value.Invert);
    }

    private static bool TryNumber(string? text, int min, int max, out int number) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) &&
        number >= min && number <= max;

    private static Label Caption(string value = "") =>
        new() { Text = value, TextColor = Muted, FontSize = 14, VerticalOptions = LayoutOptions.Center };

    private static Label Heading(string value, double size) =>
        new() { Text = value, FontSize = size, FontAttributes = FontAttributes.Bold, TextColor = Text };

    private static Button MakeButton(string text, bool primary = false) =>
        new()
        {
            Text = text, TextColor = primary ? Canvas : Text, BackgroundColor = primary ? Accent : Outline,
            CornerRadius = 7, Padding = new Thickness(14, 8), MinimumHeightRequest = 36
        };

    private static Entry MakeEntry(Keyboard keyboard) =>
        new() { Keyboard = keyboard, TextColor = Text, MinimumHeightRequest = 36, FontSize = 14 };

    private static Slider MakeSlider(double min, double max) =>
        new() { Minimum = min, Maximum = max, MinimumTrackColor = Accent, MaximumTrackColor = Outline };

    private View SliderField(string text, Slider slider, string unit)
    {
        var value = Caption($"{slider.Value:F0}{unit}");
        slider.ValueChanged += (_, args) =>
        {
            value.Text = $"{args.NewValue:F0}{unit}";
            SaveSteeringSettings();
        };
        SemanticProperties.SetDescription(slider, text);
        return Row(Field(text, slider), value);
    }

    private static View Field(string caption, View control)
    {
        SemanticProperties.SetDescription(control, caption);
        return new VerticalStackLayout { Spacing = 2, Children = { Caption(caption), control } };
    }

    private static Grid Row(View start, View end)
    {
        var items = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        items.Add(start, 0, 0);
        items.Add(end, 1, 0);
        return items;
    }

    private static Border Card(View content) =>
        new()
        {
            BackgroundColor = Panel, Stroke = new SolidColorBrush(Outline), StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 }, Padding = new Thickness(16, 12),
            Content = content
        };

    private sealed class SteeringGauge : IDrawable
    {
        public double Position { get; set; }
        public bool Active { get; set; }

        public void Draw(ICanvas canvas, RectF area)
        {
            float left = area.Left + 8;
            float width = Math.Max(0, area.Width - 16);
            float middle = area.Center.Y;
            float center = left + width / 2;
            float point = center + (float)Position * width / 2;
            canvas.FillColor = Outline;
            canvas.FillRoundedRectangle(left, middle - 4, width, 8, 4);
            canvas.FillColor = Active ? Accent : Muted;
            canvas.FillRectangle(Math.Min(center, point), middle - 4, Math.Abs(point - center), 8);
            canvas.FillRectangle(center - 1, middle - 10, 2, 20);
            canvas.FillCircle(point, middle, 7);
        }
    }
}
