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

        lock (client)
            return new(client.Received, client.Sent, client.Recent.ToArray());
    }

    private void Record(string ip, int remotePort, int? localPort, bool sent, string address, IReadOnlyList<object?> arguments)
    {
        var client = clients.GetOrAdd(ip, static _ => new ClientHistory());
        string values = string.Join(", ", arguments.Take(8).Select(FormatArgument));
        if (arguments.Count > 8)
            values += ", …";
        var entry = new ClientTrafficEntry(DateTimeOffset.Now, sent, remotePort, localPort, Limit(address), values);
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
        public Queue<ClientTrafficEntry> Recent { get; } = new(HistoryLimit);
    }
}
