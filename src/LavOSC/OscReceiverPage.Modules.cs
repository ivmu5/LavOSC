using Laveyana.LavOSC.Core;
using Laveyana.LavOSC.Modules.Pedals;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly Button pedalDrive = MakeButton("Включить педали", primary: true);
    private readonly ProgressBar throttleBar = new() { ProgressColor = Accent };
    private readonly ProgressBar brakeBar = new() { ProgressColor = Warning };
    private readonly Label throttleValue = Caption();
    private readonly Label brakeValue = Caption();
    private readonly Label pedalStatus = Caption();
    private IDisposable? pedalRegistration;
    private PedalSession? pedals;

    private void ConfigurePedalControls()
    {
        pedalDrive.Clicked += async (_, _) =>
        {
            if (pedals?.Snapshot().OutputActive == true)
                pedals.DisableOutput();
            else
                await PrepareVJoyAsync(arm: true, VJoyAxes.Pedals);
            Refresh();
        };
    }

    private View MakePedalBody() => new VerticalStackLayout
    {
        Spacing = 7,
        Children =
        {
            Row(Caption("Газ · Y"), throttleValue),
            throttleBar,
            Row(Caption("Тормоз · Z"), brakeValue),
            brakeBar,
            pedalStatus,
            pedalDrive
        }
    };

    private void RefreshPedals(PedalSnapshot? snapshot)
    {
        bool fresh = snapshot is { ThrottleFresh: true } or { BrakeFresh: true };
        bool active = snapshot?.OutputActive == true;
        throttleBar.Progress = snapshot?.Throttle ?? 0;
        brakeBar.Progress = snapshot?.Brake ?? 0;
        throttleValue.Text = snapshot is { ThrottleFresh: true } ? $"{snapshot.Throttle:P0}" : "—";
        brakeValue.Text = snapshot is { BrakeFresh: true } ? $"{snapshot.Brake:P0}" : "—";
        pedalStatus.Text = snapshot?.Notice ?? (active ? $"Передача в vJoy №{pedalDeviceId}"
            : !pedalsModule.IsEnabled ? "Модуль выключен"
            : fresh ? $"Сигнал от {snapshot!.Sender} · можно включить педали" : "Ожидаю OSC на /pedals");
        pedalStatus.TextColor = snapshot?.Notice is not null ? Warning : active ? Accent : Muted;
        pedalDrive.IsEnabled = active || fresh && !working;
        pedalDrive.Text = active ? "Выключить педали" : "Включить педали";
    }

    private void AttachPedals()
    {
        var next = new PedalSession();
        try
        {
            pedalRegistration = router.Register(next);
            pedals = next;
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
        pedalDeviceId = null;
    }

    private void InitializeModules(IReadOnlyList<IOscModule> modules, IPreferences preferences)
    {
        wheelModule = new("wheel", "Руль", "WheelModuleEnabled",
            preferences.Get("WheelModuleEnabled", true), preferences.Get("OscModulePinned.wheel", true), supportsFeedback: true);
        pedalsModule = new("pedals", "Педали", "PedalsModuleEnabled",
            preferences.Get("PedalsModuleEnabled", false), preferences.Get("OscModulePinned.pedals", true), supportsFeedback: true);
        moduleEntries.Add(wheelModule);
        moduleEntries.Add(pedalsModule);

        var names = new HashSet<string>(StringComparer.Ordinal) { "wheel", "whell", "pedals" };
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
        foreach (ModuleEntry entry in moduleEntries)
            if (entry.SupportsFeedback)
                entry.SetFeedback(preferences.Get(entry.FeedbackKey, false));
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

    private async Task ToggleModuleAsync(ModuleEntry entry)
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
            armAfterCalibration = enabled && autoSetup.IsToggled;
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

        entry.SetEnabled(enabled);
        Preferences.Default.Set(entry.EnabledKey, enabled);
        Refresh();
        if (entry == wheelModule && armAfterCalibration)
            await PrepareVJoyAsync(arm: false);
    }

    private void CloseExtensionModules()
    {
        foreach (ModuleEntry entry in moduleEntries)
            entry.Registration?.Dispose();
    }
}
