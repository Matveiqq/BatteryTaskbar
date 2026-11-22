using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;
using Windows.Devices.Power;
using Windows.Storage.Streams;

namespace BatteryTaskbar;

public record DeviceBattery(string Name, int Level);

public static class BatteryService
{
    public static async Task<List<DeviceBattery>> ReadAllAsync()
    {
        var result = new Dictionary<string, DeviceBattery>();
        try { await ReadBleAsync(result); } catch { }
        try { await ReadClassicAsync(result); } catch { }
        try { await ReadWindowsBatteryApiAsync(result); } catch { }
        try
        {
            var logi = await Task.Run(() => LogitechHidpp.ReadAll());
            foreach (var d in logi) result.TryAdd(d.Name, d);
        }
        catch { }
        return result.Values.OrderBy(d => d.Name).ToList();
    }

    // Bluetooth LE: мыши, клавиатуры, многие наушники (стандартный GATT Battery Service)
    static async Task ReadBleAsync(Dictionary<string, DeviceBattery> result)
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        foreach (var info in infos)
        {
            try
            {
                using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (dev == null || string.IsNullOrWhiteSpace(dev.Name)) continue;
                if (dev.ConnectionStatus != BluetoothConnectionStatus.Connected) continue;

                var services = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
                if (services.Status != GattCommunicationStatus.Success) continue;

                foreach (var service in services.Services)
                {
                    var chars = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel, BluetoothCacheMode.Uncached);
                    if (chars.Status != GattCommunicationStatus.Success) continue;

                    foreach (var ch in chars.Characteristics)
                    {
                        var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                        if (read.Status != GattCommunicationStatus.Success) continue;
                        var reader = DataReader.FromBuffer(read.Value);
                        int level = reader.ReadByte();
                        if (level >= 0 && level <= 100)
                            result[dev.Name] = new DeviceBattery(dev.Name, level);
                    }
                }
            }
            catch { }
        }
    }

    // Классический Bluetooth (наушники с Hands-Free): Windows хранит заряд в свойстве устройства
    static async Task ReadClassicAsync(Dictionary<string, DeviceBattery> result)
    {
        const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        const string ContainerKey = "System.Devices.ContainerId";

        var names = new Dictionary<Guid, string>();
        var paired = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            new[] { "System.Devices.Aep.ContainerId" });
        foreach (var p in paired)
        {
            if (p.Properties.TryGetValue("System.Devices.Aep.ContainerId", out var v) && v is Guid g)
                names[g] = p.Name;
        }

        var pnps = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { BatteryKey, ContainerKey });
        foreach (var obj in pnps)
        {
            if (!obj.Properties.TryGetValue(BatteryKey, out var bv) || bv == null) continue;
            if (!obj.Properties.TryGetValue(ContainerKey, out var cv) || cv is not Guid cid) continue;
            if (!names.TryGetValue(cid, out var name) || string.IsNullOrWhiteSpace(name)) continue;
            int level = Convert.ToInt32(bv);
            if (level < 0 || level > 100) continue;
            result.TryAdd(name, new DeviceBattery(name, level));
        }
    }

    // Стандартный HID-заряд: геймпады, часть мышей и клавиатур на донглах.
    // Ноутбучную батарею пропускаем: берём только устройства с HID в идентификаторе.
    static async Task ReadWindowsBatteryApiAsync(Dictionary<string, DeviceBattery> result)
    {
        var containers = new Dictionary<Guid, string>();
        var objs = await PnpObject.FindAllAsync(PnpObjectType.DeviceContainer, new[] { "System.ItemNameDisplay" });
        foreach (var o in objs)
        {
            if (Guid.TryParse(o.Id, out var g) &&
                o.Properties.TryGetValue("System.ItemNameDisplay", out var v) && v is string s)
                containers[g] = s;
        }

        var infos = await DeviceInformation.FindAllAsync(Battery.GetDeviceSelector(), new[] { "System.Devices.ContainerId" });
        foreach (var info in infos)
        {
            try
            {
                if (!info.Id.Contains("HID", StringComparison.OrdinalIgnoreCase)) continue;
                var bat = await Battery.FromIdAsync(info.Id);
                if (bat == null) continue;
                var rep = bat.GetReport();
                if (rep.RemainingCapacityInMilliwattHours is not int rem ||
                    rep.FullChargeCapacityInMilliwattHours is not int full || full <= 0) continue;

                string name = info.Name;
                if (info.Properties.TryGetValue("System.Devices.ContainerId", out var cv) && cv is Guid cid &&
                    containers.TryGetValue(cid, out var cname) && !string.IsNullOrWhiteSpace(cname))
                    name = cname;
                if (string.IsNullOrWhiteSpace(name)) continue;

                int level = (int)Math.Round(rem * 100.0 / full);
                if (level < 0 || level > 100) continue;
                result.TryAdd(name, new DeviceBattery(name, level));
            }
            catch { }
        }
    }
}
