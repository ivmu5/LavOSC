using System.Diagnostics;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC.Modules.Pedals;

public interface IPedalOutput : IDisposable
{
    bool TryWrite(double throttle, double brake);
}

public sealed record PedalSnapshot(double Throttle, double Brake, bool ThrottleFresh, bool BrakeFresh,
    bool OutputActive, string? Sender, string? Notice);

public sealed class PedalSession : IOscModule, IDisposable
{
    private const string ModuleName = "pedals";
    private const string ThrottleCommand = "throttle";
    private const string BrakeCommand = "brake";
    private const string HeartbeatCommand = "heartbeat";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(16);
    private const double SmoothingMilliseconds = 15;
    private readonly object gate = new();
    private readonly Timer timer;
    private readonly Channel throttle = new();
    private readonly Channel brake = new();
    private long lastTick = Stopwatch.GetTimestamp();
    private long? lastInput;
    private bool usesHeartbeat;
    private IPedalOutput? output;
    private string? sender;
    private string? notice;
    private bool disposed;

    public static IReadOnlyList<OscEndpoint> OscEndpoints { get; } = Array.AsReadOnly<OscEndpoint>([
        new($"/{ModuleName}/{ThrottleCommand}", OscEndpointDirection.Receive,
            "Газ · один аргумент float32 или float64 в диапазоне 0–1.", $"/{ModuleName}/{ThrottleCommand} 0.65",
            $"Без heartbeat требуется повторять значение чаще одного раза в {Timeout.TotalMilliseconds:0} мс."),
        new($"/{ModuleName}/{BrakeCommand}", OscEndpointDirection.Receive,
            "Тормоз · один аргумент float32 или float64 в диапазоне 0–1.", $"/{ModuleName}/{BrakeCommand} 0.25",
            $"Без heartbeat требуется повторять значение чаще одного раза в {Timeout.TotalMilliseconds:0} мс."),
        new($"/{ModuleName}/{HeartbeatCommand}", OscEndpointDirection.Receive, "Без аргументов.",
            $"/{ModuleName}/{HeartbeatCommand}", "Периодически подтверждает связь. Между подтверждениями можно отправлять только изменившиеся значения.")
    ]);

    public string Name => ModuleName;

    public PedalSession() => timer = new Timer(_ => Tick(), null, TickPeriod, TickPeriod);

    void IOscModule.Configure(OscModuleRegistration registration)
    {
        registration.OnCommand(ThrottleCommand, command => Receive(command, throttle), latestOnly: true);
        registration.OnCommand(BrakeCommand, command => Receive(command, brake), latestOnly: true);
        registration.OnCommand(HeartbeatCommand, ReceiveHeartbeat, latestOnly: true);
    }

