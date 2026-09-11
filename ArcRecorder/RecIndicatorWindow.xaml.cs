using System;
using System.Windows;
using System.Windows.Media;

namespace ArcRecorder
{
    /// <summary>Маленький индикатор "REC 00:00:00" в правом верхнем углу экрана.</summary>
    public partial class RecIndicatorWindow : Window
    {
        bool _blink;

        public RecIndicatorWindow()
        {
            InitializeComponent();
            Loaded += (o, e) =>
            {
                var area = SystemParameters.WorkArea;
                Left = area.Right - ActualWidth - 12;
                Top = area.Top + 12;
            };
        }

        public void UpdateTimer(TimeSpan elapsed)
        {
            RecTimer.Text = "REC " + elapsed.ToString(@"hh\:mm\:ss");
            _blink = !_blink;
            RecDot.Fill = new SolidColorBrush(_blink ? Color.FromRgb(0xE5, 0x39, 0x35) : Color.FromRgb(0x7A, 0x1F, 0x1D));
        }
    }
}
