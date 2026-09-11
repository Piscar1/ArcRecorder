using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ArcRecorder
{
    /// <summary>
    /// Работа с чужими окнами: цель для записи окна (gdigrab по заголовку)
    /// и детект «на переднем плане рабочий стол» для автоскрытия FPS-счётчика.
    /// </summary>
    public static class WindowCaptureHelper
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        const uint GW_HWNDNEXT = 2;

        // классы окон оболочки Windows: рабочий стол и панели задач
        static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
        static string TitleOf(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }

        /// <summary>true, если на переднем плане рабочий стол или панель задач (счётчик FPS там не нужен).</summary>
        public static bool ForegroundIsDesktop()
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero) return true;
            return Array.IndexOf(ShellClasses, ClassOf(h)) >= 0;
        }

        /// <summary>
        /// Заголовок окна для записи окна: активное окно;
        /// если активны мы сами (клик в оверлее) — первое подходящее по Z-порядку.
        /// null, если подходящего окна нет.
        /// </summary>
        public static string GetCaptureWindowTitle()
        {
            int own = Environment.ProcessId;
            var h = GetForegroundWindow();
            if (Suitable(h, own)) return TitleOf(h);
            for (h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
                if (Suitable(h, own)) return TitleOf(h);
            return null;
        }

        static bool Suitable(IntPtr h, int ownPid)
        {
            if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h)) return false; // свёрнутое gdigrab не возьмёт
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0 || pid == (uint)ownPid) return false;                        // не пишем сами себя
            if (Array.IndexOf(ShellClasses, ClassOf(h)) >= 0) return false;           // не рабочий стол/таскбар
            return TitleOf(h).Length > 0;                                             // gdigrab ищет по заголовку
        }
    }
}