    public PedalSnapshot Snapshot()
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            bool throttleFresh = IsFresh(throttle, now);
            bool brakeFresh = IsFresh(brake, now);
            string? status = notice ?? (output is not null && usesHeartbeat && !HasRecentInput(now)
                ? "Ожидание связи с педалями. Передача в vJoy возобновится автоматически."
                : null);
            return new(throttleFresh ? throttle.Position : 0, brakeFresh ? brake.Position : 0,
                throttleFresh, brakeFresh, output is not null, sender, status);
        }
    }

    public void EnableOutput(Func<IPedalOutput> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (output is not null)
                return;

            if (sender is null)
                throw new InvalidOperationException("Для передачи в vJoy сначала отправь команду педалей.");

            var candidate = acquire();
            try
            {
                if (!candidate.TryWrite(0, 0))
                    throw new InvalidOperationException("vJoy не принял исходное положение педалей.");
            }
            catch
            {
                candidate.Dispose();
                throw;
            }

            throttle.ResetPosition();
            brake.ResetPosition();
            lastTick = Stopwatch.GetTimestamp();
            output = candidate;
            notice = null;
        }
    }

    public void DisableOutput()
    {
        lock (gate)
            ReleaseOutput();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;

            disposed = true;
            timer.Dispose();
            ReleaseOutput();
        }
    }

    private void Receive(OscCommand command, Channel channel)
    {
        var envelope = command.Envelope;
        var arguments = envelope.Message.Arguments;
        if (command.PathData.Count != 0 || arguments.Count != 1 || !TryReadPosition(arguments[0], out var next))
            return;

        lock (gate)
        {
            if (AcceptInput(envelope))
                channel.SetTarget(next, envelope.ReceivedTimestamp);
        }
    }

    private void ReceiveHeartbeat(OscCommand command)
    {
        if (command.PathData.Count != 0 || command.Envelope.Message.Arguments.Count != 0)
            return;

        lock (gate)
        {
            if (!AcceptInput(command.Envelope))
                return;
            if (!usesHeartbeat)
            {
                long now = Stopwatch.GetTimestamp();
                if (!throttle.IsFresh(now))
                    throttle.Invalidate();
                if (!brake.IsFresh(now))
                    brake.Invalidate();
                usesHeartbeat = true;
            }
        }
    }

    private bool AcceptInput(OscEnvelope envelope)
    {
        long now = Stopwatch.GetTimestamp();
        if (disposed || Stopwatch.GetElapsedTime(envelope.ReceivedTimestamp, now) >= Timeout ||
            (sender is not null && sender != envelope.Sender.IpAddress))
            return false;

        CheckInputTimeout(now);
        if (lastInput is long previous && envelope.ReceivedTimestamp < previous)
            return false;

        sender ??= envelope.Sender.IpAddress;
        lastInput = envelope.ReceivedTimestamp;
        return true;
    }

    private bool HasRecentInput(long now) =>
        lastInput is long received && Stopwatch.GetElapsedTime(received, now) < Timeout;

    private bool IsFresh(Channel channel, long now) => !disposed && channel.HasValue &&
        (usesHeartbeat ? HasRecentInput(now) : channel.IsFresh(now));

    private void CheckInputTimeout(long now)
    {
        if (!usesHeartbeat || lastInput is null || HasRecentInput(now))
            return;

        lastInput = null;
        throttle.Invalidate();
        brake.Invalidate();
    }

    private static bool TryReadPosition(object? value, out double position)
    {
        position = value switch
        {
            float number => number,
            double number => number,
            _ => double.NaN
        };
        return double.IsFinite(position) && position is >= 0 and <= 1;
    }

    private void Tick()
    {
        lock (gate)
        {
            if (disposed)
                return;

            long now = Stopwatch.GetTimestamp();
            double milliseconds = Stopwatch.GetElapsedTime(lastTick, now).TotalMilliseconds;
            lastTick = now;
            double ratio = 1 - Math.Exp(-milliseconds / SmoothingMilliseconds);
            CheckInputTimeout(now);
            bool throttleFresh = IsFresh(throttle, now);
            bool brakeFresh = IsFresh(brake, now);
            bool changed = throttle.Advance(throttleFresh, ratio) | brake.Advance(brakeFresh, ratio);

            if (output is null)
                return;

            try
            {
                if (changed && !output.TryWrite(throttle.Position, brake.Position))
                    StopOutput("vJoy отклонил команду. Перезапустите модуль «Педали».");
            }
            catch (Exception exception)
            {
                StopOutput($"Ошибка выхода педалей. {exception.Message}");
            }
        }
    }

    private void StopOutput(string message)
    {
        notice = message;
        try
        {
            ReleaseOutput();
        }
        catch (Exception exception)
        {
            notice = $"{message} {exception.Message}";
        }
    }

    private void ReleaseOutput()
    {
        var previous = output;
        output = null;
        if (previous is null)
            return;

        try
        {
            previous.TryWrite(0, 0);
        }
        finally
        {
            previous.Dispose();
        }
    }

    private sealed class Channel
    {
        private long? timestamp;
        private double target;

        public double Position { get; private set; }
        public bool HasValue { get; private set; }

        public bool IsFresh(long now) => timestamp is { } received &&
            Stopwatch.GetElapsedTime(received, now) < Timeout;

        public void SetTarget(double value, long received)
        {
            target = value;
            timestamp = received;
            HasValue = true;
        }

        public void Invalidate() => HasValue = false;

        public void ResetPosition() => Position = 0;

        public bool Advance(bool fresh, double ratio)
        {
            double next = fresh ? Position + (target - Position) * ratio : 0;
            if (fresh && Math.Abs(target - next) < 0.0005)
                next = target;

            bool changed = Position != next;
            Position = next;
            return changed;
        }
    }
}
