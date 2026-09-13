using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ArcRecorder
{
    /// <summary>
    /// Прячет наши окна (оверлей, REC-индикатор, FPS-счётчик) от любого захвата экрана:
    /// DXGI Desktop Duplication (ddagrab), GDI BitBlt (gdigrab, CopyFromScreen), WGC.
    /// На экране окно видно, в записи и на скриншотах — нет.
    /// </summary>
    public static class CaptureExclusion
    {
        const uint WDA_EXCLUDEFROMCAPTURE = 0x11; // Windows 10 2004+

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        /// <summary>Звать после появления HWND (OnSourceInitialized). На старых Windows молча ничего не делает.</summary>
        public static void Apply(Window w)
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            // WDA_MONITOR как фолбэк не берём: он рисует в записи чёрный прямоугольник — это хуже, чем ничего
            try { SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); } catch { }
        }
    }
}
