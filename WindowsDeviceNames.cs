using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace BatteryTaskbar;

internal static class WindowsDeviceNames
{
    private const string NameKey = "System.ItemNameDisplay";

    public static async Task<Dictionary<Guid, string>> GetContainerNamesAsync()
    {
        var containers = new Dictionary<Guid, string>();
        var objects = await PnpObject.FindAllAsync(
            PnpObjectType.DeviceContainer, new[] { NameKey });

        foreach (var item in objects)
        {
            if (Guid.TryParse(item.Id, out var id) &&
                item.Properties.TryGetValue(NameKey, out var value) &&
                value is string name &&
                !string.IsNullOrWhiteSpace(name))
            {
                containers[id] = name;
            }
        }

        Log.Write("  Контейнеров устройств: " + containers.Count);
        return containers;
    }

    public static string ResolveName(
        DeviceInformation info,
        Dictionary<Guid, string> containers,
        string fallback)
    {
        if (info.Properties.TryGetValue("System.Devices.ContainerId", out var value) &&
            value is Guid id &&
            containers.TryGetValue(id, out var containerName) &&
            !string.IsNullOrWhiteSpace(containerName))
        {
            return containerName;
        }

        return string.IsNullOrWhiteSpace(info.Name) ? fallback : info.Name;
    }
}
