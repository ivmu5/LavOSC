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
using Laveyana.LavOSC.Modules.Pedals;
using Laveyana.LavOSC.Modules.Wheel;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage : ContentPage
{
    private static readonly Color Canvas = PulseVisuals.Background;
    private static readonly Color Panel = PulseVisuals.Card;
    private static readonly Color Outline = PulseVisuals.Border;
    private static readonly Color Text = PulseVisuals.Text;
    private static readonly Color Muted = PulseVisuals.Muted;
    private static readonly Color Accent = PulseVisuals.Accent;
    private static readonly Color Warning = PulseVisuals.Warning;

    private readonly Entry port = MakeEntry(Keyboard.Numeric);
    private readonly Picker device = new() { TextColor = Text, FontFamily = PulseVisuals.Font, FontSize = 14 };
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
    private readonly Label angle = Heading("", 30);
    private readonly Label axisValue = Caption();
    private readonly Label notice = Caption();
    private readonly Switch invert = new() { OnColor = Accent };
    private readonly Switch autoSetup = new() { OnColor = Accent };
    private readonly Slider lockDegrees = MakeSlider(25, 110);
    private readonly Slider deadZone = MakeSlider(0, 10);
    private readonly Slider smoothing = MakeSlider(0, 150);
    private readonly SteeringGauge gauge = new();
    private readonly GraphicsView gaugeView = new() { HeightRequest = 40 };
    private readonly IDispatcherTimer timer;
    private readonly Stopwatch rateClock = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();
    private readonly OscRouter router = new();
    private OscUdpReceiver? receiver;
    private IDisposable? wheelRegistration;
    private WheelSession? session;
    private long previousCount;
    private double frequency;
    private string? error;
    private string? vJoyError;
    private string? vJoyMessage;
    private VJoyScan? vJoyScan;
    private int? preparedDeviceId;
    private int? wheelDeviceId;
    private int? pedalDeviceId;
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
        InitializeModules(modules, preferences);

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
        prepare.Clicked += async (_, _) => await PrepareVJoyAsync(arm: false,
            (wheelModule.IsEnabled ? VJoyAxes.X : VJoyAxes.None) |
            (pedalsModule.IsEnabled ? VJoyAxes.Pedals : VJoyAxes.None));
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

        ConfigurePedalControls();
        Content = workspace = MakeWorkspace();

        ShowAddresses();
        Refresh();
        timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += async (_, _) =>
        {
            Refresh();
            if (armAfterCalibration && !working && !closed &&
                session?.Snapshot() is { Fresh: true, Calibrated: true, OutputActive: false })
            {
                armAfterCalibration = false;
                await PrepareVJoyAsync(arm: true);
            }
        };
        timer.Start();
        StartFeedbackTimer();
        Loaded += async (_, _) =>
        {
            if (initialized || closed)
                return;

            initialized = true;
            ToggleListening();
            if (wheelModule.IsEnabled && autoSetup.IsToggled)
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
        feedbackTimer.Stop();
        lifetime.Cancel();
        feedbackSocket?.Dispose();
        if (!working)
            lifetime.Dispose();
        StopListening();
        CloseExtensionModules();
    }

    private void ToggleListening()
    {
        error = null;
        if (receiver?.Snapshot().Listening == true)
        {
            StopListening();
        }
        else if (!TryNumber(port.Text, 1, 65535, out int value))
        {
            error = "UDP-порт должен быть в диапазоне 1–65535.";
        }
        else
        {
            StopListening();
            try
            {
                if (wheelModule.IsEnabled)
                    AttachWheel();
                if (pedalsModule.IsEnabled)
                    AttachPedals();
                receiver = new OscUdpReceiver(value, router);
                AdvertiseHost(receiver.Snapshot().Port);
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
            wheelRegistration = router.Register(next, WheelAlias);
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
        DiscoverClients();
        receiver?.Dispose();
        receiver = null;
        AdvertiseHost(null);
        DetachWheel();
        DetachPedals();
    }

    private void DetachWheel()
    {
        wheelRegistration?.Dispose();
        wheelRegistration = null;
        session?.Dispose();
        session = null;
        wheelDeviceId = null;
        previousCount = 0;
        frequency = 0;
        rateClock.Restart();
    }

    private void Refresh()
    {
        WheelSnapshot? snapshot = session?.Snapshot();
        PedalSnapshot? pedalSnapshot = pedals?.Snapshot();
        bool receiving = snapshot?.Fresh == true;
        bool calibrated = snapshot?.Calibrated == true;
        bool active = snapshot?.OutputActive == true;
        bool pedalsActive = pedalSnapshot?.OutputActive == true;
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
            { Listening: true } => $"UDP {input.Port} · пакеты OSC не получены",
            _ => "Приём OSC отключён"
        });
        networkStatus.TextColor = error is not null || input?.Error is not null ? Warning
            : input?.LastSender is not null ? Accent : Muted;
        settingsNetworkStatus.Text = networkStatus.Text;
        settingsNetworkStatus.TextColor = networkStatus.TextColor;
        routingStatus.IsVisible = routing.DroppedDeliveries > 0 || routing.HandlerErrors > 0;
        routingStatus.TextColor = Warning;
        routingStatus.Text = $"Пропущено доставок: {routing.DroppedDeliveries:N0} · Ошибок модулей: {routing.HandlerErrors:N0}" +
            (routing.LastHandlerError is { } handlerError ? $"\n{handlerError}" : "");
        sampleStatus.IsVisible = wheelModule.IsEnabled;
        sampleStatus.Text = receiving
            ? $"/accxyz · {snapshot!.Sender} · ≈ {frequency:F0} Гц"
            : "Нет сигнала /accxyz";
        vJoyStatus.Text = vJoyError ?? vJoyMessage ?? "Ожидание настройки";
        vJoyStatus.TextColor = vJoyError is not null ? Warning : active || pedalsActive ? Accent : Muted;
        vJoyLibrary.Text = vJoyScan?.DllPath ?? "";
        vJoyLibrary.IsVisible = vJoyScan is not null;
        vJoyDevices.Text = DescribeDevices(vJoyScan, active, pedalsActive);
        vJoyDevices.IsVisible = vJoyScan?.DriverEnabled == true;
        angle.Text = snapshot?.Angle is { } value ? $"{value:+0.0;-0.0;0.0}°" : "—°";
        axisValue.Text = calibrated && receiving ? $"{snapshot!.Position * 100:+0;-0;0}%" : "—%";
        sensorValues.Text = receiving ? $"X {snapshot!.X:F2}     Y {snapshot.Y:F2}     Z {snapshot.Z:F2}" : "X —     Y —     Z —";
        string? issue = snapshot?.Notice;
        notice.IsVisible = issue is not null || receiving && !calibrated;
        notice.Text = issue ?? "Требуется калибровка.";
        notice.TextColor = Warning;

        double position = snapshot?.Position ?? 0;
        if (gauge.Position != position || gauge.Active != active)
        {
            gauge.Position = position;
            gauge.Active = active;
            gaugeView.Invalidate();
        }
        port.IsEnabled = input?.Listening != true;
        device.IsEnabled = !active && !pedalsActive && !working;
        dllPath.IsEnabled = device.IsEnabled;
        prepare.IsEnabled = device.IsEnabled && (wheelModule.IsEnabled || pedalsModule.IsEnabled);
        listen.Text = input?.Listening == true ? "Остановить приём" : "Начать приём";
        settingsListen.Text = listen.Text;
        calibrate.IsEnabled = receiving;
        drive.IsEnabled = active || receiving && calibrated && !working;
        drive.Text = active ? "Выключить руль" : "Включить руль";
        RefreshPedals(pedalSnapshot);
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
        new() { Text = value, TextColor = Muted, FontFamily = PulseVisuals.Font, FontSize = 13, VerticalOptions = LayoutOptions.Center };

    private static Label Heading(string value, double size) =>
        new() { Text = value, FontSize = size, FontFamily = PulseVisuals.Font, FontAttributes = FontAttributes.Bold, TextColor = Text };

    private static Button MakeButton(string text, bool primary = false) =>
        new()
        {
            Text = text, TextColor = primary ? Canvas : Text, BackgroundColor = primary ? Accent : PulseVisuals.Button,
            FontFamily = PulseVisuals.Font, FontSize = 12, CornerRadius = 9,
            Padding = new Thickness(10, 8), MinimumHeightRequest = 36
        };

    private static Entry MakeEntry(Keyboard keyboard) =>
        new() { Keyboard = keyboard, TextColor = Text, MinimumHeightRequest = 36, FontSize = 14, FontFamily = PulseVisuals.Font };

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
            BackgroundColor = Panel, Stroke = new SolidColorBrush(Outline.WithAlpha(.32f)), StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 }, Padding = new Thickness(14, 12),
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
