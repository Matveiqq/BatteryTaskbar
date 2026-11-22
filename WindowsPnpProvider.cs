using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Enumeration.Pnp;

namespace BatteryTaskbar;

public sealed class WindowsPnpProvider : IBatteryProvider
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string ContainerKey = "System.Devices.ContainerId";
    private const string NameKey = "System.ItemNameDisplay";

    public string Name => "Windows PnP";

    public async Task<List<DeviceBattery>> ReadAsync()
    {
        var result = new List<DeviceBattery>();
        var containers = await WindowsDeviceNames.GetContainerNamesAsync();
        var objects = await PnpObject.FindAllAsync(
            PnpObjectType.Device, new[] { BatteryKey, ContainerKey, NameKey });

        int withBattery = 0;

        foreach (var item in objects)
        {
            if (!item.Properties.TryGetValue(BatteryKey, out var batteryValue) ||
                batteryValue == null)
                continue;

            withBattery++;

            int level;
            try { level = Convert.ToInt32(batteryValue); }
            catch { continue; }

            if (level < 0 || level > 100)
                continue;

            string? name = null;

            if (item.Properties.TryGetValue(ContainerKey, out var containerValue) &&
                containerValue is Guid containerId &&
                containers.TryGetValue(containerId, out var containerName))
            {
                name = containerName;
            }

            if (string.IsNullOrWhiteSpace(name) &&
                item.Properties.TryGetValue(NameKey, out var nameValue) &&
                nameValue is string itemName)
            {
                name = itemName;
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new DeviceBattery(name, level));
        }

        Log.Write("  PnP объектов всего: " + objects.Count +
                  ", с зарядом: " + withBattery);
        return result;
    }
}
