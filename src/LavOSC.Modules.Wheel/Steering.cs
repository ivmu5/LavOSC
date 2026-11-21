namespace Laveyana.LavOSC.Modules.Wheel;

public sealed record SteeringSettings(double LockDegrees = 60, double DeadZoneDegrees = 2,
    double SmoothingMs = 55, bool Invert = false)
{
    public SteeringSettings Normalize() => new(
        double.IsFinite(LockDegrees) ? Math.Clamp(LockDegrees, 25, 110) : 60,
        double.IsFinite(DeadZoneDegrees) ? Math.Clamp(DeadZoneDegrees, 0, 10) : 2,
        double.IsFinite(SmoothingMs) ? Math.Clamp(SmoothingMs, 0, 150) : 55,
        Invert);
}

internal sealed class SteeringEstimator
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CalibrationWindow = TimeSpan.FromMilliseconds(700);
    private readonly Queue<(TimeSpan At, double Angle)> samples = new();
    private double? center;
    private double filteredAngle;

    public TimeSpan? LastSampleAt { get; private set; }
    public long SampleCount { get; private set; }
    public bool IsCalibrated => center.HasValue;
    public (float X, float Y, float Z) Axes { get; private set; }
    public double? RelativeAngle => center is { } origin ? Delta(filteredAngle, origin) : null;

    public bool Accept(float x, float y, float z, TimeSpan now, SteeringSettings settings)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            return false;

        Axes = (x, y, z);
        double planar = double.Hypot(x, y);
        double norm = double.Hypot(planar, z);
        if (norm < 1e-6 || planar < 0.35 * norm)
            return false;

        double angle = Math.Atan2(y, x) * 180 / Math.PI;
        if (!IsFresh(now))
        {
            samples.Clear();
            filteredAngle = angle;
        }
        else
        {
            double ms = (now - LastSampleAt!.Value).TotalMilliseconds;
            double weight = settings.SmoothingMs > 0 ? 1 - Math.Exp(-ms / settings.SmoothingMs) : 1;
            filteredAngle = Delta(filteredAngle + weight * Delta(angle, filteredAngle), 0);
        }

        LastSampleAt = now;
        SampleCount++;
        samples.Enqueue((now, angle));
        PruneCalibrationSamples(now);
        return true;
    }

    public bool IsFresh(TimeSpan now) => LastSampleAt is { } lastAt && now - lastAt < Timeout;

    public bool Calibrate(TimeSpan now)
    {
        PruneCalibrationSamples(now);
        if (!IsFresh(now) || samples.Count < 3 || now - samples.Peek().At < TimeSpan.FromMilliseconds(450))
            return false;

        double initialAngle = samples.Peek().Angle;
        double min = 0, max = 0, sum = 0;
        foreach (var sample in samples)
        {
            double relative = Delta(sample.Angle, initialAngle);
            min = Math.Min(relative, min);
            max = Math.Max(relative, max);
            sum += relative;
        }

        if (max - min > 5)
            return false;

        center = Delta(initialAngle + sum / samples.Count, 0);
        filteredAngle = center.Value;
        return true;
    }

    public double Position(SteeringSettings settings)
    {
        if (RelativeAngle is not { } angle || Math.Abs(angle) <= settings.DeadZoneDegrees)
            return 0;

        double magnitude = (Math.Abs(angle) - settings.DeadZoneDegrees) /
            (settings.LockDegrees - settings.DeadZoneDegrees);
        return Math.CopySign(Math.Min(1, magnitude), settings.Invert ? -angle : angle);
    }

    private void PruneCalibrationSamples(TimeSpan now)
    {
        while (samples.TryPeek(out var sample) && now - sample.At > CalibrationWindow)
            samples.Dequeue();
    }

    private static double Delta(double angle, double origin) => Math.IEEERemainder(angle - origin, 360);
}
