using System.Runtime.InteropServices;
using Laveyana.LavOSC.Modules.Wheel;

namespace Laveyana.LavOSC;

internal enum VJoyDeviceState { Owned, Free, Busy, Missing, Unknown }

internal sealed record VJoyDevice(int Id, VJoyDeviceState State, bool HasXAxis);
internal sealed record VJoyScan(string DllPath, bool DriverEnabled, VJoyDevice[] Devices);

internal sealed class VJoyOutput : ISteeringOutput
{
    private const uint SteeringAxis = 0x30; // HID_USAGE_X
    private static readonly object DriverLock = new();
    private static VJoyApi? processApi;
    private readonly VJoyApi api;
    private readonly uint id;
    private readonly int minimum;
    private readonly int maximum;
    private bool disposed;

    private VJoyOutput(VJoyApi api, uint id, int minimum, int maximum)
    {
        this.api = api;
        this.id = id;
        this.minimum = minimum;
        this.maximum = maximum;
    }

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
                VJoyDeviceState state = api.Status((uint)id);
                bool hasAxis = state is (VJoyDeviceState.Free or VJoyDeviceState.Busy or VJoyDeviceState.Owned) &&
                    api.HasAxis((uint)id, SteeringAxis) != 0;
                devices[id - 1] = new(id, state, hasAxis);
            }
            return new(api.Path, true, devices);
        }
    }

    public static VJoyOutput Connect(uint id, string path)
    {
        lock (DriverLock)
        {
            VJoyApi api = GetEnabledApi(path) ??
                throw new InvalidOperationException("Драйвер vJoy недоступен. Нажми «Подготовить vJoy», чтобы проверить его состояние.");

            switch (api.Status(id))
            {
                case VJoyDeviceState.Free:
                    break;
                case VJoyDeviceState.Owned:
                case VJoyDeviceState.Busy:
                    throw new InvalidOperationException($"Устройство vJoy №{id} занято.");
                case VJoyDeviceState.Missing:
                    throw new InvalidOperationException($"Устройство vJoy №{id} отсутствует. Проверь его в Configure vJoy.");
                default:
                    throw new InvalidOperationException($"Не удалось определить состояние vJoy №{id}.");
            }

            if (api.Acquire(id) == 0)
                throw new InvalidOperationException($"Не удалось открыть устройство vJoy №{id}.");

            try
            {
                if (api.HasAxis(id, SteeringAxis) == 0)
                    throw new InvalidOperationException($"Включи ось X для устройства vJoy №{id} в Configure vJoy.");

                if (api.AxisMin(id, SteeringAxis, out int minimum) == 0 ||
                    api.AxisMax(id, SteeringAxis, out int maximum) == 0 || maximum <= minimum)
                    throw new InvalidOperationException("Не удалось прочитать диапазон оси X в vJoy.");

                return new VJoyOutput(api, id, minimum, maximum);
            }
            catch
            {
                api.Release(id);
                throw;
            }
        }
    }

    public bool TryWrite(double position)
    {
        lock (DriverLock)
        {
            if (disposed)
                return false;

            double fraction = (Math.Clamp(position, -1, 1) + 1) / 2;
            int value = (int)Math.Round(minimum + fraction * ((double)maximum - minimum));
            return api.WriteAxis(value, id, SteeringAxis) != 0;
        }
    }

    public void Dispose()
    {
        lock (DriverLock)
        {
            if (disposed)
                return;

            disposed = true;
            api.Release(id);
        }
    }

    private static VJoyApi? GetEnabledApi(string path)
    {
        path = Path.GetFullPath(path);
        if (processApi is { } loaded)
        {
            if (!string.Equals(loaded.Path, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Для смены DLL vJoy полностью закрой приложение и открой его снова.");

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
            throw new InvalidOperationException("Нужна 64-битная vJoyInterface.dll из папки x64.", exception);
        }
        catch (DllNotFoundException exception)
        {
            throw new InvalidOperationException("Не удалось загрузить vJoyInterface.dll или её зависимости.", exception);
        }
    }

    private static T Load<T>(nint library, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(library, name, out nint entry))
            throw new InvalidOperationException($"В vJoyInterface.dll отсутствует функция {name}. Укажи DLL установленного драйвера.");
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
