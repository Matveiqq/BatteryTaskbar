using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Power;

namespace BatteryTaskbar;

public sealed class WindowsBatteryApiProvider : IBatteryProvider
{
    private const string ContainerKey = "System.Devices.ContainerId";

    public string Name => "Windows Battery API";

    public async Task<List<DeviceBattery>> ReadAsync()
    {
        var result = new List<DeviceBattery>();
        var containers = await WindowsDeviceNames.GetContainerNamesAsync();
        var infos = await DeviceInformation.FindAllAsync(
            Battery.GetDeviceSelector(), new[] { ContainerKey });

        Log.Write("  Battery API, устройств с батареей: " + infos.Count);

        foreach (var info in infos)
        {
            try
            {
                bool isHid = info.Id.Contains(
                    "HID", StringComparison.OrdinalIgnoreCase);

                Log.Write("  Battery API: " + info.Name +
                          (isHid ? " (HID)" : " (пропущено, не HID)"));

                if (!isHid)
                    continue;

                var battery = await Battery.FromIdAsync(info.Id);
                if (battery == null)
                    continue;

                var report = battery.GetReport();

                if (report.RemainingCapacityInMilliwattHours is not int remaining ||
                    report.FullChargeCapacityInMilliwattHours is not int full ||
                    full <= 0)
                    continue;

                string name = WindowsDeviceNames.ResolveName(
                    info, containers, info.Name);

                int level = (int)Math.Round(remaining * 100.0 / full);
                if (level >= 0 && level <= 100)
                    result.Add(new DeviceBattery(name, level));
            }
            catch (Exception ex)
            {
                Log.Write("  Battery API ошибка: " + ex.Message);
            }
        }

        return result;
    }
}
