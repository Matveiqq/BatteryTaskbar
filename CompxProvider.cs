using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HidSharp;

namespace BatteryTaskbar;

// Мыши семейства Compx/Pulsar на донгле или по кабелю: VXE (в том числе R1 SE+), ATK, VGN, Pulsar.
// Служебный канал: usage page 0xFF02, feature-отчёт 0x08 длиной 17 байт (номер отчёта + 16 байт),
// контрольная сумма в последнем байте дополняет сумму байт кадра до 0x55, команда 0x04 = состояние питания.
// Мышь только опрашивается, никаких настроек не меняется.
// Протокол описан в открытых проектах (OpenMouse mouse-protocol, BatteryTray, драйвер hid-pulsar, HaloBattery).
// Код написан без доступа к реальному устройству автором: при неудаче в журнале остаётся сырой ответ для разбора.
public sealed class CompxProvider : IBatteryProvider
{
    static readonly int[] VendorIds = { 0x3554, 0x3710 };
    const uint VendorUsagePage = 0xFF02;
    const byte ReportId = 0x08;
    const int FrameLength = 17;
    const byte CmdPower = 0x04;
    const int ChecksumTotal = 0x55;

    public string Name => "Мыши Compx/Pulsar (VXE, ATK, VGN)";

    public Task<List<BatteryDevice>> ReadAsync() => Task.Run(ReadAll);

    static List<BatteryDevice> ReadAll()
    {
        var list = new List<BatteryDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (int vendorId in VendorIds)
        {
            foreach (var device in DeviceList.Local.GetHidDevices(vendorId))
            {
                try
                {
                    if (!HasVendorPage(device)) continue;
                    if (device.GetMaxFeatureReportLength() < FrameLength) continue;

                    int? level = Query(device);
                    if (level.HasValue)
                    {
                        string name = MakeName(device);
                        if (seen.Add(name))
                            list.Add(new BatteryDevice(name, level.Value, "Compx HID"));
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("  Compx: ошибка " + ex.Message);
                }
            }
        }
        return list;
    }

    static bool HasVendorPage(HidDevice device)
    {
        try
        {
            foreach (var item in device.GetReportDescriptor().DeviceItems)
                foreach (var usage in item.Usages.GetAllValues())
                    if ((usage >> 16) == VendorUsagePage) return true;
        }
        catch { }
        return false;
    }

    static string MakeName(HidDevice device)
    {
        string product = "";
        try { product = device.GetProductName(); } catch { }
        product = Regex.Replace(product ?? "", @"(?i)\b(receiver|dongle|wireless|2\.4g|usb|mouse)\b", " ");
        product = Regex.Replace(product, @"\s+", " ").Trim();
        return "Мышь " + (string.IsNullOrEmpty(product) ? "VXE/ATK" : product);
    }

    static byte Checksum(byte[] frame)
    {
        int sum = 0;
        for (int i = 1; i <= 15; i++) sum += frame[i];
        return (byte)((ChecksumTotal - sum) & 0xFF);
    }

    static int? Query(HidDevice device)
    {
        if (!device.TryOpen(out HidStream stream)) return null;

        using (stream)
        {
            stream.ReadTimeout = 100;
            stream.WriteTimeout = 500;

            var request = new byte[FrameLength];
            request[0] = ReportId;
            request[1] = CmdPower;
            request[16] = Checksum(request);
            stream.SetFeature(request);

            byte[]? last = null;
            var end = DateTime.UtcNow.AddMilliseconds(900);
            while (DateTime.UtcNow < end)
            {
                Thread.Sleep(40);
                var reply = new byte[FrameLength];
                reply[0] = ReportId;
                try { stream.GetFeature(reply); }
                catch (Exception ex)
                {
                    Log.Debug("  Compx: чтение ответа не удалось: " + ex.Message);
                    break;
                }

                last = reply;
                if (reply[1] != CmdPower) continue;

                int? level = ParseBattery(reply);
                if (level.HasValue) return level;
            }

            if (last != null)
                Log.Debug("  Compx: заряд не получен, последний ответ " + BitConverter.ToString(last));
        }
        return null;
    }

    // Ответ на команду 0x04: 08 04 .. .. .. <длина> <заряд %> <флаги> <напряжение мВ, 2 байта> ..
    static int? ParseBattery(byte[] reply)
    {
        int percent = reply[6];
        int flags = reply[7];
        if (percent >= 1 && percent <= 100 && flags <= 0x04)
            return percent;
        return null;
    }
}
