using Laveyana.LavOSC.Core;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace Laveyana.LavOSC;

public sealed class LavOscApplication : Application
{
    private readonly IOscModule[] modules;

    public LavOscApplication(IEnumerable<IOscModule> modules)
    {
        this.modules = modules.ToArray();
        LegacyPreferences.Import();
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = new OscReceiverPage(modules);
        var window = new Window(page)
        {
            Title = "LavOSC",
            Width = 1020,
            Height = 820,
            MinimumWidth = 760,
            MinimumHeight = 520
        };
        window.Destroying += (_, _) => page.Close();
        return window;
    }
}
