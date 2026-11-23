using System.Net;
using System.Net.Sockets;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC;

internal enum ClientAccessMode { Blacklist, Whitelist }

internal sealed record ClientAccessRule(string Address, bool Blocked = false, bool Allowed = false,
    int FeedbackPort = 9000, bool FeedbackEnabled = false)
{
    public bool IsAllowed(ClientAccessMode mode) => mode == ClientAccessMode.Whitelist ? Allowed : !Blocked;
}

internal sealed record ClientAccessSettings(ClientAccessMode Mode, bool BlockNew, ClientAccessRule[] Clients);

internal sealed class ClientAccessPolicy
{
    private readonly object gate = new();
    private readonly Dictionary<string, ClientAccessRule> clients = new(StringComparer.Ordinal);
    private ClientAccessMode mode;
    private bool blockNew;
    private long version;
    public long Version => Interlocked.Read(ref version);

    public void Load(ClientAccessSettings settings)
    {
        lock (gate)
        {
            mode = settings.Mode == ClientAccessMode.Whitelist ? ClientAccessMode.Whitelist : ClientAccessMode.Blacklist;
            blockNew = settings.BlockNew;
            foreach (ClientAccessRule rule in settings.Clients ?? [])
                if (Normalize(rule.Address) is { } address)
                {
                    bool validPort = rule.FeedbackPort is >= 1 and <= 65535;
                    clients.TryAdd(address, rule with
                    {
                        Address = address,
                        FeedbackPort = validPort ? rule.FeedbackPort : 9000,
                        FeedbackEnabled = validPort && rule.FeedbackEnabled
                    });
                }
            Interlocked.Increment(ref version);
        }
    }

    public ClientAccessSettings Snapshot()
    {
        lock (gate)
            return new(mode, blockNew, clients.Values.ToArray());
    }

    public bool Accept(OscSender sender)
    {
        lock (gate)
        {
            if (!clients.TryGetValue(sender.IpAddress, out var rule))
            {
                rule = new(sender.IpAddress, Blocked: blockNew);
                clients.Add(sender.IpAddress, rule);
                Interlocked.Increment(ref version);
            }
            return rule.IsAllowed(mode);
        }
    }

    public bool IsAllowed(string address)
    {
        lock (gate)
            return clients.TryGetValue(address, out var rule) && rule.IsAllowed(mode);
    }

    public bool SetAllowed(string address, bool allowed)
    {
        if (Normalize(address) is not { } ip)
            return false;

        lock (gate)
        {
            var rule = clients.GetValueOrDefault(ip) ?? new(ip);
            clients[ip] = mode == ClientAccessMode.Whitelist
                ? rule with { Allowed = allowed }
                : rule with { Blocked = !allowed };
            Interlocked.Increment(ref version);
            return true;
        }
    }

    public void Configure(ClientAccessMode newMode, bool blockNewClients)
    {
        lock (gate)
        {
            if (mode == newMode && blockNew == blockNewClients)
                return;
            mode = newMode;
            blockNew = blockNewClients;
            Interlocked.Increment(ref version);
        }
    }

    public void SetFeedback(string address, int port, bool enabled)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        lock (gate)
        {
            if (!clients.TryGetValue(address, out var rule) ||
                (rule.FeedbackPort == port && rule.FeedbackEnabled == enabled))
                return;
            clients[address] = rule with { FeedbackPort = port, FeedbackEnabled = enabled };
            Interlocked.Increment(ref version);
        }
    }

    private static string? Normalize(string? address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : null;
}
