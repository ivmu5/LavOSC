using Laveyana.LavOSC.Core;
using Laveyana.LavOSC.Modules.Pedals;
using Microsoft.Maui;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private const string WheelAlias = "whell";
    private readonly ProgressBar throttleBar = new() { ProgressColor = Accent, HeightRequest = 10 };
    private readonly ProgressBar brakeBar = new() { ProgressColor = Warning, HeightRequest = 10 };
    private readonly Label throttleValue = MetricValue(20);
    private readonly Label brakeValue = MetricValue(20);
    private readonly Label pedalStatus = Caption();
    private IDisposable? pedalRegistration;
    private PedalSession? pedals;

    private View MakePedalBody() => new VerticalStackLayout
    {
        Spacing = 10,
        Children =
        {
            PedalMeter("Газ", "Ось Y", throttleBar, throttleValue),
            PedalMeter("Тормоз", "Ось Z", brakeBar, brakeValue),
            pedalStatus
        }
    };

    private static View PedalMeter(string name, string axis, ProgressBar bar, Label value)
    {
        var label = new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { Heading(name, 14), Caption(axis) }
        };
        SemanticProperties.SetDescription(bar, name);
        return new Border
        {
            BackgroundColor = PulseVisuals.Recessed,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(10, 6),
            Content = new VerticalStackLayout { Spacing = 2, Children = { Row(label, value), bar } }
        };
    }

    private void RefreshPedals(PedalSnapshot? snapshot)
    {
        bool fresh = snapshot is { ThrottleFresh: true } or { BrakeFresh: true };
        bool active = snapshot?.OutputActive == true;
        throttleBar.Progress = snapshot?.Throttle ?? 0;
        brakeBar.Progress = snapshot?.Brake ?? 0;
        throttleValue.Text = snapshot is { ThrottleFresh: true } ? $"{snapshot.Throttle:P0}" : "—";
        brakeValue.Text = snapshot is { BrakeFresh: true } ? $"{snapshot.Brake:P0}" : "—";
        pedalStatus.IsVisible = pedalsModule.IsEnabled;
        pedalStatus.Text = snapshot?.Notice ?? (active ? (fresh ? $"Передача в vJoy №{pedalDeviceId}"
            : $"vJoy №{pedalDeviceId} · ожидание данных педалей")
            : fresh ? $"Источник сигнала · {snapshot!.Sender}" : "Нет сигнала педалей");
        pedalStatus.TextColor = snapshot?.Notice is not null ? Warning : active ? Accent : Muted;
    }

    private void AttachPedals()
    {
        var next = new PedalSession();
        try
        {
            pedalRegistration = router.Register(next);
            pedals = next;
            pedalsModule.SetError(null);
            pendingOutputs |= VJoyAxes.Pedals;
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    private void DetachPedals()
    {
        pedalRegistration?.Dispose();
        pedalRegistration = null;
        pedals?.Dispose();
        pedals = null;
        pendingOutputs &= ~VJoyAxes.Pedals;
        pedalDeviceId = null;
    }

    private void InitializeModules(IReadOnlyList<IOscModule> modules, IPreferences preferences)
    {
        wheelModule = new("wheel", "Руль", "WheelModuleEnabled",
            preferences.Get("WheelModuleEnabled", true), preferences.Get("OscModulePinned.wheel", true));
        pedalsModule = new("pedals", "Педали", "PedalsModuleEnabled",
            preferences.Get("PedalsModuleEnabled", false), preferences.Get("OscModulePinned.pedals", true));
        moduleEntries.Add(wheelModule);
        moduleEntries.Add(pedalsModule);

        var names = new HashSet<string>(StringComparer.Ordinal) { "wheel", WheelAlias, "pedals" };
        var extensions = new List<ModuleEntry>(modules.Count);
        foreach (IOscModule module in modules)
        {
            try
            {
                string name = module.Name;
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                    throw new InvalidOperationException($"Имя модуля «{name}» отсутствует или уже используется.");

                var entry = new ModuleEntry(name, name, $"OscModuleEnabled.{name}",
                    pinned: preferences.Get($"OscModulePinned.{name}", false), extension: module);
                if (preferences.Get(entry.EnabledKey, true))
                    TryEnableExtension(entry);
                extensions.Add(entry);
            }
            catch (Exception exception)
            {
                string name = module.GetType().Name;
                extensions.Add(new(name, name, "", validationError: exception.Message));
            }
        }
        moduleEntries.AddRange(extensions.OrderBy(module => module.Title, StringComparer.CurrentCultureIgnoreCase));
    }

    private bool TryEnableExtension(ModuleEntry entry)
    {
        try
        {
            entry.Registration = router.Register(entry.Extension!);
            entry.SetError(null);
            entry.SetEnabled(true);
            return true;
        }
        catch (Exception exception)
        {
            entry.SetError(exception.Message);
            return false;
        }
    }

    private void ToggleModule(ModuleEntry entry)
    {
        if (closed || !entry.Available)
            return;

        bool enabled = !entry.IsEnabled;
        bool listening = receiver?.Snapshot().Listening == true;
        if (entry == wheelModule)
        {
            if (enabled && listening)
                AttachWheel();
            else if (!enabled)
                DetachWheel();
        }
        else if (entry == pedalsModule)
        {
            if (enabled && listening)
                AttachPedals();
            else if (!enabled)
                DetachPedals();
        }
        else if (enabled)
        {
            if (!TryEnableExtension(entry))
                return;
        }
        else
        {
            entry.Registration?.Dispose();
            entry.Registration = null;
        }

        if (!enabled)
            entry.SetError(null);
        entry.SetEnabled(enabled);
        Preferences.Default.Set(entry.EnabledKey, enabled);
        Refresh();
    }

    private void ResetBlockedSources()
    {
        if (session?.Snapshot().Sender is { } wheelSource && !clientAccess.IsAllowed(wheelSource))
        {
            DetachWheel();
            AttachWheel();
        }
        if (pedals?.Snapshot().Sender is { } pedalSource && !clientAccess.IsAllowed(pedalSource))
        {
            DetachPedals();
            AttachPedals();
        }
    }

    private void CloseExtensionModules()
    {
        foreach (ModuleEntry entry in moduleEntries)
            entry.Registration?.Dispose();
    }
}
