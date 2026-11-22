using System.Diagnostics;
using System.Runtime.InteropServices;
using Laveyana.LavOSC.Modules.Pedals;
using Laveyana.LavOSC.Modules.Wheel;

namespace Laveyana.LavOSC;

internal enum VJoyDeviceState { Owned, Free, Busy, Missing, Unknown }

[Flags]
internal enum VJoyAxes { None = 0, X = 1, Y = 2, Z = 4, Pedals = Y | Z, All = X | Y | Z }

internal sealed record VJoyDevice(int Id, VJoyDeviceState State, VJoyAxes Axes, VJoyAxes AvailableAxes);
internal sealed record VJoyScan(string DllPath, bool DriverEnabled, VJoyDevice[] Devices);

internal sealed class VJoyOutput : ISteeringOutput, IPedalOutput
{
    private static readonly object DriverLock = new();
    private static readonly (VJoyAxes Axis, uint Usage)[] AxisUsages =
        [(VJoyAxes.X, 0x30), (VJoyAxes.Y, 0x31), (VJoyAxes.Z, 0x32)];
    private static readonly Dictionary<uint, VJoyAxes> LeasedAxes = new();
    private static VJoyApi? processApi;
    private static long peakWriteTicks;
    private readonly VJoyApi api;
    private readonly uint id;
    private readonly VJoyAxes axes;
    private readonly Dictionary<VJoyAxes, AxisRange> ranges;
    private bool disposed;

    private VJoyOutput(VJoyApi api, uint id, VJoyAxes axes, Dictionary<VJoyAxes, AxisRange> ranges)
    {
        this.api = api;
        this.id = id;
        this.axes = axes;
        this.ranges = ranges;
    }

    public static string FormatAxes(VJoyAxes axes) => string.Join(", ", AxisUsages
        .Where(entry => (axes & entry.Axis) != 0).Select(entry => entry.Axis.ToString()));

    public static TimeSpan PeakWriteTime => TimeSpan.FromTicks(Interlocked.Read(ref peakWriteTicks));

    public static VJoyScan Scan(string path)
    {
        lock (DriverLock)
        {
            VJoyApi? api = GetEnabledApi(path);
            if (api is null)
                return new(path, false, []);

            var devices = new VJoyDevice[16];
            for (int id = 1; id <= devices.Length; id++)
            {
                var deviceId = (uint)id;
                VJoyDeviceState state = api.Status(deviceId);
                VJoyAxes axes = VJoyAxes.None;
                if (state is VJoyDeviceState.Free or VJoyDeviceState.Busy or VJoyDeviceState.Owned)
                    foreach (var (axis, usage) in AxisUsages)
                        if (api.HasAxis(deviceId, usage) != 0)
                            axes |= axis;

                bool leased = LeasedAxes.TryGetValue(deviceId, out var used);
                var available = state switch
                {
                    VJoyDeviceState.Free when !leased => axes,
                    VJoyDeviceState.Owned when leased => axes & ~used,
                    _ => VJoyAxes.None
                };
                devices[id - 1] = new(id, state, axes, available);
            }
            return new(api.Path, true, devices);
        }
    }

    public static VJoyOutput Connect(uint id, string path, VJoyAxes axes)
    {
        lock (DriverLock)
        {
            VJoyApi api = GetEnabledApi(path) ??
                throw new InvalidOperationException("Драйвер vJoy недоступен.");

            if (axes is VJoyAxes.None || (axes & ~VJoyAxes.All) != 0)
                throw new ArgumentOutOfRangeException(nameof(axes));

            var state = api.Status(id);
            bool leased = LeasedAxes.TryGetValue(id, out var used);
            if (state == VJoyDeviceState.Missing)
                throw new InvalidOperationException($"Устройство vJoy №{id} отсутствует.");
            if ((leased ? state != VJoyDeviceState.Owned : state != VJoyDeviceState.Free) || (used & axes) != 0)
                throw new InvalidOperationException($"Устройство vJoy №{id} или оси {FormatAxes(axes)} заняты.");

            if (!leased && api.Acquire(id) == 0)
                throw new InvalidOperationException($"Не удалось открыть устройство vJoy №{id}.");

            try
            {
                var ranges = new Dictionary<VJoyAxes, AxisRange>();
                foreach (var (axis, usage) in AxisUsages)
                {
                    if ((axes & axis) == 0)
                        continue;
                    if (api.HasAxis(id, usage) == 0)
                        throw new InvalidOperationException($"Ось {axis} устройства vJoy №{id} недоступна. Проверьте настройки в Configure vJoy.");
                    if (api.AxisMin(id, usage, out int minimum) == 0 ||
                        api.AxisMax(id, usage, out int maximum) == 0 || maximum <= minimum)
                        throw new InvalidOperationException($"Не удалось прочитать диапазон оси {axis} в vJoy.");

                    ranges.Add(axis, new(usage, minimum, maximum));
                }

                LeasedAxes[id] = used | axes;
                return new VJoyOutput(api, id, axes, ranges);
            }
            catch
            {
                if (!leased)
                    api.Release(id);
                throw;
            }
        }
    }

