using Laveyana.LavOSC.Modules.Pedals;
using Laveyana.LavOSC.Modules.Wheel;
using Microsoft.Maui.Storage;

namespace Laveyana.LavOSC;

internal sealed partial class OscReceiverPage
{
    private async Task PrepareVJoyAsync(bool arm, VJoyAxes axes = VJoyAxes.X)
    {
        if (working || closed || axes == VJoyAxes.None)
            return;

        WheelSession? currentWheel = session;
        PedalSession? currentPedals = pedals;
        bool wheelActive = currentWheel?.Snapshot().OutputActive == true;
        bool pedalsActive = currentPedals?.Snapshot().OutputActive == true;
        if ((axes == VJoyAxes.X && wheelActive) || (axes == VJoyAxes.Pedals && pedalsActive))
            return;

        bool SessionIsCurrent() => axes switch
        {
            VJoyAxes.X => currentWheel is not null && ReferenceEquals(session, currentWheel),
            VJoyAxes.Pedals => currentPedals is not null && ReferenceEquals(pedals, currentPedals),
            _ => false
        };
        if (arm && !SessionIsCurrent())
            return;

        int? sharedId = axes == VJoyAxes.X && pedalsActive ? pedalDeviceId :
            axes == VJoyAxes.Pedals && wheelActive ? wheelDeviceId : null;
        var options = new VJoySetupOptions(dllPath.Text, Math.Max(0, device.SelectedIndex),
            sharedId ?? preparedDeviceId ?? Preferences.Default.Get("VJoyDevice", 0), axes,
            AllowDeviceCreation: !wheelActive && !pedalsActive);

        working = true;
        vJoyError = null;
        vJoyMessage = "Поиск установленного vJoy…";
        Refresh();
        var token = lifetime.Token;
        try
        {
            var progress = new Progress<VJoyProgress>(update =>
            {
                if (closed || !working)
                    return;
                vJoyMessage = update.Message;
                vJoyScan = update.Scan;
                Refresh();
            });
            var prepared = await Task.Run(() => VJoySetup.PrepareAsync(options, progress, token), token);
            if (closed)
                return;

            vJoyScan = prepared.Scan;
            preparedDeviceId = prepared.Id;
            vJoyMessage = prepared.Created ? $"Создано устройство №{prepared.Id} · оси X, Y, Z" :
                $"Подготовлено устройство №{prepared.Id} · {VJoyOutput.FormatAxes(axes)}";

            if (!arm || !SessionIsCurrent())
                return;

            vJoyMessage = $"Подключение к устройству №{prepared.Id}…";
            Refresh();
            VJoyOutput? pending = await Task.Run(() => VJoyOutput.Connect((uint)prepared.Id, prepared.Scan.DllPath, axes), token);
            try
            {
                if (closed || !SessionIsCurrent())
                    return;

                if (axes == VJoyAxes.X)
                {
                    currentWheel!.EnableOutput(() => pending);
                    wheelDeviceId = prepared.Id;
                }
                else
                {
                    currentPedals!.EnableOutput(() => pending);
                    pedalDeviceId = prepared.Id;
                }

                pending = null;
                vJoyMessage = $"Устройство №{prepared.Id} · {VJoyOutput.FormatAxes(axes)}";
                Preferences.Default.Set("VJoyDevice", prepared.Id);
            }
            finally
            {
                pending?.Dispose();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            vJoyError = exception.Message;
            if (axes == VJoyAxes.X && ReferenceEquals(currentWheel, session))
                armAfterCalibration = false;
        }
        finally
        {
            working = false;
            if (closed)
                lifetime.Dispose();
            else
                Refresh();
        }
    }

    private void ClearPreparedDevice()
    {
        preparedDeviceId = null;
        vJoyError = null;
        vJoyMessage = null;
        vJoyScan = null;
        Refresh();
    }

    private string DescribeDevices(VJoyScan? scan, bool wheelActive, bool pedalsActive)
    {
        if (scan?.DriverEnabled != true)
            return "";

        var devices = scan.Devices.Where(item => item.State != VJoyDeviceState.Missing).Select(item =>
        {
            VJoyAxes leased = (wheelActive && item.Id == wheelDeviceId ? VJoyAxes.X : VJoyAxes.None) |
                (pedalsActive && item.Id == pedalDeviceId ? VJoyAxes.Pedals : VJoyAxes.None);
            string status = leased != VJoyAxes.None ? $"передача · {VJoyOutput.FormatAxes(leased)}" : item.State switch
            {
                VJoyDeviceState.Free => "свободно при проверке",
                VJoyDeviceState.Owned or VJoyDeviceState.Busy => "занято при проверке",
                _ => "состояние неизвестно"
            };
            string axes = item.Axes == VJoyAxes.None ? "оси недоступны" : $"оси {VJoyOutput.FormatAxes(item.Axes)}";
            return $"№{item.Id} · {status} · {axes}";
        }).ToArray();
        return devices.Length == 0 ? "Виртуальные устройства отсутствуют" : string.Join(Environment.NewLine, devices);
    }
}
