using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Laveyana.LavOSC.Core;

public sealed record OscReceiverSnapshot(int Port, bool Listening, long Packets, long Messages,
    long MalformedPackets, OscSender? LastSender, string? LastAddress, string? Error);

public sealed class OscUdpReceiver : IDisposable
{
    private readonly object gate = new();
    private readonly OscRouter router;
    private readonly UdpClient listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<string> clientAddresses = new(StringComparer.Ordinal);
    private OscReceiverSnapshot snapshot;

    public OscUdpReceiver(int port, OscRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        this.router = router;
        listener = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            listener.ExclusiveAddressUse = true;
            listener.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            snapshot = new(((IPEndPoint)listener.Client.LocalEndPoint!).Port, true, 0, 0, 0, null, null, null);
        }
        catch
        {
            listener.Dispose();
            stopping.Dispose();
            throw;
        }

        _ = Task.Run(ReceiveAsync);
    }

    public OscReceiverSnapshot Snapshot()
    {
        lock (gate)
            return snapshot;
    }

    public string[] GetClientAddresses()
    {
        lock (gate)
            return [.. clientAddresses];
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!snapshot.Listening)
                return;

            snapshot = snapshot with { Listening = false };
            stopping.Cancel();
            listener.Dispose();
        }
    }

    private async Task ReceiveAsync()
    {
        CancellationToken token = stopping.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await listener.ReceiveAsync(token).ConfigureAwait(false);
                long received = Stopwatch.GetTimestamp();
                var sender = new OscSender(packet.RemoteEndPoint.Address.ToString(), packet.RemoteEndPoint.Port);
                List<OscMessage>? messages = null;
                try
                {
                    messages = OscReader.Read(packet.Buffer);
                }
                catch (Exception error) when (error is FormatException or DecoderFallbackException)
                {
                }

                lock (gate)
                {
                    if (!snapshot.Listening)
                        break;

                    snapshot = snapshot with
                    {
                        Packets = snapshot.Packets + 1,
                        Messages = snapshot.Messages + (messages?.Count ?? 0),
                        MalformedPackets = snapshot.MalformedPackets + (messages is null ? 1 : 0),
                        LastSender = sender,
                        LastAddress = messages is { Count: > 0 } ? messages[^1].Address : snapshot.LastAddress
                    };
                    if (messages is { Count: > 0 })
                    {
                        clientAddresses.Add(sender.IpAddress);
                        foreach (var message in messages)
                            router.Dispatch(new(message, sender, received));
                    }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            lock (gate)
                if (snapshot.Listening)
                    snapshot = snapshot with { Error = error.Message };
        }
        finally
        {
            lock (gate)
            {
                snapshot = snapshot with { Listening = false };
                listener.Dispose();
                stopping.Dispose();
            }
        }
    }
}
