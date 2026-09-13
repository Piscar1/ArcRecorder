using System;
using System.IO;

namespace ArcRecorder
{
    /// <summary>Логи в %APPDATA%\ArcRecorder. Файл больше 1 МБ уезжает в .old — лог не растёт бесконечно.</summary>
    public static class AppLog
    {
        const long MaxBytes = 1_000_000;
        static readonly object Sync = new object();

        public static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArcRecorder");

        /// <summary>Пишет строку в лог (молча глотает ошибки — лог не должен ничего ломать).</summary>
        public static void Write(string fileName, string msg)
        {
            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Dir);
                    string path = Path.Combine(Dir, fileName);
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > MaxBytes) File.Move(path, path + ".old", true);
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {msg}\r\n");
                }
            }
            catch { }
        }

        public static void Error(string where, Exception ex) => Write("error.log", where + ": " + ex);
    }
}
