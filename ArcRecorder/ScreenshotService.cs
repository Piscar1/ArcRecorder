using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace ArcRecorder
{
    /// <summary>Скриншоты: весь монитор (Alt+F1) или активное окно (Alt+F2, "фоторежим").</summary>
    public static class ScreenshotService
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        public static string ScreenshotFolder(AppSettings s) => Path.Combine(s.OutputFolder, "Screenshots");

        /// <summary>Alt+F1: снимок выбранного в настройках монитора (координаты из DXGI, как у записи).</summary>
        public static string CaptureMonitor(AppSettings s)
        {
            var m = MonitorService.Find(s) ?? MonitorService.GetMonitors()[0];
            return Capture(new Rectangle(m.Left, m.Top, m.Width, m.Height), s);
        }

        /// <summary>Alt+F2 ("фоторежим"): снимок активного окна (границы через DWM, без тени).</summary>
        public static string CaptureActiveWindow(AppSettings s)
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return CaptureMonitor(s);

            RECT r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);

            var b = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (b.Width <= 0 || b.Height <= 0) return CaptureMonitor(s);
            return Capture(b, s);
        }

        static string Capture(Rectangle b, AppSettings s)
        {
            string dir = ScreenshotFolder(s);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
            using (var bmp = new Bitmap(b.Width, b.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                bmp.Save(path, ImageFormat.Png);
            }
            return path;
        }
    }
}
