using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace ArcRecorder
{
    /// <summary>
    /// Клик-прозрачная статистика в углу экрана как у NVIDIA App:
    /// FPS 117 | GPU 88 % | CPU 31 % | LAT 51.8 ms
    /// </summary>
    public class FpsOverlayWindow : Window
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x80;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        // Тема «тёмный фон» (по умолчанию): светлый текст + чёрная тень
        static readonly Brush DarkLabelBrush = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xB8));
        static readonly Brush DarkValueBrush = Brushes.White;
        static readonly Brush DarkSepBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        // Тема «светлый фон»: тёмный текст + белая тень (чтоб читалось на белом)
        static readonly Brush LightLabelBrush = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
        static readonly Brush LightValueBrush = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14));
        static readonly Brush LightSepBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

        TextBlock _fpsVal, _gpuVal, _cpuVal, _latVal;
        readonly System.Collections.Generic.List<TextBlock> _labels = new();
        readonly System.Collections.Generic.List<TextBlock> _values = new();
        readonly System.Collections.Generic.List<Border> _seps = new();
        System.Windows.Media.Effects.DropShadowEffect _shadow;
        int _corner = 1;          // 0 TL, 1 TR, 2 BL, 3 BR
        bool _lightTheme;         // текущая тема (true = тёмный текст на светлом фоне)

        Brush LabelBrush => _lightTheme ? LightLabelBrush : DarkLabelBrush;
        Brush ValueBrush => _lightTheme ? LightValueBrush : DarkValueBrush;

        public FpsOverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Background = Brushes.Transparent;
            Left = 12;
            Top = 12;

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(Metric("FPS", out _fpsVal));
            row.Children.Add(Sep());
            row.Children.Add(Metric("GPU", out _gpuVal, "%"));
            row.Children.Add(Sep());
            row.Children.Add(Metric("CPU", out _cpuVal, "%"));
            row.Children.Add(Sep());
            row.Children.Add(Metric("LAT", out _latVal, "ms"));

            // Без подложки — только текст с тенью, чтобы читался на любом фоне
            _shadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 4,
                ShadowDepth = 1,
                Opacity = 0.9
            };
            row.Effect = _shadow;
            Content = row;

            // Перепозиционировать в выбранный угол, когда размер посчитан
            SizeChanged += (o, e) => Reposition();
        }

        /// <summary>Применить настройки: угол экрана и масштаб (75–150 %).</summary>
        public void ApplySettings(AppSettings s)
        {
            _corner = s.FpsCorner;
            double scale = Math.Clamp(s.FpsScalePercent, 50, 200) / 100.0;
            ((FrameworkElement)Content).LayoutTransform = new ScaleTransform(scale, scale);
            Reposition();
        }

        void Reposition()
        {
            var area = SystemParameters.WorkArea;
            Left = _corner is 0 or 2 ? area.Left + 14 : area.Right - ActualWidth - 14;
            Top = _corner is 0 or 1 ? area.Top + 10 : area.Bottom - ActualHeight - 10;
        }

        /// <summary>Сменить тему: light=true — тёмный текст (для светлого фона).</summary>
        public void SetTheme(bool light)
        {
            if (_lightTheme == light) return;
            _lightTheme = light;
            foreach (var t in _labels) t.Foreground = LabelBrush;
            foreach (var t in _values) t.Foreground = ValueBrush;
            foreach (var b in _seps) b.Background = light ? LightSepBrush : DarkSepBrush;
            _shadow.Color = light ? Colors.White : Colors.Black;
        }

        /// <summary>Блок «подпись + значение (+ единица)» в стиле NVIDIA.</summary>
        StackPanel Metric(string label, out TextBlock value, string unit = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = LabelBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 5, 0)
            };
            _labels.Add(lbl);
            sp.Children.Add(lbl);
            value = new TextBlock
            {
                Text = "--",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                Foreground = ValueBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            _values.Add(value);
            sp.Children.Add(value);
            if (unit != null)
            {
                var u = new TextBlock
                {
                    Text = unit,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI"),
                    Foreground = LabelBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 2, 0, 0)
                };
                _labels.Add(u);
                sp.Children.Add(u);
            }
            return sp;
        }

        Border Sep()
        {
            var b = new Border
            {
                Width = 1,
                Height = 14,
                Background = DarkSepBrush,
                Margin = new Thickness(9, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _seps.Add(b);
            return b;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        }

        /// <summary>Экранный прямоугольник счётчика в физических пикселях (звать с UI-потока).</summary>
        public System.Drawing.Rectangle GetScreenRect()
        {
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            return new System.Drawing.Rectangle(
                (int)(Left * sx), (int)(Top * sy),
                Math.Max(8, (int)(ActualWidth * sx)), Math.Max(8, (int)(ActualHeight * sy)));
        }

        /// <summary>
        /// Средняя яркость участка экрана (0–255) для автотемы. -1 при ошибке.
        /// Статик + LockBits: зовётся с ФОНОВОГО потока, чтобы GDI-readback
        /// (CopyFromScreen синхронизирует GPU) не фризил UI и не дёргал игру лишний раз.
        /// </summary>
        public static double SampleLuma(System.Drawing.Rectangle r)
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(r.Width, r.Height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, r.Width, r.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                double sum = 0; int n = 0;
                try
                {
                    int stepX = Math.Max(1, r.Width / 16), stepY = Math.Max(1, r.Height / 4);
                    unsafe
                    {
                        byte* basePtr = (byte*)data.Scan0;
                        for (int iy = 0; iy < r.Height; iy += stepY)
                        {
                            byte* row = basePtr + iy * data.Stride;
                            for (int ix = 0; ix < r.Width; ix += stepX)
                            {
                                byte* px = row + ix * 4; // BGRA
                                sum += 0.0722 * px[0] + 0.7152 * px[1] + 0.2126 * px[2];
                                n++;
                            }
                        }
                    }
                }
                finally { bmp.UnlockBits(data); }
                return n > 0 ? sum / n : -1;
            }
            catch { return -1; }
        }

        /// <summary>Обновить все показатели (fps=0 или -1 → «--»).</summary>
        public void SetStats(int fps, int gpu, int cpu, double latMs)
        {
            _fpsVal.Text = fps > 0 ? fps.ToString() : "--";
            _gpuVal.Text = gpu >= 0 ? gpu.ToString() : "--";
            _cpuVal.Text = cpu >= 0 ? cpu.ToString() : "--";
            _latVal.Text = latMs > 0 ? latMs.ToString("0.0") : "--";
        }
    }
}
