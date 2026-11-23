using System.Diagnostics;
using Laveyana.LavOSC.Core;

namespace Laveyana.LavOSC.Modules.Wheel;

public interface ISteeringOutput : IDisposable
{
    bool TryWrite(double position);
}

public sealed record WheelSnapshot(string? Sender, long Samples, bool Fresh, bool Calibrated,
    bool OutputActive, double? Angle, double Position, float X, float Y, float Z, string? Notice)
{
    public long RejectedSensorSamples { get; init; }
}

public sealed class WheelSession : IOscModule, IDisposable
{
    private const string ModuleName = "wheel";
    private const string AccelerationAddress = "/accxyz";
    private const string CalibrateCommand = "calibrate";

    public static IReadOnlyList<OscEndpoint> OscEndpoints { get; } = Array.AsReadOnly<OscEndpoint>([
        new(AccelerationAddress, OscEndpointDirection.Receive, "X, Y, Z · три конечных числа float32 (OSC ,fff).",
            $"{AccelerationAddress} 9.8 0.0 0.0"),
        new($"/{ModuleName}/{CalibrateCommand}", OscEndpointDirection.Receive,
            "Без аргументов, Impulse, bool true или число 1 (int32, int64, float32, float64). Значение 0 игнорируется.",
            $"/{ModuleName}/{CalibrateCommand} 1", "Калибровка принимается с того же IPv4, что и сигнал акселерометра.")
    ]);

    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Timer watchdog;
    private readonly SteeringEstimator steering = new();
    private SteeringSettings settings;
    private string? sender;
    private ISteeringOutput? output;
    private string? notice;
    private bool disposed;
    private bool outputPaused;
    private long rejectedSensorSamples;

    public string Name => ModuleName;

    public WheelSession(SteeringSettings settings)
    {
        this.settings = settings.Normalize();
        watchdog = new Timer(_ => CheckTime(), null, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25));
    }

    void IOscModule.Configure(OscModuleRegistration registration)
    {
        registration.Subscribe(AccelerationAddress, ReceiveAcceleration, latestOnly: true);
        registration.OnCommand(CalibrateCommand, ReceiveCalibration);
    }

    public WheelSnapshot Snapshot()
    {
        lock (gate)
        {
            bool fresh = !disposed && steering.IsFresh(Stopwatch.GetElapsedTime(started));
            var (x, y, z) = steering.Axes;
            string? status = notice ?? (outputPaused
                ? "Ожидание /accxyz. Передача в vJoy возобновится автоматически."
                : null);
            return new(sender, steering.SampleCount, fresh, steering.IsCalibrated,
                output is not null, fresh ? steering.RelativeAngle : null,
                fresh ? steering.Position(settings) : 0, x, y, z, status)
            {
                RejectedSensorSamples = rejectedSensorSamples
            };
        }
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
            if (!steering.IsCalibrated)
                throw new InvalidOperationException("Для передачи в vJoy требуется калибровка.");

            output = acquire();
            notice = null;
            if (steering.IsFresh(Stopwatch.GetElapsedTime(started)))
                WritePosition(steering.Position(settings));
            else
                CheckTimeout(Stopwatch.GetElapsedTime(started));
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
            if (!disposed)
                CheckTimeout(Stopwatch.GetElapsedTime(started));
        }
    }

    private void ReceiveAcceleration(OscEnvelope envelope)
    {
        lock (gate)
        {
            if (disposed || (sender is not null && sender != envelope.Sender.IpAddress))
                return;

            TimeSpan now = Stopwatch.GetElapsedTime(started);
            CheckTimeout(now);
            TimeSpan received = Stopwatch.GetElapsedTime(started, envelope.ReceivedTimestamp);
            if (now - received >= SteeringEstimator.Timeout)
                return;
            if (envelope.Message.Types != ",fff" || envelope.Message.Arguments is not [float x, float y, float z] ||
                !steering.Accept(x, y, z, received, settings))
            {
                rejectedSensorSamples++;
                return;
            }

            sender ??= envelope.Sender.IpAddress;
            outputPaused = false;
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
        if (output is null || outputPaused || steering.IsFresh(now))
            return;

        WritePosition(0);
        outputPaused = output is not null;
    }

    private void WritePosition(double position)
    {
        if (output is null)
            return;

        try
        {
            if (output.TryWrite(position))
                return;
            notice = "vJoy отклонил команду. Перезапустите модуль «Руль».";
        }
        catch (Exception error)
        {
            notice = $"Ошибка выхода руля. {error.Message}";
        }

        try
        {
            ReleaseOutput();
        }
        catch (Exception error)
        {
            notice += $" {error.Message}";
        }
    }

    private void ReleaseOutput()
    {
        ISteeringOutput? previous = output;
        output = null;
        outputPaused = false;
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
