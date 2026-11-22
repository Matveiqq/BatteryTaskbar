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
    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    const string ContainerKey = "System.Devices.ContainerId";
    const string NameKey = "System.ItemNameDisplay";
    static bool dumped;

    public static async Task<List<DeviceBattery>> ReadAllAsync()
    {
        var result = new Dictionary<string, DeviceBattery>();
        Log.Write("--- опрос устройств ---");

        await Safe("Заряд из Windows (PnP)", () => ReadPnpAsync(result));
        await Safe("Windows Battery API", () => ReadWindowsBatteryApiAsync(result));
        if (!dumped)
        {
            dumped = true;
            await Safe("Список HID-устройств", () => Task.Run(() => LogitechHidpp.DumpAllHid()));
        }
        await Safe("Logitech HID++", async () =>
        {
            var logi = await Task.Run(() => LogitechHidpp.ReadAll());
            foreach (var d in logi)
            {
                Log.Write("  Logitech: " + d.Name + " = " + d.Level + "%");
                result.TryAdd(d.Name, d);
            }
        });
        await Safe("Bluetooth LE (GATT)", () => ReadBleAsync(result));

        Log.Write("Итого найдено: " + result.Count);
        return result.Values.OrderBy(d => d.Name).ToList();
    }

    static async Task Safe(string title, Func<Task> action)
    {
        try
        {
            await action();
            Log.Write(title + ": завершено");
        }
        catch (Exception ex)
        {
            Log.Write(title + ": ОШИБКА " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // Имена устройств по ContainerId
    static async Task<Dictionary<Guid, string>> GetContainerNamesAsync()
    {
        var containers = new Dictionary<Guid, string>();
        var objs = await PnpObject.FindAllAsync(PnpObjectType.DeviceContainer, new[] { NameKey });
        foreach (var o in objs)
        {
            if (Guid.TryParse(o.Id, out var g) &&
                o.Properties.TryGetValue(NameKey, out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
                containers[g] = s;
        }
        Log.Write("  Контейнеров устройств: " + containers.Count);
        return containers;
    }

    // Windows сама хранит заряд Bluetooth-устройств (и классических, и LE) в свойстве узла устройства
    static async Task ReadPnpAsync(Dictionary<string, DeviceBattery> result)
    {
        var containers = await GetContainerNamesAsync();
        var pnps = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { BatteryKey, ContainerKey, NameKey });

        int withBattery = 0;
        foreach (var obj in pnps)
        {
            if (!obj.Properties.TryGetValue(BatteryKey, out var bv) || bv == null) continue;
            withBattery++;

            int level;
            try { level = Convert.ToInt32(bv); } catch { continue; }
            if (level < 0 || level > 100) continue;

            string? name = null;
            if (obj.Properties.TryGetValue(ContainerKey, out var cv) && cv is Guid cid &&
                containers.TryGetValue(cid, out var cname))
                name = cname;
            if (string.IsNullOrWhiteSpace(name) &&
                obj.Properties.TryGetValue(NameKey, out var nv) && nv is string ns)
                name = ns;
            if (string.IsNullOrWhiteSpace(name)) continue;

            Log.Write("  PnP: " + name + " = " + level + "%");
            result.TryAdd(name, new DeviceBattery(name, level));
        }
        Log.Write("  PnP объектов всего: " + pnps.Count + ", с зарядом: " + withBattery);
    }

    // Стандартный HID-заряд: геймпады, часть мышей и клавиатур на донглах.
    // Ноутбучную батарею пропускаем: берём только устройства с HID в идентификаторе.
    static async Task ReadWindowsBatteryApiAsync(Dictionary<string, DeviceBattery> result)
    {
        var containers = await GetContainerNamesAsync();
        var infos = await DeviceInformation.FindAllAsync(Battery.GetDeviceSelector(), new[] { ContainerKey });
        Log.Write("  Battery API, устройств с батареей: " + infos.Count);

        foreach (var info in infos)
        {
            try
            {
                bool isHid = info.Id.Contains("HID", StringComparison.OrdinalIgnoreCase);
                Log.Write("  Battery API: " + info.Name + (isHid ? " (HID)" : " (пропущено, не HID)"));
                if (!isHid) continue;

                var bat = await Battery.FromIdAsync(info.Id);
                if (bat == null) continue;
                var rep = bat.GetReport();
                if (rep.RemainingCapacityInMilliwattHours is not int rem ||
                    rep.FullChargeCapacityInMilliwattHours is not int full || full <= 0) continue;

                string name = info.Name;
                if (info.Properties.TryGetValue(ContainerKey, out var cv) && cv is Guid cid &&
                    containers.TryGetValue(cid, out var cname) && !string.IsNullOrWhiteSpace(cname))
                    name = cname;
                if (string.IsNullOrWhiteSpace(name)) continue;

                int level = (int)Math.Round(rem * 100.0 / full);
                if (level < 0 || level > 100) continue;
                result.TryAdd(name, new DeviceBattery(name, level));
            }
            catch (Exception ex)
            {
                Log.Write("  Battery API ошибка: " + ex.Message);
            }
        }
    }

    // Bluetooth LE: стандартный GATT Battery Service. Медленный способ, поэтому идёт последним
    static async Task ReadBleAsync(Dictionary<string, DeviceBattery> result)
    {
        var infos = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true));
        Log.Write("  BLE спаренных устройств: " + infos.Count);
        foreach (var info in infos)
        {
            var task = ReadBleDeviceAsync(info, result);
            if (await Task.WhenAny(task, Task.Delay(8000)) != task)
                Log.Write("  BLE таймаут: " + info.Name);
        }
    }

    static async Task ReadBleDeviceAsync(DeviceInformation info, Dictionary<string, DeviceBattery> result)
    {
        try
        {
            using var dev = await BluetoothLEDevice.FromIdAsync(info.Id);
            if (dev == null) { Log.Write("  BLE " + info.Name + ": не открылось"); return; }
            Log.Write("  BLE " + dev.Name + ": " + dev.ConnectionStatus);
            if (string.IsNullOrWhiteSpace(dev.Name) || dev.ConnectionStatus != BluetoothConnectionStatus.Connected) return;

            var services = await dev.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
            Log.Write("  BLE " + dev.Name + ": служба батареи " + services.Status + ", найдено " + services.Services.Count);
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
                        Log.Write("  BLE: " + dev.Name + " = " + level + "%");
                        result[dev.Name] = new DeviceBattery(dev.Name, level);
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
