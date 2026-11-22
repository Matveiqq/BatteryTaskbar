using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;
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

        // AJAZZ 3151:5007: vendor status poll 0xF7, then Feature Report 0x05.
        await Safe("AJAZZ HID (заряд)", () => Task.Run(() => ReadAjazzAsync(result)));

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

    static void ReadAjazzAsync(Dictionary<string, DeviceBattery> result)
    {
        int matching = 0;
        int success = 0;

        foreach (var dev in DeviceList.Local.GetHidDevices(0x3151))
        {
            if (dev.ProductID != 0x5007) continue;

            int featureLength;
            try { featureLength = dev.GetMaxFeatureReportLength(); }
            catch { continue; }

            Log.Write("  AJAZZ HID-интерфейс: feature=" + featureLength);
            if (featureLength < 65) continue;
            matching++;

            try
            {
                if (!dev.TryOpen(out HidStream stream))
                {
                    Log.Write("  AJAZZ: интерфейс не удалось открыть");
                    continue;
                }

                using (stream)
                {
                    stream.ReadTimeout = 500;
                    stream.WriteTimeout = 500;

                    // Включаем обновление телеметрии 2.4 GHz-приёмника.
                    // Report ID 0x00, opcode 0xF7, остальные байты нулевые.
                    var poll = new byte[featureLength];
                    poll[0] = 0x00;
                    poll[1] = 0xF7;
                    stream.SetFeature(poll);
                    Thread.Sleep(30);

                    // Report ID 0x05: на Windows уровень заряда находится в byte[3].
                    var report = new byte[featureLength];
                    report[0] = 0x05;
                    stream.GetFeature(report);

                    int level = report[3];
                    Log.Write("  AJAZZ Feature 0x05: " + BitConverter.ToString(report, 0, Math.Min(8, report.Length)));

                    // A completely empty report means the receiver has not populated
                    // its telemetry yet; do not display that as a false 0% reading.
                    bool emptyReport = report[1] == 0 && report[2] == 0 && report[3] == 0;
                    if (!emptyReport && level <= 100)
                    {
                        const string name = "AJAZZ 2.4G 8K";
                        result[name] = new DeviceBattery(name, level);
                        Log.Write("  AJAZZ: заряд " + level + "%");
                        success++;
                    }
                    else
                    {
                        Log.Write(emptyReport
                            ? "  AJAZZ: приёмник пока не передал данные о заряде"
                            : "  AJAZZ: некорректное значение заряда " + level);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("  AJAZZ: ошибка чтения: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        Log.Write("  AJAZZ: подходящих интерфейсов=" + matching + ", успешно прочитано=" + success);
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

    // Windows сама хранит заряд Bluetooth-устройств в свойстве узла устройства.
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

    // Стандартный Windows Battery API.
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

    // Bluetooth LE: стандартный GATT Battery Service.
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
