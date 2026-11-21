using Microsoft.Maui.Hosting;
using Microsoft.Maui.LifecycleEvents;

namespace Laveyana.LavOSC;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<LavOscApplication>()
            .ConfigureFonts(fonts => fonts.AddFont("InterVariable.ttf", PulseVisuals.Font))
            .ConfigureLifecycleEvents(events => events.AddWindows(windows => windows
                .OnPlatformWindowSubclassed((window, _) => WindowsWindowChrome.Prepare(window))
                .OnWindowCreated(window =>
                {
                    string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon", "lavosc.ico");
                    if (File.Exists(iconPath))
                        window.AppWindow.SetIcon(iconPath);
                })));

        return builder.Build();
    }
}
