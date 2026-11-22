using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Laveyana.LavOSC.Core;

public readonly record struct OscSender(string IpAddress, int Port);

public sealed record OscEnvelope(OscMessage Message, OscSender Sender, long ReceivedTimestamp);

public sealed record OscCommand(string Module, string Name, IReadOnlyList<string> PathData, OscEnvelope Envelope);

public interface IOscModule
{
    string Name { get; }
    void Configure(OscModuleRegistration registration);
}

public sealed class OscModuleRegistration
{
    internal Dictionary<string, (Action<OscCommand> Invoke, bool Latest)> Commands { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, (Action<OscEnvelope> Invoke, bool Latest)> Subscriptions { get; } = new(StringComparer.Ordinal);

    public void OnCommand(string name, Action<OscCommand> handler) => OnCommand(name, handler, latestOnly: false);

    /// <summary>For continuous values, replace a pending update from the same address and IP.</summary>
    public void OnCommand(string name, Action<OscCommand> handler, bool latestOnly)
    {
        ValidateSegment(name, nameof(name));
        ArgumentNullException.ThrowIfNull(handler);
        Commands.Add(name, (handler, latestOnly));
    }

    public void Subscribe(string address, Action<OscEnvelope> handler) => Subscribe(address, handler, latestOnly: false);

    /// <summary>For continuous values, replace a pending update from the same address and IP.</summary>
    public void Subscribe(string address, Action<OscEnvelope> handler, bool latestOnly)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.StartsWith('/'))
            throw new ArgumentException("Адрес общего события должен начинаться с /.", nameof(address));

        ValidateSegment(address[1..], nameof(address));
        ArgumentNullException.ThrowIfNull(handler);
        Subscriptions.Add(address, (handler, latestOnly));
    }

    internal static void ValidateSegment(string segment, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment, parameter);
        if (segment.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
                                     "/#*,?[]{}".Contains(character)))
            throw new ArgumentException("Требуется один сегмент OSC без пробелов и шаблонов.", parameter);
    }
}

public sealed record OscRoutingSnapshot(long DroppedDeliveries, long HandlerErrors, string? LastHandlerError)
{
    public long SupersededDeliveries { get; init; }
    public TimeSpan PeakQueueDelay { get; init; }
}

public sealed class OscRouter
{
    private readonly object gate = new();
    private readonly ConditionalWeakTable<IOscModule, object> deliveryGates = new();
    private readonly Dictionary<string, Route> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Subscription>> subscriptions = new(StringComparer.Ordinal);
    private long droppedDeliveries;
    private long supersededDeliveries;
    private long peakQueueTicks;
    private long handlerErrors;
    private string? lastHandlerError;

    public IDisposable Register(IOscModule module, params string[] aliases)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(aliases);
        string name = module.Name;
        string[] names = [name, .. aliases];
        foreach (string routeName in names)
            OscModuleRegistration.ValidateSegment(routeName, nameof(module));
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new ArgumentException("Имя модуля и его псевдонимы должны различаться.", nameof(aliases));

