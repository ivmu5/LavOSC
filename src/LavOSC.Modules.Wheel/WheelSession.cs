using System.Diagnostics;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC.Modules.Wheel;

public interface ISteeringOutput : IDisposable
{
    bool TryWrite(double position);
}

public sealed record WheelSnapshot(string? Sender, long Samples, bool Fresh, bool Calibrated,
    bool OutputActive, double? Angle, double Position, float X, float Y, float Z, string? Notice);

public sealed class WheelSession : IOscModule, IOscFeedbackSource, IDisposable
{
    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Timer watchdog;
    private readonly SteeringEstimator steering = new();
    private SteeringSettings settings;
    private string? sender;
    private ISteeringOutput? output;
    private string? notice;
    private bool disposed;

    public string Name => "wheel";

    public WheelSession(SteeringSettings settings)
    {
        this.settings = settings.Normalize();
        watchdog = new Timer(_ => CheckTime(), null, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
    }

    void IOscModule.Configure(OscModuleRegistration registration)
    {
        registration.Subscribe("/accxyz", ReceiveAcceleration);
        registration.OnCommand("calibrate", ReceiveCalibration);
    }

    public WheelSnapshot Snapshot()
    {
        lock (gate)
        {
            bool fresh = !disposed && steering.IsFresh(Stopwatch.GetElapsedTime(started));
            var (x, y, z) = steering.Axes;
            return new(sender, steering.SampleCount, fresh, steering.IsCalibrated,
                output is not null, fresh ? steering.RelativeAngle : null,
                fresh ? steering.Position(settings) : 0, x, y, z, notice);
        }
    }

    public IReadOnlyList<OscFeedback> GetFeedback()
    {
        WheelSnapshot state = Snapshot();
        return [new("state", [(float)state.Position, (float)(state.Angle ?? 0),
            state.Fresh, state.Calibrated, state.OutputActive])];
    }

    public void Configure(SteeringSettings newSettings)
    {
        lock (gate)
            settings = newSettings.Normalize();
    }

    public bool Calibrate()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return CalibrateAt(Stopwatch.GetElapsedTime(started));
        }
    }

    private bool CalibrateAt(TimeSpan now)
    {
        if (!steering.Calibrate(now))
        {
            notice = "Калибровка не выполнена. Требуется стабильный сигнал акселерометра.";
            return false;
        }

        notice = null;
        WritePosition(0);
        return true;
    }

    public void EnableOutput(Func<ISteeringOutput> acquire)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (output is not null)
                return;
            if (!steering.IsCalibrated || !steering.IsFresh(Stopwatch.GetElapsedTime(started)))
                throw new InvalidOperationException("Для передачи в vJoy требуются сигнал акселерометра и калибровка.");

            output = acquire();
            notice = null;
            CheckTimeout(Stopwatch.GetElapsedTime(started));
            WritePosition(steering.Position(settings));
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
            watchdog.Dispose();
            ReleaseOutput();
        }
    }

    private void CheckTime()
    {
        lock (gate)
        {
            if (disposed)
                return;

            try
            {
                CheckTimeout(Stopwatch.GetElapsedTime(started));
            }
            catch (Exception error)
            {
                notice = $"Не удалось завершить передачу в vJoy. {error.Message}";
            }
        }
    }

    private void ReceiveAcceleration(OscEnvelope envelope)
    {
        if (envelope.Message.Types != ",fff" || envelope.Message.Arguments is not [float x, float y, float z])
            return;

        lock (gate)
        {
            if (disposed || (sender is not null && sender != envelope.Sender.IpAddress))
                return;

            TimeSpan now = Stopwatch.GetElapsedTime(started);
            CheckTimeout(now);
            TimeSpan received = Stopwatch.GetElapsedTime(started, envelope.ReceivedTimestamp);
            if (now - received >= SteeringEstimator.Timeout || !steering.Accept(x, y, z, received, settings))
                return;

            sender ??= envelope.Sender.IpAddress;
            WritePosition(steering.Position(settings));
        }
    }

    private void ReceiveCalibration(OscCommand command)
    {
        if (command.PathData.Count != 0 || !IsCalibrationTrigger(command.Envelope.Message))
            return;

        lock (gate)
        {
            if (disposed || sender is null || sender != command.Envelope.Sender.IpAddress)
                return;

            TimeSpan now = Stopwatch.GetElapsedTime(started);
            TimeSpan received = Stopwatch.GetElapsedTime(started, command.Envelope.ReceivedTimestamp);
            if (now - received < SteeringEstimator.Timeout)
                CalibrateAt(now);
        }
    }

    private static bool IsCalibrationTrigger(OscMessage message) =>
        message.Types == ",I" || message.Arguments is [] or [true] or [1] or [1L] or [1f] or [1d];

    private void CheckTimeout(TimeSpan now)
    {
        if (output is null || steering.IsFresh(now))
            return;

        ReleaseOutput();
        notice = "Сигнал потерян. После восстановления требуется повторное включение руля.";
    }

    private void WritePosition(double position)
    {
        if (output is null || output.TryWrite(position))
            return;

        ReleaseOutput();
        notice = "vJoy отклонил команду. Требуется повторное включение руля.";
    }

    private void ReleaseOutput()
    {
        ISteeringOutput? previous = output;
        output = null;
        if (previous is null)
            return;

        try
        {
            previous.TryWrite(0);
        }
        finally
        {
            previous.Dispose();
        }
    }
}
