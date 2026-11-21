using System.Net;
using System.Net.Sockets;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC;

internal static class FeedbackDelivery
{
    public static async Task<string?> SendAsync(UdpClient udp, IReadOnlyList<IPEndPoint> recipients,
        IReadOnlyList<(string Name, IOscFeedbackSource Source)> sources, CancellationToken token)
    {
        string? error = null;
        foreach (var (name, source) in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (OscFeedback message in source.GetFeedback())
                {
                    byte[] packet = message.Encode(name);
                    foreach (IPEndPoint client in recipients)
                    {
                        try
                        {
                            await udp.SendAsync(packet.AsMemory(), client, token).ConfigureAwait(false);
                        }
                        catch (SocketException exception)
                        {
                            error ??= $"{client}: {exception.Message}";
                        }
                    }
                }
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                error ??= $"/{name}: {exception.Message}";
            }
        }
        return error;
    }
}
