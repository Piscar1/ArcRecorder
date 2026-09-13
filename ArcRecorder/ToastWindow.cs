using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ArcRecorder
{
    public enum ToastKind { Info, Record, Warning, Error }

    /// <summary>
    /// Всплывающее уведомление в правом верхнем углу монитора игры: «Запись запущена», «Повтор сохранён»…
    /// Стиль панели: тёмная подложка, intel-градиент слева, line-иконка, тонкая полоска-таймер снизу.
    /// Клик-прозрачное, фокус у игры не забирает, в запись/скриншоты не попадает. Новое заменяет текущее.
    /// </summary>
    public class ToastWindow : Window
    {
        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x80;
        const uint MONITOR_DEFAULTTOPRIMARY = 1;
        const uint MONITOR_DEFAULTTONEAREST = 2;
        const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        const int ShowMs = 3500;      // обычное уведомление
        const int LongShowMs = 6000;  // предупреждения и ошибки — дольше, чтобы успеть прочитать
        const double CardMargin = 18; // поля окна вокруг карточки — место под тень

        static readonly Color IntelBlue = Color.FromRgb(0x00, 0x68, 0xB5);
        static readonly Color IntelCyan = Color.FromRgb(0x00, 0xA3, 0xD9);
        static readonly Color RecRed = Color.FromRgb(0xE5, 0x39, 0x35);
        static readonly Color RecDark = Color.FromRgb(0x9E, 0x1B, 0x1B);
        static readonly Color Amber = Color.FromRgb(0xF5, 0xA6, 0x23);
        static readonly Color AmberDark = Color.FromRgb(0xB0, 0x6A, 0x00);

        readonly Border _bar;
        readonly Border _iconPlate;
        readonly Path _icon;
        readonly Ellipse _recDot;
        readonly TextBlock _title, _sub;
        readonly Border _progress;
        readonly ScaleTransform _progressScale = new ScaleTransform(1, 1);
        readonly TranslateTransform _slide = new TranslateTransform();
        readonly DispatcherTimer _hold = new DispatcherTimer();
        int _generation;
        IntPtr _monitor;
        double _topOffsetDip;

        public ToastWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            Left = -10000; // до первого позиционирования — за экраном
            Top = -10000;
            Opacity = 0;

            var textBrush = Res("TextBrush", Color.FromRgb(0xE8, 0xE8, 0xEA));
            var subBrush = Res("SubTextBrush", Color.FromRgb(0x8E, 0x90, 0x99));
            var lineBrush = Res("LineBrush", Color.FromRgb(0x2A, 0x2C, 0x33));
            var font = new FontFamily("Segoe UI");

            _bar = new Border { Width = 3, CornerRadius = new CornerRadius(3, 0, 0, 3) };

            _icon = new Path
            {
                StrokeThickness = 1.6,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            _recDot = new Ellipse
            {
                Width = 7,
                Height = 7,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            var iconGrid = new Grid { Width = 20, Height = 20 };
            iconGrid.Children.Add(_icon);
            iconGrid.Children.Add(_recDot);
            _iconPlate = new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(19),
                Child = iconGrid,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(14, 0, 12, 0)
            };

            var caption = new TextBlock
            {
                Text = "ARC RECORDER",
                FontFamily = font,
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = subBrush,
                Margin = new Thickness(0, 0, 0, 1)
            };
            _title = new TextBlock
            {
                FontFamily = font,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = textBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _sub = new TextBlock
            {
                FontFamily = font,
                FontSize = 11.5,
                Foreground = subBrush,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 46, // не больше трёх строк
                Margin = new Thickness(0, 2, 0, 0)
            };
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 18, 12) };
            texts.Children.Add(caption);
            texts.Children.Add(_title);
            texts.Children.Add(_sub);

            var row = new DockPanel();
            DockPanel.SetDock(_bar, Dock.Left);
            DockPanel.SetDock(_iconPlate, Dock.Left);
            row.Children.Add(_bar);
            row.Children.Add(_iconPlate);
            row.Children.Add(texts);

            // полоска-таймер снизу, как фирменная intel-линия под шапкой панели
            _progress = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3, 0, 3, 0),
                RenderTransform = _progressScale,
                RenderTransformOrigin = new Point(0, 0.5)
            };

            var layers = new Grid();
            layers.Children.Add(row);
            layers.Children.Add(_progress);

            Content = new Border
            {
                Background = Res("ToastBg", Color.FromArgb(0xF2, 0x1B, 0x1D, 0x22)),
                BorderBrush = lineBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                MinWidth = 300,
                MaxWidth = 400,
                Margin = new Thickness(CardMargin),
                Child = layers,
                RenderTransform = _slide,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.55 }
            };

            _hold.Tick += (o, e) =>
            {
                _hold.Stop();
                FadeOut();
            };
            Closed += (o, e) => _hold.Stop();
            // прижаты к правому краю: сменился текст → сменилась ширина → пересчитать X
            SizeChanged += (o, e) => { if (IsVisible) Reposition(); };
        }

        static Brush Res(string key, Color fallback) =>
            Application.Current?.TryFindResource(key) as Brush ?? Frozen(new SolidColorBrush(fallback));

        static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        /// <summary>
        /// Показать (или заменить текущее) уведомление.
        /// topOffsetDip — сдвиг вниз, чтобы не залезать на REC-индикатор и FPS-счётчик в том же углу.
        /// </summary>
        public void ShowToast(ToastKind kind, Geometry icon, string title, string sub, double topOffsetDip = 0)
        {
            int ms = kind is ToastKind.Warning or ToastKind.Error ? LongShowMs : ShowMs;
            var (dark, bright) = kind switch
            {
                ToastKind.Record => (RecDark, RecRed),
                ToastKind.Error => (RecDark, RecRed),
                ToastKind.Warning => (AmberDark, Amber),
                _ => (IntelBlue, IntelCyan)
            };

            _generation++;
            _bar.Background = Frozen(new LinearGradientBrush(bright, dark, 90));
            _progress.Background = Frozen(new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(dark, 0),
                new GradientStop(bright, 0.6),
                new GradientStop(Color.FromArgb(0, bright.R, bright.G, bright.B), 1)
            }, new Point(0, 0), new Point(1, 0)));
            var accent = Frozen(new SolidColorBrush(bright));
            _icon.Data = icon;
            _icon.Stroke = accent;
            _recDot.Fill = accent;
            _recDot.Visibility = kind == ToastKind.Record ? Visibility.Visible : Visibility.Collapsed;
            _iconPlate.Background = Frozen(new SolidColorBrush(Color.FromArgb(0x26, bright.R, bright.G, bright.B)));
            _title.Text = title ?? "";
            _sub.Text = sub ?? "";
            _sub.Visibility = string.IsNullOrEmpty(sub) ? Visibility.Collapsed : Visibility.Visible;

            // монитор игры (активного чужого окна); если активны мы сами — главный
            var fg = WindowCaptureHelper.ForegroundExternalWindow();
            _monitor = fg != IntPtr.Zero ? MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
            _topOffsetDip = topOffsetDip;

            bool alreadyOnScreen = IsVisible && Opacity > 0.01;
            if (!IsVisible) Show();
            Reposition();

            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            if (!alreadyOnScreen) // въезд от правого края; замена на лету — без повторного въезда
                _slide.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            _progressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ms)));

            _hold.Stop();
            _hold.Interval = TimeSpan.FromMilliseconds(ms);
            _hold.Start();
        }

        void FadeOut()
        {
            int gen = _generation;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (o, e) => { if (gen == _generation) Hide(); }; // за время затухания могло прийти новое
            BeginAnimation(OpacityProperty, fade);
        }

        /// <summary>Правый верхний угол рабочей области монитора (в физических пикселях) + поверх всех topmost-окон.</summary>
        void Reposition()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var wr)) return;
            var mon = _monitor != IntPtr.Zero ? _monitor : MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int x = mi.rcWork.Right - (wr.Right - wr.Left); // поля окна (18 dip под тень) дают отступ от края
            int y = mi.rcWork.Top + (int)Math.Round(_topOffsetDip * dpi);
            // HWND_TOPMOST каждый раз: игра или другой оверлей могли встать выше
            SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE,
                GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            CaptureExclusion.Apply(this); // уведомление не должно попасть в запись или на скриншот
        }
    }
}
