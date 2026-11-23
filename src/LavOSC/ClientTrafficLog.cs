using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC;

internal sealed record ClientTrafficEntry(DateTimeOffset At, bool Sent, int RemotePort, int? LocalPort,
    string Address, string Arguments);

internal sealed record ClientTrafficSnapshot(long Received, long Sent, ClientTrafficEntry[] Recent);

internal sealed class ClientTrafficLog
{
    internal const int HistoryLimit = 100;
    private readonly ConcurrentDictionary<string, ClientHistory> clients = new(StringComparer.Ordinal);

    public void Received(OscEnvelope envelope, int port) => Record(envelope.Sender.IpAddress, envelope.Sender.Port,
        port, false, envelope.Message.Address, envelope.Message.Arguments);

    public void Sent(IPEndPoint endpoint, int? port, string address, IReadOnlyList<object?> arguments) =>
        Record(endpoint.Address.ToString(), endpoint.Port, port, true, address, arguments);

    public ClientTrafficSnapshot Snapshot(string ip)
    {
        if (!clients.TryGetValue(ip, out var client))
            return new(0, 0, []);

        long received;
        long sent;
        RawTraffic[] recent;
        lock (client)
        {
            received = client.Received;
            sent = client.Sent;
            recent = client.Recent.ToArray();
        }

        return new(received, sent, recent.Select(entry => new ClientTrafficEntry(entry.At.ToLocalTime(), entry.Sent,
            entry.RemotePort, entry.LocalPort, entry.Address, FormatArguments(entry.Arguments))).ToArray());
    }

    private void Record(string ip, int remotePort, int? localPort, bool sent, string address, IReadOnlyList<object?> arguments)
    {
        var client = clients.GetOrAdd(ip, static _ => new ClientHistory());
        var entry = new RawTraffic(DateTimeOffset.UtcNow, sent, remotePort, localPort, Limit(address), RetainArguments(arguments));
        lock (client)
        {
            if (sent)
                client.Sent++;
            else
                client.Received++;
            if (client.Recent.Count == HistoryLimit)
                client.Recent.Dequeue();
            client.Recent.Enqueue(entry);
        }
    }

    private static IReadOnlyList<object?> RetainArguments(IReadOnlyList<object?> arguments)
    {
        int count = Math.Min(arguments.Count, 8);
        for (int index = 0; index < count; index++)
        {
            if (arguments[index] is string { Length: > 100 })
                return CopyPreview(arguments, count);
        }
        return count == arguments.Count ? arguments : CopyPreview(arguments, count);
    }

    private static object?[] CopyPreview(IReadOnlyList<object?> arguments, int count)
    {
        var preview = new object?[arguments.Count > count ? count + 1 : count];
        for (int index = 0; index < count; index++)
            preview[index] = arguments[index] is string text ? Limit(text) : arguments[index];
        return preview;
    }

    private static string FormatArguments(IReadOnlyList<object?> arguments)
    {
        string values = string.Join(", ", arguments.Take(8).Select(FormatArgument));
        return arguments.Count > 8 ? values + ", …" : values;
    }

    private static string FormatArgument(object? value) => value switch
    {
        null => "nil",
        bool boolean => boolean ? "true" : "false",
        string text => $"\"{Limit(text)}\"",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => "…"
    };

    private static string Limit(string value) => (value.Length > 100 ? value[..100] + "…" : value)
        .Replace('\r', ' ').Replace('\n', ' ');

    private sealed class ClientHistory
    {
        public long Received;
        public long Sent;
        public Queue<RawTraffic> Recent { get; } = new(HistoryLimit);
    }

    private readonly record struct RawTraffic(DateTimeOffset At, bool Sent, int RemotePort, int? LocalPort,
        string Address, IReadOnlyList<object?> Arguments);
}
