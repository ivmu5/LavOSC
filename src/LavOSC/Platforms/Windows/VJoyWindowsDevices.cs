using System.Runtime.InteropServices;

namespace Laveyana.LavOSC;

internal sealed record VJoyWindowsDevice(string InstanceId, bool Started, uint? ProblemCode)
{
    public bool Disabled => ProblemCode == 22;
}

internal static class VJoyWindowsDevices
{
    private const uint FilterService = 0x00000002;
    private const uint DoNotGenerate = 0x10000040;
    private const uint FilterPresent = 0x00000100;
    private const uint DeviceFilters = FilterService | DoNotGenerate | FilterPresent;
    private const uint NoSuchDevNode = 0x0D;
    private const uint BufferSmall = 0x1A;
    private const uint NoSuchValue = 0x25;
    private const uint Started = 0x00000008;
    private const uint HasProblem = 0x00000400;

    public static VJoyWindowsDevice[] Read()
    {
        string[] ids = ReadInstanceIds("vjoy");
        var devices = new List<VJoyWindowsDevice>(ids.Length);
        foreach (string id in ids)
        {
            uint code = LocateDevice(out uint instance, id, 0);
            if (code == NoSuchDevNode)
                continue;
            Verify(code);

            code = ReadStatus(out uint status, out uint problem, instance, 0);
            if (code == NoSuchDevNode)
                continue;
            Verify(code);

            devices.Add(new(id, (status & Started) != 0, (status & HasProblem) != 0 ? problem : null));
        }

        return devices.ToArray();
    }

    private static string[] ReadInstanceIds(string service)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint code = GetListSize(out uint length, service, DeviceFilters);
            if (code is NoSuchValue or NoSuchDevNode)
                return [];
            Verify(code);
            if (length <= 1)
                return [];

            var buffer = new char[length];
            code = GetList(service, buffer, length, DeviceFilters);
            if (code == BufferSmall)
                continue;
            if (code is NoSuchValue or NoSuchDevNode)
                return [];
            Verify(code);
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        throw new InvalidOperationException("Список устройств Windows изменился во время проверки. Повторите поиск vJoy.");
    }

    private static void Verify(uint code)
    {
        if (code != 0)
            throw new InvalidOperationException($"Не удалось прочитать состояние vJoy в Windows (CfgMgr32: 0x{code:X}).");
    }

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetListSize(out uint length, string filter, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetList(string filter, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)]
        char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint LocateDevice(out uint instance, string instanceId, uint flags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Status", ExactSpelling = true)]
    private static extern uint ReadStatus(out uint status, out uint problem, uint instance, uint flags);
}
