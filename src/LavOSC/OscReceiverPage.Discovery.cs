using System.ComponentModel;
using Microsoft.Maui.Controls;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private readonly Label discoveryStatus = Caption();
    private OscHostAdvertisement? advertisement;
    private Task advertisementUpdate = Task.CompletedTask;
    private int advertisementVersion;

    private void AdvertiseHost(int? port)
    {
        int version = ++advertisementVersion;
        if (port is null)
            discoveryStatus.IsVisible = false;
        advertisementUpdate = UpdateAdvertisementAsync(advertisementUpdate, version, port);
    }

    private async Task UpdateAdvertisementAsync(Task previous, int version, int? port)
    {
        await previous;
        if (version != advertisementVersion)
            return;

        try
        {
            var old = advertisement;
            advertisement = null;
            if (old is not null)
                await old.DisposeAsync();
            if (version != advertisementVersion || closed)
                return;

            discoveryStatus.IsVisible = port.HasValue;
            if (port is not int listeningPort)
                return;

            discoveryStatus.TextColor = Muted;
            discoveryStatus.Text = "Регистрация Zeroconf…";
            advertisement = new OscHostAdvertisement(listeningPort);
            await advertisement.WaitUntilRegisteredAsync();
            if (version == advertisementVersion && !closed)
                discoveryStatus.Text = "Обнаружение Zeroconf активно.";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
            DllNotFoundException or EntryPointNotFoundException)
        {
            if (version == advertisementVersion && !closed)
            {
                discoveryStatus.IsVisible = port.HasValue;
                discoveryStatus.TextColor = Warning;
                discoveryStatus.Text = $"Zeroconf недоступен. Возможно подключение по IP. {exception.Message}";
            }
        }
    }
}