    bool ISteeringOutput.TryWrite(double position)
    {
        long started = Stopwatch.GetTimestamp();
        bool written;
        lock (DriverLock)
            written = TryWriteAxis(VJoyAxes.X, (position + 1) / 2);
        TrackWrite(started);
        return written;
    }

    bool IPedalOutput.TryWrite(double throttle, double brake)
    {
        long started = Stopwatch.GetTimestamp();
        bool written;
        lock (DriverLock)
            written = TryWriteAxis(VJoyAxes.Y, throttle) & TryWriteAxis(VJoyAxes.Z, brake);
        TrackWrite(started);
        return written;
    }

    private bool TryWriteAxis(VJoyAxes axis, double fraction)
    {
        if (disposed || !double.IsFinite(fraction) || !ranges.TryGetValue(axis, out var range))
            return false;

        int value = (int)Math.Round(range.Minimum + Math.Clamp(fraction, 0, 1) * ((double)range.Maximum - range.Minimum));
        if (range.LastWritten == value)
            return true;
        if (api.WriteAxis(value, id, range.Usage) == 0)
            return false;
        range.LastWritten = value;
        return true;
    }

    private static void TrackWrite(long started)
    {
        long ticks = Stopwatch.GetElapsedTime(started).Ticks;
        long peak;
        do
        {
            peak = Interlocked.Read(ref peakWriteTicks);
        } while (ticks > peak && Interlocked.CompareExchange(ref peakWriteTicks, ticks, peak) != peak);
    }

    public void Dispose()
    {
        lock (DriverLock)
        {
            if (disposed)
                return;

            disposed = true;
            VJoyAxes remaining = LeasedAxes[id] & ~axes;
            if (remaining == VJoyAxes.None)
            {
                LeasedAxes.Remove(id);
                api.Release(id);
            }
            else
            {
                LeasedAxes[id] = remaining;
            }
        }
    }

    private static VJoyApi? GetEnabledApi(string path)
    {
        path = Path.GetFullPath(path);
        if (processApi is { } loaded)
        {
            if (!string.Equals(loaded.Path, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Для смены DLL vJoy требуется перезапуск приложения.");

            return loaded.Enabled() != 0 ? loaded : null;
        }

        nint library = OpenLibrary(path);
        try
        {
            var enabled = Load<DriverEnabled>(library, "vJoyEnabled");
            if (enabled() == 0)
                return null;

            var api = new VJoyApi(path, enabled,
                Load<GetStatus>(library, "GetVJDStatus"),
                Load<AxisExists>(library, "GetVJDAxisExist"),
                Load<AcquireDevice>(library, "AcquireVJD"),
                Load<ReleaseDevice>(library, "RelinquishVJD"),
                Load<GetAxisLimit>(library, "GetVJDAxisMin"),
                Load<GetAxisLimit>(library, "GetVJDAxisMax"),
                Load<SetAxis>(library, "SetAxis"));

            // GetVJDStatus and AcquireVJD can start a native window thread.
            // Leave the module loaded until the process exits, including after RelinquishVJD.
            processApi = api;
            library = 0;
            return api;
        }
        finally
        {
            if (library != 0)
                NativeLibrary.Free(library);
        }
    }

    private sealed record AxisRange(uint Usage, int Minimum, int Maximum)
    {
        public int? LastWritten { get; set; }
    }

    private sealed record VJoyApi(string Path, DriverEnabled Enabled, GetStatus Status, AxisExists HasAxis,
        AcquireDevice Acquire, ReleaseDevice Release, GetAxisLimit AxisMin, GetAxisLimit AxisMax, SetAxis WriteAxis);

    private static nint OpenLibrary(string path)
    {
        try
        {
            return NativeLibrary.Load(path);
        }
        catch (BadImageFormatException exception)
        {
            throw new InvalidOperationException("Требуется 64-битная vJoyInterface.dll.", exception);
        }
        catch (DllNotFoundException exception)
        {
            throw new InvalidOperationException("Не удалось загрузить vJoyInterface.dll или её зависимости.", exception);
        }
    }

    private static T Load<T>(nint library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out nint entry))
            throw new InvalidOperationException($"В vJoyInterface.dll отсутствует функция {name}. Требуется DLL установленного драйвера.");
        return Marshal.GetDelegateForFunctionPointer<T>(entry);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DriverEnabled();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate VJoyDeviceState GetStatus(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AcquireDevice(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReleaseDevice(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AxisExists(uint id, uint axis);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetAxisLimit(uint id, uint axis, out int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetAxis(int value, uint id, uint axis);
}
