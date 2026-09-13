using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ArcRecorder
{
    /// <summary>
    /// Индикатор идущей записи: красная точка в правом нижнем углу записываемого монитора (над панелью задач).
    /// Клик-прозрачный, фокус не забирает, в запись и на скриншоты не попадает.
    /// </summary>
    public partial class RecIndicatorWindow : Window
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x80;
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        const double EdgeMarginDip = 10; // отступ от края рабочей области
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        IntPtr _monitor; // Zero — главный монитор

        public RecIndicatorWindow()
        {
            InitializeComponent();
            Left = -10000; // до позиционирования — за экраном, чтобы не мелькнуть в углу по умолчанию
            Top = -10000;
            SizeChanged += (o, e) => Reposition(); // смена DPI при переезде на другой монитор
        }

        /// <summary>Поставить на монитор, которому принадлежит точка (координаты рабочего стола, физические пиксели).</summary>
        public void SetMonitorAt(int x, int y)
        {
            _monitor = MonitorFromPoint(new POINT { X = x, Y = y }, MONITOR_DEFAULTTONEAREST);
            Reposition();
        }

        /// <summary>Поставить на монитор окна (режим записи окна). Zero — оставить главный.</summary>
        public void SetMonitorOf(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero) _monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            Reposition();
        }

        void Reposition()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr)) return;
            var mon = _monitor != IntPtr.Zero ? _monitor : MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;
            int margin = (int)Math.Round(EdgeMarginDip * VisualTreeHelper.GetDpi(this).DpiScaleX);
            int x = mi.rcWork.Right - (wr.Right - wr.Left) - margin;
            int y = mi.rcWork.Bottom - (wr.Bottom - wr.Top) - margin;
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            CaptureExclusion.Apply(this); // точка видна на экране, но не в записи и не на скриншотах
        }
    }
}
