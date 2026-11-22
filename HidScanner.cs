using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HidSharp;

namespace BatteryTaskbar;

// Диагностика HID-устройств. Только ЧТЕНИЕ: ничего не отправляем устройствам.
public static class HidScanner
{
    // Список всех HID-устройств (нужен, чтобы добавлять поддержку новых брендов)
    public static void DumpAll()
    {
        foreach (var dev in DeviceList.Local.GetHidDevices())
        {
            try
            {
                string name = "", man = "";
                try { name = dev.GetProductName(); } catch { }
                try { man = dev.GetManufacturer(); } catch { }
                Log.Write("  HID " + dev.VendorID.ToString("X4") + ":" + dev.ProductID.ToString("X4") + " " + man + " " + name +
                          " in=" + dev.GetMaxInputReportLength() + " out=" + dev.GetMaxOutputReportLength() +
                          " feat=" + dev.GetMaxFeatureReportLength());
            }
            catch { }
        }
    }

    // Слушает служебные каналы любых донглов: только интерфейсы с vendor-defined usage page (0xFF00+).
    // Клавиатурные, мышиные и указательные интерфейсы пропускаются, чтобы не логировать ввод.
    public static async Task RunAsync(int seconds)
    {
        Log.Write("=== СКАНЕР СЛУЖЕБНЫХ HID-КАНАЛОВ, " + seconds + " с ===");
        var tasks = new List<Task>();
        int idx = 0;
        foreach (var dev in DeviceList.Local.GetHidDevices())
        {
            if (!IsVendorInterface(dev)) continue;
            int id = ++idx;
            var d = dev;
            tasks.Add(Task.Run(() => ScanInterface(d, id, seconds)));
        }
        Log.Write("Служебных интерфейсов: " + tasks.Count);
        await Task.WhenAll(tasks);
        Log.Write("=== сканер завершён ===");
    }

    static bool IsVendorInterface(HidDevice dev)
    {
        try
        {
            bool vendor = false;
            foreach (var item in dev.GetReportDescriptor().DeviceItems)
            {
                foreach (var usage in item.Usages.GetAllValues())
                {
                    // 0x00010006 клавиатура, 0x00010002 мышь, 0x00010001 указатель
                    if (usage == 0x00010006 || usage == 0x00010002 || usage == 0x00010001) return false;
                    if ((usage >> 16) >= 0xFF00) vendor = true;
                }
            }
            return vendor;
        }
        catch { return false; }
    }

    static void ScanInterface(HidDevice dev, int id, int seconds)
    {
        string name = "";
        try { name = dev.GetProductName(); } catch { }
        string tag = "[" + id + " " + dev.VendorID.ToString("X4") + ":" + dev.ProductID.ToString("X4") + " " + name +
                     " in=" + dev.GetMaxInputReportLength() + " out=" + dev.GetMaxOutputReportLength() +
                     " feat=" + dev.GetMaxFeatureReportLength() + "]";
        try
        {
            if (!dev.TryOpen(out HidStream stream)) { Log.Write(tag + " не открылся"); return; }
            using (stream)
            {
                // feature-отчёты из дескриптора (только чтение)
                try
                {
                    var rd = dev.GetReportDescriptor();
                    foreach (var fr in rd.FeatureReports)
                    {
                        try
                        {
                            var buf = new byte[Math.Max(dev.GetMaxFeatureReportLength(), fr.Length + 1)];
                            buf[0] = fr.ReportID;
                            stream.GetFeature(buf);
                            Log.Write(tag + " feature#" + fr.ReportID + ": " + Hex(buf, buf.Length));
                        }
                        catch (Exception ex) { Log.Write(tag + " feature#" + fr.ReportID + " ошибка: " + ex.Message); }
                    }
                }
                catch (Exception ex) { Log.Write(tag + " дескриптор: " + ex.Message); }

                int inLen = dev.GetMaxInputReportLength();
                if (inLen <= 0) return;
                stream.ReadTimeout = 1000;
                var end = DateTime.UtcNow.AddSeconds(seconds);
                string? prev = null;
                while (DateTime.UtcNow < end)
                {
                    var buf = new byte[inLen];
                    int n;
                    try { n = stream.Read(buf); }
                    catch (TimeoutException) { continue; }
                    var hex = Hex(buf, n);
                    if (hex != prev) { Log.Write(tag + " in: " + hex); prev = hex; }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write(tag + " ошибка: " + ex.Message);
        }
    }

    static string Hex(byte[] b, int n)
    {
        n = Math.Min(n, b.Length);
        return BitConverter.ToString(b, 0, n).Replace("-", " ");
    }
}
