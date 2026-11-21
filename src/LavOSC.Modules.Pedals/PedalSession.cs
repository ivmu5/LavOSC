using System.Diagnostics;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC.Modules.Pedals;

public interface IPedalOutput : IDisposable
{
    bool TryWrite(double throttle, double brake);
}

public sealed record PedalSnapshot(double Throttle, double Brake, bool ThrottleFresh, bool BrakeFresh,
    bool OutputActive, string? Sender, string? Notice);

public sealed class PedalSession : IOscModule, IOscFeedbackSource, IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(16);
    private readonly object gate = new();
    private readonly Timer timer;
    private readonly Channel throttle = new();
    private readonly Channel brake = new();
    private long lastTick = Stopwatch.GetTimestamp();
    private IPedalOutput? output;
    private string? sender;
    private string? notice;
    private bool disposed;

    public string Name => "pedals";

    public PedalSession() => timer = new Timer(_ => Tick(), null, TickPeriod, TickPeriod);

    void IOscModule.Configure(OscModuleRegistration registration)
    {
        registration.OnCommand("state", command => Receive(command, throttle, brake));
        registration.OnCommand("throttle", command => Receive(command, throttle));
        registration.OnCommand("brake", command => Receive(command, brake));
    }

    public PedalSnapshot Snapshot()
    {
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            bool throttleFresh = !disposed && throttle.IsFresh(now);
            bool brakeFresh = !disposed && brake.IsFresh(now);
            return new(throttleFresh ? throttle.Position : 0, brakeFresh ? brake.Position : 0,
                throttleFresh, brakeFresh, output is not null, sender, notice);
        }
    }

    public IReadOnlyList<OscFeedback> GetFeedback()
    {
        PedalSnapshot state = Snapshot();
        return [new("state", [(float)state.Throttle, (float)state.Brake,
            state.ThrottleFresh, state.BrakeFresh, state.OutputActive])];
    }

    public void EnableOutput(Func<IPedalOutput> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (output is not null)
                return;

            long now = Stopwatch.GetTimestamp();
            if (!throttle.IsFresh(now) && !brake.IsFresh(now))
                throw new InvalidOperationException("Для передачи в vJoy требуется сигнал педалей.");

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

            throttle.Position = 0;
            brake.Position = 0;
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

    private void Receive(OscCommand command, Channel first, Channel? second = null)
    {
        var envelope = command.Envelope;
        var arguments = envelope.Message.Arguments;
        double nextSecond = 0;
        if (command.PathData.Count != 0 || arguments.Count != (second is null ? 1 : 2) ||
            !TryReadPosition(arguments[0], out var nextFirst) ||
            (second is not null && !TryReadPosition(arguments[1], out nextSecond)))
            return;

        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            if (disposed || Stopwatch.GetElapsedTime(envelope.ReceivedTimestamp, now) >= Timeout ||
                (sender is not null && sender != envelope.Sender.IpAddress))
                return;

            if (output is not null && !throttle.IsFresh(now) && !brake.IsFresh(now))
                StopOutput("Сигнал потерян. После восстановления требуется повторное включение педалей.");

            sender ??= envelope.Sender.IpAddress;
            first.SetTarget(nextFirst, envelope.ReceivedTimestamp);
            second?.SetTarget(nextSecond, envelope.ReceivedTimestamp);
        }
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
            double ratio = 1 - Math.Exp(-milliseconds / 35);
            bool throttleFresh = throttle.IsFresh(now);
            bool brakeFresh = brake.IsFresh(now);
            bool changed = throttle.Advance(throttleFresh, ratio) | brake.Advance(brakeFresh, ratio);

            if (output is null)
                return;

            try
            {
                if (!throttleFresh && !brakeFresh)
                    StopOutput("Сигнал потерян. После восстановления требуется повторное включение педалей.");
                else if (changed && !output.TryWrite(throttle.Position, brake.Position))
                    StopOutput("vJoy отклонил команду. Требуется повторное включение педалей.");
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

        public double Position { get; set; }

        public bool IsFresh(long now) => timestamp is { } received &&
            Stopwatch.GetElapsedTime(received, now) < Timeout;

        public void SetTarget(double value, long received)
        {
            target = value;
            timestamp = received;
        }

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