        var registration = new OscModuleRegistration();
        module.Configure(registration);
        lock (gate)
        {
            foreach (string routeName in names)
                if (modules.ContainsKey(routeName))
                    throw new InvalidOperationException($"Адрес /{routeName} уже зарегистрирован.");

            var route = new Route(this, name, names, registration.Commands, deliveryGates.GetOrCreateValue(module));
            foreach (string routeName in names)
                modules.Add(routeName, route);
            foreach (var (address, handler) in registration.Subscriptions)
            {
                if (!subscriptions.TryGetValue(address, out var listeners))
                {
                    listeners = [];
                    subscriptions.Add(address, listeners);
                }
                listeners.Add(new(route, handler.Invoke, handler.Latest));
            }
            return route;
        }
    }

    public OscRoutingSnapshot Snapshot() =>
        new(Interlocked.Read(ref droppedDeliveries), Interlocked.Read(ref handlerErrors), Volatile.Read(ref lastHandlerError))
        {
            SupersededDeliveries = Interlocked.Read(ref supersededDeliveries),
            PeakQueueDelay = TimeSpan.FromTicks(Interlocked.Read(ref peakQueueTicks))
        };

    internal void Dispatch(OscEnvelope envelope)
    {
        string address = envelope.Message.Address;
        lock (gate)
        {
            if (subscriptions.TryGetValue(address, out var listeners))
            {
                foreach (var subscription in listeners)
                    subscription.Route.Enqueue(new(envelope, subscription.Handler, null, null, subscription.Latest));
            }

            int separator = address.IndexOf('/', 1);
            if (separator <= 1 || separator == address.Length - 1)
                return;

            if (!modules.TryGetValue(address[1..separator], out var route))
                return;

            int next = address.IndexOf('/', separator + 1);
            string commandName = next < 0 ? address[(separator + 1)..] : address[(separator + 1)..next];
            if (!route.Commands.TryGetValue(commandName, out var handler))
                return;

            IReadOnlyList<string> parts = [];
            if (next >= 0)
            {
                string[] segments = address[(next + 1)..].Split('/');
                if (Array.Exists(segments, string.IsNullOrEmpty))
                    return;
                parts = Array.AsReadOnly(segments);
            }

            var command = new OscCommand(route.Name, commandName, parts, envelope);
            route.Enqueue(new(envelope, null, handler.Invoke, command, handler.Latest));
        }
    }

    private void TrackQueueDelay(long received)
    {
        long ticks = Stopwatch.GetElapsedTime(received).Ticks;
        long peak;
        do
        {
            peak = Interlocked.Read(ref peakQueueTicks);
        } while (ticks > peak && Interlocked.CompareExchange(ref peakQueueTicks, ticks, peak) != peak);
    }

    private void Unregister(Route route)
    {
        lock (gate)
        {
            if (!modules.TryGetValue(route.Name, out var registered) || !ReferenceEquals(registered, route))
                return;

            foreach (string name in route.Names)
                modules.Remove(name);
            foreach (var listeners in subscriptions.Values)
                listeners.RemoveAll(subscription => ReferenceEquals(subscription.Route, route));
            foreach (string address in subscriptions.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray())
                subscriptions.Remove(address);
            route.Complete();
        }
    }

    private sealed record Subscription(Route Route, Action<OscEnvelope> Handler, bool Latest);
    private readonly record struct Delivery(OscEnvelope Envelope, Action<OscEnvelope>? Subscriber,
        Action<OscCommand>? Handler, OscCommand? Command, bool Latest);

    private sealed class Route : IDisposable
    {
        private readonly OscRouter router;
        private readonly object deliveryGate;
        private const int QueueCapacity = 64;
        private readonly object queueGate = new();
        private readonly List<Delivery> queue = new();
        private readonly Channel<bool> ready = Channel.CreateBounded<bool>(1);
        private int disposed;

        public string Name { get; }
        public string[] Names { get; }
        public Dictionary<string, (Action<OscCommand> Invoke, bool Latest)> Commands { get; }

        public Route(OscRouter router, string name, string[] names,
            Dictionary<string, (Action<OscCommand> Invoke, bool Latest)> commands, object deliveryGate)
        {
            this.router = router;
            this.deliveryGate = deliveryGate;
            Name = name;
            Names = names;
            Commands = new(commands, StringComparer.Ordinal);
            _ = Task.Run(ProcessAsync);
        }

        public void Enqueue(Delivery delivery)
        {
            lock (queueGate)
            {
                if (delivery.Latest)
                {
                    // A discrete command is an ordering barrier. Move the replacement to the tail to keep arrival order.
                    for (int index = queue.Count - 1; index >= 0 && queue[index].Latest; index--)
                    {
                        var previous = queue[index].Envelope;
                        if (previous.Message.Address != delivery.Envelope.Message.Address ||
                            previous.Sender.IpAddress != delivery.Envelope.Sender.IpAddress)
                            continue;

                        queue.RemoveAt(index);
                        Interlocked.Increment(ref router.supersededDeliveries);
                        break;
                    }
                }
                if (queue.Count == QueueCapacity)
                {
                    Interlocked.Increment(ref router.droppedDeliveries);
                    return;
                }
                queue.Add(delivery);
                if (queue.Count == 1)
                    ready.Writer.TryWrite(true);
            }
        }

        public void Complete()
        {
            lock (queueGate)
            {
                queue.Clear();
                ready.Writer.TryComplete();
            }
        }

        private bool TryTake(out Delivery delivery)
        {
            lock (queueGate)
            {
                if (queue.Count == 0)
                {
                    delivery = default;
                    return false;
                }
                delivery = queue[0];
                queue.RemoveAt(0);
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                router.Unregister(this);
        }

        private async Task ProcessAsync()
        {
            await foreach (bool _ in ready.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                while (TryTake(out var delivery))
                {
                    lock (deliveryGate)
                    {
                        if (Volatile.Read(ref disposed) != 0)
                            return;

                        router.TrackQueueDelay(delivery.Envelope.ReceivedTimestamp);
                        try
                        {
                            if (delivery.Command is { } command)
                                delivery.Handler!(command);
                            else
                                delivery.Subscriber!(delivery.Envelope);
                        }
                        catch (Exception error)
                        {
                            Volatile.Write(ref router.lastHandlerError, $"{Name}: {error.Message}");
                            Interlocked.Increment(ref router.handlerErrors);
                        }
                    }
                }
            }
        }
    }
}
