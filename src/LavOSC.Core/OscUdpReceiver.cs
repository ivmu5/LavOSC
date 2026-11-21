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
    private readonly Action<OscEnvelope, int>? onMessageReceived;
    private readonly UdpClient listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly HashSet<string> clientAddresses = new(StringComparer.Ordinal);
    private OscReceiverSnapshot snapshot;

    public OscUdpReceiver(int port, OscRouter router) : this(port, router, null)
    {
    }

    /// <summary>The optional observer runs on the receive loop and receives the local UDP port.</summary>
    public OscUdpReceiver(int port, OscRouter router, Action<OscEnvelope, int>? onMessageReceived)
    {
        ArgumentNullException.ThrowIfNull(router);
        this.router = router;
        this.onMessageReceived = onMessageReceived;
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

                int localPort;
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
                    localPort = snapshot.Port;
                    if (messages is { Count: > 0 })
                    {
                        clientAddresses.Add(sender.IpAddress);
                        foreach (OscMessage message in messages)
                            router.Dispatch(new(message, sender, received));
                    }
                }

                if (messages is null || onMessageReceived is null)
                    continue;
                foreach (OscMessage message in messages)
                {
                    try
                    {
                        onMessageReceived(new(message, sender, received), localPort);
                    }
                    catch (Exception error)
                    {
                        Trace.TraceWarning("OSC receive observer failed: {0}", error.Message);
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
