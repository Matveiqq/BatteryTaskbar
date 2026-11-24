using System;
using System.Collections.Generic;
using System.IO;

namespace BatteryTaskbar;

public static class Log
{
    const long MaxBytes = 150_000;
    static readonly object Gate = new();
    static readonly Dictionary<string, DateTime> recentErrors = new();

    // Подробный журнал: включается в меню трея
    public static bool Verbose { get; set; }
    // Подробно на время первого опроса после запуска (там нужен список HID-устройств и ответы источников)
    public static bool ForceVerbose { get; set; }

    static string Dir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BatteryTaskbar");

    public static string FilePath => System.IO.Path.Combine(Dir, "log.txt");
    static string OldPath => System.IO.Path.Combine(Dir, "log.old.txt");

    // Подробные строки: по умолчанию не пишутся, чтобы журнал не разрастался
    public static void Debug(string message)
    {
        if (Verbose || ForceVerbose) Write(message);
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var now = DateTime.Now;

                // Одинаковые ошибки пишем не чаще раза в 30 минут
                bool isError = message.Contains("ошибк", StringComparison.OrdinalIgnoreCase) ||
                               message.Contains("тайм-аут", StringComparison.OrdinalIgnoreCase);
                if (isError && !Verbose && !ForceVerbose)
                {
                    if (recentErrors.TryGetValue(message, out var last) && now - last < TimeSpan.FromMinutes(30)) return;
                    recentErrors[message] = now;
                    if (recentErrors.Count > 300) recentErrors.Clear();
                }

                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    // хранятся максимум два файла: log.txt и log.old.txt
                    if (File.Exists(OldPath)) File.Delete(OldPath);
                    File.Move(FilePath, OldPath);
                }
                File.AppendAllText(FilePath, now.ToString("dd.MM HH:mm:ss") + "  " + message + Environment.NewLine);
            }
        }
        catch { }
    }
}
