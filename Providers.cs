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

static class WinKeys
{
    public const string Battery = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    public const string ContainerId = "System.Devices.ContainerId";
    public const string ItemName = "System.ItemNameDisplay";

    // Имена устройств по ContainerId
    public static async Task<Dictionary<Guid, string>> ContainerNamesAsync()
    {
        var map = new Dictionary<Guid, string>();
        var objs = await PnpObject.FindAllAsync(PnpObjectType.DeviceContainer, new[] { ItemName });
        foreach (var o in objs)
        {
            if (Guid.TryParse(o.Id, out var g) &&
                o.Properties.TryGetValue(ItemName, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                map[g] = s;
        }
        return map;
    }
}

// Windows сама хранит заряд Bluetooth-устройств (классических и LE) в свойстве устройства.
public sealed class PnpBatteryProvider : IBatteryProvider
{
    public string Name => "Заряд из Windows (PnP)";

    public async Task<List<BatteryDevice>> ReadAsync()
    {
        var result = new List<BatteryDevice>();

        try
        {
            var paired = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            Log.Debug("  Bluetooth (классический) спаренных: " + paired.Count + " " +
                      string.Join(", ", paired.Select(p => p.Name)));
        }
        catch (Exception ex) { Log.Debug("  Bluetooth (классический): " + ex.Message); }

        var containers = await WinKeys.ContainerNamesAsync();
        var pnps = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { WinKeys.Battery, WinKeys.ContainerId, WinKeys.ItemName });

        int withBattery = 0;
        foreach (var obj in pnps)
        {
            if (!obj.Properties.TryGetValue(WinKeys.Battery, out var bv) || bv == null) continue;
            withBattery++;

            int level;
            try { level = Convert.ToInt32(bv); } catch { continue; }
            if (level < 0 || level > 100) continue;

            string? name = null;
            if (obj.Properties.TryGetValue(WinKeys.ContainerId, out var cv) && cv is Guid cid &&
                containers.TryGetValue(cid, out var cname))
                name = cname;
            if (string.IsNullOrWhiteSpace(name) &&
                obj.Properties.TryGetValue(WinKeys.ItemName, out var nv) && nv is string ns)
                name = ns;
            if (string.IsNullOrWhiteSpace(name)) continue;

            Log.Debug("  PnP: " + name + " = " + level + "%");
            result.Add(new BatteryDevice(name, level, "Windows PnP"));
        }
        Log.Debug("  PnP объектов всего: " + pnps.Count + ", с зарядом: " + withBattery);
        return result;
    }
}

// Стандартный HID-заряд (геймпады, часть мышей и клавиатур на донглах).
// Ноутбучную батарею пропускаем: берём только устройства с HID в идентификаторе.
public sealed class WindowsBatteryApiProvider : IBatteryProvider
{
    public string Name => "Windows Battery API";

    public async Task<List<BatteryDevice>> ReadAsync()
    {
        var result = new List<BatteryDevice>();
        var containers = await WinKeys.ContainerNamesAsync();
        var infos = await DeviceInformation.FindAllAsync(Battery.GetDeviceSelector(), new[] { WinKeys.ContainerId });
        Log.Debug("  Battery API, устройств с батареей: " + infos.Count);

        foreach (var info in infos)
        {
            try
            {
                bool isHid = info.Id.Contains("HID", StringComparison.OrdinalIgnoreCase);
                Log.Debug("  Battery API: " + info.Name + (isHid ? " (HID)" : " (пропущено, не HID)"));
                if (!isHid) continue;

                var bat = await Battery.FromIdAsync(info.Id);
                if (bat == null) continue;
                var rep = bat.GetReport();
                if (rep.RemainingCapacityInMilliwattHours is not int rem ||
                    rep.FullChargeCapacityInMilliwattHours is not int full || full <= 0) continue;

                string name = info.Name;
                if (info.Properties.TryGetValue(WinKeys.ContainerId, out var cv) && cv is Guid cid &&
                    containers.TryGetValue(cid, out var cname) && !string.IsNullOrWhiteSpace(cname))
                    name = cname;
                if (string.IsNullOrWhiteSpace(name)) continue;

                int level = (int)Math.Round(rem * 100.0 / full);
                if (level < 0 || level > 100) continue;
                result.Add(new BatteryDevice(name, level, "Windows Battery API"));
            }
            catch (Exception ex)
            {
                Log.Write("  Battery API ошибка: " + ex.Message);
            }
        }
        return result;
    }
}

// Bluetooth LE: стандартная служба GATT Battery Service.
public sealed class BleGattProvider : IBatteryProvider
{
    public string Name => "Bluetooth LE (GATT)";

    public async Task<List<BatteryDevice>> ReadAsync()
    {
        var result = new List<BatteryDevice>();
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        Log.Debug("  BLE спаренных устройств: " + infos.Count);

        foreach (var info in infos)
        {
            var task = ReadDeviceAsync(info, result);
            if (await Task.WhenAny(task, Task.Delay(8000)) != task)
                Log.Write("  BLE тайм-аут: " + info.Name);
        }
        return result;
    }

    static async Task ReadDeviceAsync(DeviceInformation info, List<BatteryDevice> result)
    {
        try
        {
            using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (dev == null) { Log.Debug("  BLE " + info.Name + ": не открылось"); return; }
            Log.Debug("  BLE " + dev.Name + ": " + dev.ConnectionStatus);
            if (string.IsNullOrWhiteSpace(dev.Name) || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) return;

            var services = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
            Log.Debug("  BLE " + dev.Name + ": служба батареи " + services.Status + ", найдено " + services.Services.Count);
            if (services.Status != GattCommunicationStatus.Success) return;

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
                    {
                        Log.Debug("  BLE: " + dev.Name + " = " + level + "%");
                        lock (result) result.Add(new BatteryDevice(dev.Name, level, "Bluetooth LE"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("  BLE " + info.Name + " ошибка: " + ex.Message);
        }
    }
}
