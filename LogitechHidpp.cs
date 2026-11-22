using System;
using System.Collections.Generic;
using System.Text;
using HidSharp;

namespace BatteryTaskbar;

// Устройства Logitech на USB-приёмниках (Unifying, Bolt, Lightspeed) отдают заряд по протоколу HID++ 2.0.
public static class LogitechHidpp
{
    const int VendorId = 0x046D;
    const byte SwId = 0x05;

    public static List<DeviceBattery> ReadAll()
    {
        var list = new List<DeviceBattery>();
        int total = 0, longIf = 0;
        foreach (var dev in DeviceList.Local.GetHidDevices(VendorId))
        {
            total++;
            try
            {
                // интерфейс с длинными HID++ отчётами (20 байт)
                if (dev.GetMaxOutputReportLength() != 20) continue;
                longIf++;
                int inLen = dev.GetMaxInputReportLength();
                if (inLen < 20) continue;
                if (!dev.TryOpen(out HidStream stream)) continue;
                using (stream)
                {
                    stream.ReadTimeout = 300;
                    stream.WriteTimeout = 300;
                    for (byte idx = 1; idx <= 6; idx++)
                    {
                        try
                        {
                            var r = ReadDevice(stream, inLen, idx);
                            if (r != null) list.Add(r);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
        Log.Write("  Logitech HID интерфейсов: " + total + ", с длинными отчётами HID++: " + longIf);
        return list;
    }

    static DeviceBattery? ReadDevice(HidStream s, int inLen, byte idx)
    {
        int level = -1;

        // 0x1004 — Unified Battery
        var f = Request(s, inLen, idx, 0x00, 0x00, 0x10, 0x04);
        if (f != null && f[4] != 0)
        {
            var st = Request(s, inLen, idx, f[4], 0x01);
            if (st != null) level = st[4];
        }

        // 0x1000 — Battery Level Status
        if (level < 0)
        {
            f = Request(s, inLen, idx, 0x00, 0x00, 0x10, 0x00);
            if (f != null && f[4] != 0)
            {
                var st = Request(s, inLen, idx, f[4], 0x00);
                if (st != null) level = st[4];
            }
        }

        if (level < 0 || level > 100) return null;
        string name = ReadName(s, inLen, idx) ?? ("Logitech " + idx);
        return new DeviceBattery(name, level);
    }

    static string? ReadName(HidStream s, int inLen, byte idx)
    {
        var f = Request(s, inLen, idx, 0x00, 0x00, 0x00, 0x05); // 0x0005 — Device Name
        if (f == null || f[4] == 0) return null;
        var cnt = Request(s, inLen, idx, f[4], 0x00);
        if (cnt == null) return null;
        int len = cnt[4];
        if (len <= 0 || len > 60) return null;

        var sb = new StringBuilder();
        while (sb.Length < len)
        {
            var part = Request(s, inLen, idx, f[4], 0x01, (byte)sb.Length);
            if (part == null) break;
            int take = Math.Min(16, len - sb.Length);
            sb.Append(Encoding.ASCII.GetString(part, 4, take));
        }
        var name = sb.ToString().Trim('\0', ' ');
        return name.Length > 0 ? name : null;
    }

    static byte[]? Request(HidStream s, int inLen, byte devIdx, byte featIdx, byte func, params byte[] args)
    {
        var buf = new byte[20];
        buf[0] = 0x11;
        buf[1] = devIdx;
        buf[2] = featIdx;
        buf[3] = (byte)((func << 4) | SwId);
        for (int i = 0; i < args.Length && i < 16; i++) buf[4 + i] = args[i];
        s.Write(buf);

        var end = DateTime.UtcNow.AddMilliseconds(600);
        while (DateTime.UtcNow < end)
        {
            var inb = new byte[inLen];
            int n;
            try { n = s.Read(inb); }
            catch (TimeoutException) { return null; }
            if (n < 5) continue;
            if (inb[1] != devIdx) continue;
            if (inb[2] == 0x8F) return null;                      // ошибка HID++ 1.0 (устройство недоступно)
            if (inb[2] == 0xFF && inb[3] == featIdx) return null; // ошибка HID++ 2.0
            if (inb[0] == 0x11 && inb[2] == featIdx && (inb[3] & 0x0F) == SwId) return inb;
        }
        return null;
    }
}
