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
            Width = 1120,
            Height = 790,
            MinimumWidth = 720,
            MinimumHeight = 520
        };
        window.Created += (_, _) => WindowsWindowChrome.Attach(window, page);
        window.Destroying += (_, _) => page.Close();
        return window;
    }
}
