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
    internal Dictionary<string, Action<OscCommand>> Commands { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Action<OscEnvelope>> Subscriptions { get; } = new(StringComparer.Ordinal);

    public void OnCommand(string name, Action<OscCommand> handler)
    {
        ValidateSegment(name, nameof(name));
        ArgumentNullException.ThrowIfNull(handler);
        Commands.Add(name, handler);
    }

    public void Subscribe(string address, Action<OscEnvelope> handler)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.StartsWith('/'))
            throw new ArgumentException("Адрес общего события должен начинаться с /.", nameof(address));

        ValidateSegment(address[1..], nameof(address));
        ArgumentNullException.ThrowIfNull(handler);
        Subscriptions.Add(address, handler);
    }

    internal static void ValidateSegment(string segment, string parameter)
    {
        ArgumentException.ThrowIfNullOrEmpty(segment, parameter);
        if (segment.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) ||
                                     "/#*,?[]{}".Contains(character)))
            throw new ArgumentException("Укажи один сегмент OSC без пробелов и шаблонов.", parameter);
    }
}

public sealed record OscRoutingSnapshot(long DroppedDeliveries, long HandlerErrors, string? LastHandlerError);

public sealed class OscRouter
{
    private readonly object gate = new();
    private readonly Dictionary<string, Route> modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Subscription>> subscriptions = new(StringComparer.Ordinal);
    private long droppedDeliveries;
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

            var route = new Route(this, name, names, registration.Commands);
            foreach (string routeName in names)
                modules.Add(routeName, route);
            foreach (var (address, handler) in registration.Subscriptions)
            {
                if (!subscriptions.TryGetValue(address, out var listeners))
                {
                    listeners = [];
                    subscriptions.Add(address, listeners);
                }
                listeners.Add(new(route, handler));
            }
            return route;
        }
    }

    public OscRoutingSnapshot Snapshot() =>
        new(Interlocked.Read(ref droppedDeliveries), Interlocked.Read(ref handlerErrors),
            Volatile.Read(ref lastHandlerError));

    internal void Dispatch(OscEnvelope envelope)
    {
        string address = envelope.Message.Address;
        lock (gate)
        {
            if (subscriptions.TryGetValue(address, out var listeners))
            {
                foreach (var subscription in listeners)
                    Enqueue(subscription.Route, new(envelope, subscription.Handler, null, null));
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

            string[] parts = next < 0 ? [] : address[(next + 1)..].Split('/');
            if (parts.Any(string.IsNullOrEmpty))
                return;

            var command = new OscCommand(route.Name, commandName, Array.AsReadOnly(parts), envelope);
            Enqueue(route, new(envelope, null, handler, command));
        }
    }

    private void Enqueue(Route route, Delivery delivery)
    {
        if (!route.Queue.Writer.TryWrite(delivery))
            Interlocked.Increment(ref droppedDeliveries);
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
            route.Queue.Writer.TryComplete();
        }
    }

    private sealed record Subscription(Route Route, Action<OscEnvelope> Handler);
    private readonly record struct Delivery(OscEnvelope Envelope, Action<OscEnvelope>? Subscriber,
        Action<OscCommand>? Handler, OscCommand? Command);

    private sealed class Route : IDisposable
    {
        private readonly OscRouter router;
        private int disposed;

        public string Name { get; }
        public string[] Names { get; }
        public Dictionary<string, Action<OscCommand>> Commands { get; }
        public Channel<Delivery> Queue { get; } = Channel.CreateBounded<Delivery>(new BoundedChannelOptions(64)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        public Route(OscRouter router, string name, string[] names, Dictionary<string, Action<OscCommand>> commands)
        {
            this.router = router;
            Name = name;
            Names = names;
            Commands = new(commands, StringComparer.Ordinal);
            _ = Task.Run(ProcessAsync);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                router.Unregister(this);
        }

        private async Task ProcessAsync()
        {
            await foreach (Delivery delivery in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (Volatile.Read(ref disposed) != 0)
                    break;

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
