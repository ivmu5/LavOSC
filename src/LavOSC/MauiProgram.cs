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
            .ConfigureLifecycleEvents(events => events.AddWindows(windows => windows.OnWindowCreated(window =>
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon", "lavosc.ico");
                if (File.Exists(iconPath))
                    window.AppWindow.SetIcon(iconPath);
            })));

        return builder.Build();
    }
}
