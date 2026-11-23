using System.Net;
using System.Net.Sockets;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC;

internal sealed record FeedbackStatus(int Recipients, bool HasSources, long PacketsSent, string? Failure);

internal sealed class FeedbackDelivery : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private readonly object gate = new();
    private readonly AutoResetEvent changed = new(false);
    private readonly Dictionary<IPEndPoint, Recipient> recipients = [];
    private readonly Action<IPEndPoint, int?, string, IReadOnlyList<object?>> onSent;
    private Configuration configuration = new([], []);
    private FeedbackStatus status = new(0, false, 0, null);
    private bool disposed;

    public FeedbackDelivery(Action<IPEndPoint, int?, string, IReadOnlyList<object?>> onSent)
    {
        this.onSent = onSent;
        new Thread(Run) { IsBackground = true, Name = "LavOSC feedback" }.Start();
    }

    public FeedbackStatus Snapshot() => Volatile.Read(ref status);

    public void Configure(IPEndPoint[] endpoints, (string Name, IOscFeedbackSource Source)[] sources)
    {
        lock (gate)
        {
            if (disposed)
                return;

            Volatile.Write(ref configuration, new(endpoints, sources));
            changed.Set();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            Volatile.Write(ref disposed, true);
            changed.Set();
        }
    }

    private void Run()
    {
        Socket? socket = null;
        Configuration? previous = null;
        long packetsSent = 0;
        try
        {
            while (!Volatile.Read(ref disposed))
            {
                Configuration current = Volatile.Read(ref configuration);
                if (!ReferenceEquals(previous, current))
                {
                    var enabled = current.Endpoints.ToHashSet();
                    foreach (IPEndPoint endpoint in recipients.Keys.Where(endpoint => !enabled.Contains(endpoint)).ToArray())
                        recipients.Remove(endpoint);
                    foreach (IPEndPoint endpoint in enabled)
                        if (!recipients.ContainsKey(endpoint))
                            recipients.Add(endpoint, new(endpoint));
                    previous = current;
                }

                FeedbackStatus next = new(recipients.Count, current.Sources.Length > 0, packetsSent, null);
                if (next is { Recipients: > 0, HasSources: true })
                {
                    try
                    {
                        socket ??= new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
                        var (sent, failure) = SendChanges(socket, current);
                        packetsSent += sent;
                        next = next with { PacketsSent = packetsSent, Failure = failure };
                    }
                    catch (SocketException error)
                    {
                        next = next with { Failure = error.Message };
                    }
                }
                if (IsCurrent(current))
                    Volatile.Write(ref status, next);
                changed.WaitOne(next is { Recipients: > 0, HasSources: true } ? Interval : Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            socket?.Dispose();
            lock (gate)
            {
                disposed = true;
                changed.Dispose();
            }
        }
    }

    private (int Sent, string? Failure) SendChanges(Socket socket, Configuration current)
    {
        string? failure = null;
        int sent = 0;
        foreach (var (name, source) in current.Sources)
        {
            if (!IsCurrent(current))
                break;

            try
            {
                foreach (OscFeedback message in source.GetFeedback())
                {
                    string address = message.GetAddress(name);
                    byte[]? packet = null;
                    object?[]? arguments = null;
                    foreach ((IPEndPoint endpoint, Recipient recipient) in recipients)
                    {
                        if (!IsCurrent(current))
                            return (sent, failure);
                        KnownValue known = recipient.GetValue(address, message.InitialArguments);
                        if (!known.NeedsUpdate(recipient.Address, message))
                            continue;

                        packet ??= message.Encode(name);
                        try
                        {
                            socket.SendTo(packet, SocketFlags.None, endpoint);
                            known.Arguments = arguments ??= message.Arguments.ToArray();
                            sent++;
                            onSent(endpoint, (socket.LocalEndPoint as IPEndPoint)?.Port, address, known.Arguments);
                        }
                        catch (SocketException error)
                        {
                            failure ??= $"{endpoint}: {error.Message}";
                        }
                    }
                }
            }
            catch (Exception error)
            {
                failure ??= $"/{name}: {error.Message}";
            }
        }
        return (sent, failure);
    }

    private bool IsCurrent(Configuration current) =>
        !Volatile.Read(ref disposed) && ReferenceEquals(current, Volatile.Read(ref configuration));

    private sealed record Configuration(IPEndPoint[] Endpoints, (string Name, IOscFeedbackSource Source)[] Sources);

    private sealed class Recipient(IPEndPoint endpoint)
    {
        private readonly Dictionary<string, KnownValue> values = new(StringComparer.Ordinal);

        public string Address { get; } = endpoint.Address.ToString();

        public KnownValue GetValue(string address, IReadOnlyList<object?>? initial)
        {
            if (!values.TryGetValue(address, out KnownValue? value))
                values.Add(address, value = new(initial));
            return value;
        }
    }

    private sealed class KnownValue(IReadOnlyList<object?>? initial)
    {
        private OscFeedbackOrigin? lastInput;

        public IReadOnlyList<object?>? Arguments { get; set; } = initial?.ToArray();

        public bool NeedsUpdate(string address, OscFeedback message)
        {
            if (message.Origin is { } input && input.IpAddress == address)
            {
                if (lastInput is null || lastInput.Timestamp != input.Timestamp ||
                    !lastInput.Arguments.SequenceEqual(input.Arguments))
                {
                    Arguments = input.Arguments.ToArray();
                    lastInput = input with { Arguments = Arguments };
                }
                if (input.SuppressEcho)
                    return false;
            }
            return Arguments is null || !Arguments.SequenceEqual(message.Arguments);
        }
    }
}
