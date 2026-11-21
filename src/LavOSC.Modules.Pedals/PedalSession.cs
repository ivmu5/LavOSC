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
    private const string ModuleName = "pedals";
    private const string ThrottleCommand = "throttle";
    private const string BrakeCommand = "brake";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TickPeriod = TimeSpan.FromMilliseconds(16);
    private static readonly IReadOnlyList<object?> Neutral = Array.AsReadOnly<object?>([0f]);
    private readonly object gate = new();
    private readonly Timer timer;
    private readonly Channel throttle = new();
    private readonly Channel brake = new();
    private long lastTick = Stopwatch.GetTimestamp();
    private IPedalOutput? output;
    private string? sender;
    private string? notice;
    private bool disposed;

    public static IReadOnlyList<OscEndpoint> OscEndpoints { get; } = Array.AsReadOnly<OscEndpoint>([
        new($"/{ModuleName}/{ThrottleCommand}", OscEndpointDirection.Receive,
            "Газ · один аргумент float32 или float64 в диапазоне 0–1.", $"/{ModuleName}/{ThrottleCommand} 0.65",
            $"Через {Timeout.TotalMilliseconds:0} мс без данных значение сбрасывается."),
        new($"/{ModuleName}/{BrakeCommand}", OscEndpointDirection.Receive,
            "Тормоз · один аргумент float32 или float64 в диапазоне 0–1.", $"/{ModuleName}/{BrakeCommand} 0.25",
            $"Через {Timeout.TotalMilliseconds:0} мс без данных значение сбрасывается."),
        new($"/{ModuleName}/{ThrottleCommand}", OscEndpointDirection.Send,
            "Текущее значение газа · один аргумент float32 в диапазоне 0–1."),
        new($"/{ModuleName}/{BrakeCommand}", OscEndpointDirection.Send,
            "Текущее значение тормоза · один аргумент float32 в диапазоне 0–1.")
    ]);

    public string Name => ModuleName;

    public PedalSession() => timer = new Timer(_ => Tick(), null, TickPeriod, TickPeriod);

    void IOscModule.Configure(OscModuleRegistration registration)
    {
        registration.OnCommand(ThrottleCommand, command => Receive(command, throttle));
        registration.OnCommand(BrakeCommand, command => Receive(command, brake));
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
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            return [throttle.Feedback(ThrottleCommand, sender, !disposed && throttle.IsFresh(now)),
                brake.Feedback(BrakeCommand, sender, !disposed && brake.IsFresh(now))];
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
            long now = Stopwatch.GetTimestamp();
            if (disposed || Stopwatch.GetElapsedTime(envelope.ReceivedTimestamp, now) >= Timeout ||
                (sender is not null && sender != envelope.Sender.IpAddress))
                return;

            if (output is not null && !throttle.IsFresh(now) && !brake.IsFresh(now))
                StopOutput("Сигнал потерян. После восстановления требуется повторное включение педалей.");

            sender ??= envelope.Sender.IpAddress;
            channel.SetTarget(next, envelope.ReceivedTimestamp);
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
        private bool targetFromClient;

        public double Position { get; private set; }

        public bool IsFresh(long now) => timestamp is { } received &&
            Stopwatch.GetElapsedTime(received, now) < Timeout;

        public OscFeedback Feedback(string name, string? sender, bool fresh) => new(name, [fresh ? (float)Position : 0f])
        {
            UseCommandAddress = true,
            InitialArguments = Neutral,
            Origin = sender is not null && timestamp is long received
                ? new(sender, received, [(float)target], SuppressEcho: fresh && targetFromClient)
                : null
        };

        public void SetTarget(double value, long received)
        {
            target = value;
            timestamp = received;
            targetFromClient = true;
        }

        public void ResetPosition()
        {
            if (Position != 0)
                targetFromClient = false;
            Position = 0;
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
