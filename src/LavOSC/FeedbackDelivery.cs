using System.Net;
using System.Net.Sockets;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC;

internal static class FeedbackDelivery
{
    public static async Task<(int PacketsSent, string? Error)> SendAsync(UdpClient udp, IReadOnlyList<IPEndPoint> recipients,
        IReadOnlyList<(string Name, IOscFeedbackSource Source)> sources, CancellationToken token,
        Action<IPEndPoint, int?, string, IReadOnlyList<object?>>? onSent = null)
    {
        string? error = null;
        int packetsSent = 0;
        foreach (var (name, source) in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                foreach (OscFeedback message in source.GetFeedback())
                {
                    byte[] packet = message.Encode(name);
                    string? address = onSent is null ? null : message.GetAddress(name);
                    foreach (IPEndPoint client in recipients)
                    {
                        try
                        {
                            await udp.SendAsync(packet.AsMemory(), client, token).ConfigureAwait(false);
                            packetsSent++;
                            onSent?.Invoke(client, (udp.Client.LocalEndPoint as IPEndPoint)?.Port, address!, message.Arguments);
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
        return (packetsSent, error);
    }
}
