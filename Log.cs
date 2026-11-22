using System;
using System.IO;

namespace BatteryTaskbar;

public static class Log
{
    static readonly object Gate = new();

    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BatteryTaskbar", "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 300_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}
