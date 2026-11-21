using Laveyana.LavOSC.Core;
using Laveyana.LavOSC.Modules.Pedals;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly List<IDisposable> extensionRegistrations = [];
    private readonly Switch pedalsEnabled = new() { OnColor = Accent };
    private readonly Button pedalDrive = MakeButton("Включить педали", primary: true);
    private readonly ProgressBar throttleBar = new() { ProgressColor = Accent };
    private readonly ProgressBar brakeBar = new() { ProgressColor = Warning };
    private readonly Label throttleValue = Caption();
    private readonly Label brakeValue = Caption();
    private readonly Label pedalStatus = Caption();
    private IDisposable? pedalRegistration;
    private PedalSession? pedals;

    private void ConfigurePedalControls(IPreferences preferences)
    {
        pedalsEnabled.IsToggled = preferences.Get("PedalsModuleEnabled", false);
        pedalsEnabled.Toggled += (_, args) =>
        {
            preferences.Set("PedalsModuleEnabled", args.Value);
            if (!args.Value)
                DetachPedals();
            else if (receiver?.Snapshot().Listening == true)
                AttachPedals();
            Refresh();
        };

        pedalDrive.Clicked += async (_, _) =>
        {
            if (pedals?.Snapshot().OutputActive == true)
                pedals.DisableOutput();
            else
                await PrepareVJoyAsync(arm: true, VJoyAxes.Pedals);
            Refresh();
        };
    }

    private View MakePedalControls() => Card(new VerticalStackLayout
    {
        Spacing = 8,
        Children =
        {
            Heading("Педали · /pedals", 18),
            Row(Caption("Модуль включён"), pedalsEnabled),
            pedalDrive
        }
    });

    private View MakePedalDiagnostics() => Card(new VerticalStackLayout
    {
        Spacing = 7,
        Children =
        {
            Heading("Педали", 18),
            Row(Caption("Газ · Y"), throttleValue),
            throttleBar,
            Row(Caption("Тормоз · Z"), brakeValue),
            brakeBar,
            pedalStatus
        }
    });

    private void RefreshPedals(PedalSnapshot? snapshot)
    {
        bool fresh = snapshot is { ThrottleFresh: true } or { BrakeFresh: true };
        bool active = snapshot?.OutputActive == true;
        throttleBar.Progress = snapshot?.Throttle ?? 0;
        brakeBar.Progress = snapshot?.Brake ?? 0;
        throttleValue.Text = snapshot is { ThrottleFresh: true } ? $"{snapshot.Throttle:P0}" : "—";
        brakeValue.Text = snapshot is { BrakeFresh: true } ? $"{snapshot.Brake:P0}" : "—";
        pedalStatus.Text = snapshot?.Notice ?? (active ? $"Передача в vJoy №{pedalDeviceId}"
            : !pedalsEnabled.IsToggled ? "Модуль выключен"
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

    private View MakeExtensionModules(IReadOnlyList<IOscModule> modules)
    {
        var controls = new VerticalStackLayout { Spacing = 8 };
        controls.Children.Add(Heading("Дополнительные модули", 18));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (IOscModule module in modules)
        {
            try
            {
                string name = module.Name;
                if (name is "wheel" or "whell" or "pedals")
                    throw new InvalidOperationException($"Адрес /{name} закреплён за встроенным модулем.");
                if (!names.Add(name))
                    throw new InvalidOperationException($"Имя /{name} уже используется другим модулем.");

                var status = Caption();
                status.TextColor = Warning;
                var toggle = new Switch { OnColor = Accent };
                var preferences = Preferences.Default;
                string preference = $"OscModuleEnabled.{name}";
                IDisposable? registration = null;

                bool Enable()
                {
                    try
                    {
                        registration = router.Register(module);
                        extensionRegistrations.Add(registration);
                        status.IsVisible = false;
                        return true;
                    }
                    catch (Exception exception)
                    {
                        status.Text = exception.Message;
                        status.IsVisible = true;
                        return false;
                    }
                }

                status.IsVisible = false;
                toggle.IsToggled = preferences.Get(preference, true) && Enable();
                toggle.Toggled += (_, args) =>
                {
                    if (closed)
                        return;

                    if (args.Value && !Enable())
                    {
                        toggle.IsToggled = false;
                        return;
                    }

                    if (!args.Value && registration is not null)
                    {
                        registration.Dispose();
                        extensionRegistrations.Remove(registration);
                        registration = null;
                    }
                    preferences.Set(preference, args.Value);
                };

                controls.Children.Add(Row(Caption($"/{name}"), toggle));
                controls.Children.Add(status);
            }
            catch (Exception exception)
            {
                var status = Caption($"{module.GetType().Name}: {exception.Message}");
                status.TextColor = Warning;
                controls.Children.Add(status);
            }
        }
        return Card(controls);
    }

    private void CloseExtensionModules()
    {
        foreach (IDisposable registration in extensionRegistrations)
            registration.Dispose();
        extensionRegistrations.Clear();
    }
}
