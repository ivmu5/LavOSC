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

public sealed class WheelSession : IOscModule, IOscFeedbackSource, IDisposable
{
    private const string ModuleName = "wheel";
    private const string AccelerationAddress = "/accxyz";
    private const string CalibrateCommand = "calibrate";
    private const string FeedbackPosition = "position";
    private const string FeedbackAngle = "angle";
    private const string FeedbackFresh = "fresh";
    private const string FeedbackCalibrated = "calibrated";
    private const string FeedbackOutput = "output";

    public static IReadOnlyList<OscEndpoint> OscEndpoints { get; } = Array.AsReadOnly<OscEndpoint>([
        new(AccelerationAddress, OscEndpointDirection.Receive, "X, Y, Z · три конечных числа float32 (OSC ,fff).",
            $"{AccelerationAddress} 9.8 0.0 0.0"),
        new($"/{ModuleName}/{CalibrateCommand}", OscEndpointDirection.Receive,
            "Без аргументов, Impulse, bool true или число 1 (int32, int64, float32, float64). Значение 0 игнорируется.",
            $"/{ModuleName}/{CalibrateCommand} 1", "Калибровка принимается с того же IPv4, что и сигнал акселерометра."),
        new($"/{ModuleName}/feedback/{FeedbackPosition}", OscEndpointDirection.Send, "Положение от −1 до 1 (float32)."),
        new($"/{ModuleName}/feedback/{FeedbackAngle}", OscEndpointDirection.Send, "Угол в градусах (float32)."),
        new($"/{ModuleName}/feedback/{FeedbackFresh}", OscEndpointDirection.Send, "Свежий сигнал (bool)."),
        new($"/{ModuleName}/feedback/{FeedbackCalibrated}", OscEndpointDirection.Send, "Калибровка выполнена (bool)."),
        new($"/{ModuleName}/feedback/{FeedbackOutput}", OscEndpointDirection.Send, "Активный выход vJoy (bool).")
    ]);

    private readonly object gate = new();
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly Timer watchdog;
    private readonly SteeringEstimator steering = new();
    private SteeringSettings settings;
    private string? sender;
    private ISteeringOutput? output;
    private string? notice;
    private OscFeedbackOrigin? positionOrigin;
    private OscFeedbackOrigin? angleOrigin;
    private OscFeedbackOrigin? freshOrigin;
    private OscFeedbackOrigin? calibrationOrigin;
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

    public IReadOnlyList<OscFeedback> GetFeedback()
    {
        lock (gate)
        {
            bool fresh = !disposed && steering.IsFresh(Stopwatch.GetElapsedTime(started));
            var (position, angle) = ReadAxes(fresh);
            return [
                Feedback(FeedbackPosition, position, 0f, positionOrigin, fresh),
                Feedback(FeedbackAngle, angle, 0f, angleOrigin, fresh),
                Feedback(FeedbackFresh, fresh, false, freshOrigin, fresh),
                Feedback(FeedbackCalibrated, steering.IsCalibrated, false, calibrationOrigin),
                Feedback(FeedbackOutput, output is not null, false)
            ];
        }
    }

    public void Configure(SteeringSettings newSettings)
    {
        lock (gate)
        {
            float before = (float)steering.Position(settings);
            settings = newSettings.Normalize();
            positionOrigin = UpdateOrigin(positionOrigin, before, (float)steering.Position(settings), null);
        }
    }

    public bool Calibrate()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return CalibrateAt(Stopwatch.GetElapsedTime(started));
        }
    }

    private bool CalibrateAt(TimeSpan now, OscEnvelope? input = null)
    {
        bool wasCalibrated = steering.IsCalibrated;
        var before = ReadAxes(steering.IsFresh(now));
        if (!steering.Calibrate(now))
        {
            notice = "Калибровка не выполнена. Требуется стабильный сигнал акселерометра.";
            return false;
        }

        notice = null;
        UpdateAxesOrigins(before, ReadAxes(true), input);
        calibrationOrigin = UpdateOrigin(calibrationOrigin, wasCalibrated, true, input);
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
            bool wasFresh = steering.IsFresh(now);
            var before = ReadAxes(wasFresh);
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
            UpdateAxesOrigins(before, ReadAxes(true), envelope, force: !wasFresh);
            freshOrigin = UpdateOrigin(freshOrigin, wasFresh, true, envelope);
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
                CalibrateAt(now, command.Envelope);
        }
    }

    private (float Position, float Angle) ReadAxes(bool fresh) =>
        fresh ? ((float)steering.Position(settings), (float)(steering.RelativeAngle ?? 0)) : (0, 0);

    private void UpdateAxesOrigins((float Position, float Angle) before, (float Position, float Angle) after,
        OscEnvelope? input, bool force = false)
    {
        positionOrigin = UpdateOrigin(positionOrigin, before.Position, after.Position, input, force);
        angleOrigin = UpdateOrigin(angleOrigin, before.Angle, after.Angle, input, force);
    }

    private static OscFeedbackOrigin? UpdateOrigin<T>(OscFeedbackOrigin? previous, T before, T after,
        OscEnvelope? input, bool force = false) where T : IEquatable<T>
    {
        if (!force && before.Equals(after))
            return previous;
        if (input is not null)
            return new(input.Sender.IpAddress, input.ReceivedTimestamp, [after]);
        return previous is null ? null : previous with { SuppressEcho = false };
    }

    private static OscFeedback Feedback(string name, object value, object initial, OscFeedbackOrigin? origin = null,
        bool inputActive = true) => new(name, [value])
    {
        InitialArguments = [initial],
        Origin = origin is { SuppressEcho: true } && !inputActive ? origin with { SuppressEcho = false } : origin
    };

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
            notice = "vJoy отклонил команду. Требуется повторное включение руля.";
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
