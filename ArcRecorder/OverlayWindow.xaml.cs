using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ArcRecorder
{
    /// <summary>Боковая панель в стиле NVIDIA: меню функций + страница настроек, выезжает слева по Alt+Z.</summary>
    public partial class OverlayWindow : Window
    {
        readonly App _app;
        bool _loadingUi;
        readonly DispatcherTimer _unclipTimer; // некоторые игры зажимают курсор заново каждый кадр

        public OverlayWindow(App app)
        {
            _app = app;
            _loadingUi = true;
            InitializeComponent();
            Loc.Lang = _app.Settings.Language;
            LoadUiFromSettings();
            ApplyLanguage();

            // Реальное имя видеокарты в шапке (WMI небыстрый — грузим в фоне)
            System.Threading.Tasks.Task.Run(() => GpuInfo.GetGpuShortName())
                .ContinueWith(t => Dispatcher.Invoke(() => GpuSubText.Text = t.Result + " · QSV"));

            Deactivated += (o, e) => HideOverlay();
            // Панель не должна попадать в запись, если её открыли по Alt+Z посреди записи
            SourceInitialized += (o, e) => CaptureExclusion.Apply(this);
            _unclipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _unclipTimer.Tick += (o, e) => ClipCursor(IntPtr.Zero);
        }

        // ---------- win32: отобрать фокус и мышь у полноэкранной игры ----------
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool ClipCursor(IntPtr lpRect); // NULL = снять ограничение курсора
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        /// <summary>
        /// SetForegroundWindow напрямую игра блокирует (foreground lock), поэтому
        /// временно привязываем свой поток ввода к потоку игры — тогда Windows разрешает смену фокуса.
        /// Без этого оверлей рисуется поверх игры, но мышь остаётся у игры и панель некликабельна.
        /// </summary>
        void ForceForeground()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var fg = GetForegroundWindow();
            uint ourThread = GetCurrentThreadId();
            uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;
            if (fg != hwnd && fgThread != 0 && fgThread != ourThread)
            {
                AttachThreadInput(ourThread, fgThread, true);
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally { AttachThreadInput(ourThread, fgThread, false); }
            }
            else SetForegroundWindow(hwnd);
            Activate();
            Focus();
        }

        // ---------- панель: позиционирование слева + анимация ----------
        public void ShowOverlay()
        {
            var area = SystemParameters.WorkArea;
            Height = area.Height;
            Top = area.Top;
            Left = area.Left;
            RefreshStatus();
            Show();
            ForceForeground();
            ClipCursor(IntPtr.Zero); // игра могла зажать курсор в своём окне — освобождаем мышь
            _unclipTimer.Start();
            var slide = new ThicknessAnimation(new Thickness(-Width, 0, Width, 0), new Thickness(0),
                TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            RootPanel.BeginAnimation(MarginProperty, slide);
        }

        public void HideOverlay()
        {
            _unclipTimer.Stop();
            Hide();
        }

        // ---------- переключение страниц: меню <-> настройки ----------
        void ShowMenuPage()
        {
            MenuPage.Visibility = Visibility.Visible;
            SettingsPage.Visibility = Visibility.Collapsed;
        }

        void ShowSettingsPage()
        {
            MenuPage.Visibility = Visibility.Collapsed;
            SettingsPage.Visibility = Visibility.Visible;
        }

        void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (SettingsPage.Visibility == Visibility.Visible) ShowMenuPage();
            else ShowSettingsPage();
        }

        void BackButton_Click(object sender, RoutedEventArgs e) => ShowMenuPage();

        // ---------- язык ----------
        void Language_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi || LanguageBox?.SelectedItem == null) return;
            _app.Settings.Language = TagOf(LanguageBox) ?? "ru";
            _app.Settings.Save();
            Loc.Lang = _app.Settings.Language;
            ApplyLanguage();
            _app.ApplyLanguageToTray();
        }

        /// <summary>Прогоняет все статичные тексты интерфейса через словарь Loc.</summary>
        void ApplyLanguage()
        {
            // низ + меню
            HotkeysText.Text = Loc.T("Hotkeys");
            GalleryTitle.Text = Loc.T("Gallery");
            GallerySubText.Text = Loc.T("GallerySub");
            RecordSubText.Text = Loc.T("RecordSub");
            ReplayTitle.Text = Loc.T("Replay");
            ScreenshotTitle.Text = Loc.T("Screenshot");
            ScreenshotSubText.Text = Loc.T("ScreenshotSub");
            PhotoTitle.Text = Loc.T("PhotoMode");
            PhotoSubText.Text = Loc.T("PhotoSub");
            FpsRowTitle.Text = Loc.T("FpsCounter");
            FpsSubText.Text = Loc.T("FpsCounterSub");
            MicTitle.Text = Loc.T("Mic");
            MicRowSub.Text = Loc.T("MicSub");

            // настройки
            BackButton.Content = Loc.T("Back");
            InterfaceHeader.Text = Loc.T("InterfaceHeader");
            LanguageLabel.Text = Loc.T("Language");
            CaptureHeader.Text = Loc.T("CaptureHeader");
            WhatToRecordLabel.Text = Loc.T("WhatToRecord");
            CapScreenItem.Content = Loc.T("CapScreen");
            CapWindowItem.Content = Loc.T("CapWindow");
            WindowHintText.Text = Loc.T("WindowHint");
            CodecLabel.Text = Loc.T("Codec");
            CodecAv1Item.Content = Loc.T("CodecAv1");
            CodecH264Item.Content = Loc.T("CodecH264");
            ResolutionLabel.Text = Loc.T("Resolution");
            ResNativeItem.Content = Loc.T("ResNative");
            BitrateTitle.Text = Loc.T("Bitrate");
            BitrateLabel.Text = (int)BitrateSlider.Value + " " + Loc.T("Mbps");
            MonitorLabel.Text = Loc.T("Monitor");
            AudioHeader.Text = Loc.T("AudioHeader");
            SystemAudioCheck.Content = Loc.T("SystemAudio");
            MicCheck.Content = Loc.T("Mic");
            ReplayHeader.Text = Loc.T("ReplayHeader");
            ReplayCheck.Content = Loc.T("KeepBuffer");
            BufferLenLabel.Text = Loc.T("BufferLen");
            Min1Item.Content = Loc.T("Min1");
            Min3Item.Content = Loc.T("Min3");
            Min5Item.Content = Loc.T("Min5");
            Min10Item.Content = Loc.T("Min10");
            OverlaysHeader.Text = Loc.T("OverlaysHeader");
            FpsOverlayCheck.Content = Loc.T("FpsCheck");
            FpsPosLabel.Text = Loc.T("FpsPos");
            CornerTL.Content = Loc.T("TL");
            CornerTR.Content = Loc.T("TR");
            CornerBL.Content = Loc.T("BL");
            CornerBR.Content = Loc.T("BR");
            FpsSizeLabel.Text = Loc.T("FpsSize");
            SizeSItem.Content = Loc.T("SizeS");
            SizeMItem.Content = Loc.T("SizeM");
            SizeLItem.Content = Loc.T("SizeL");
            SizeXLItem.Content = Loc.T("SizeXL");
            FpsAutoThemeCheck.Content = Loc.T("AutoTheme");
            FpsHideDesktopCheck.Content = Loc.T("HideDesktop");
            NotificationsCheck.Content = Loc.T("Notifications");

            // подписи мониторов ("главный"/"primary")
            RefreshMonitorNames();

            // динамика (Готов/Запись, вкл/выкл и т.д.)
            RefreshStatus();
        }

        /// <summary>Обновляет подписи в списке мониторов под текущий язык (не трогая выбор).</summary>
        void RefreshMonitorNames()
        {
            foreach (ComboBoxItem item in MonitorBox.Items)
                if (item.Tag is MonitorInfo m)
                {
                    int idx = MonitorBox.Items.IndexOf(item);
                    item.Content = $"{Loc.T("Monitor")} {idx + 1}: {m.Width}x{m.Height}{(m.Primary ? Loc.T("Primary") : "")} — {m.AdapterName}";
                }
        }

        // ---------- настройки ----------
        void LoadUiFromSettings()
        {
            _loadingUi = true;
            var s = _app.Settings;

            SelectByTag(LanguageBox, s.Language);
            SelectByTag(CaptureModeBox, s.CaptureMode);
            SelectByTag(CodecBox, s.Codec);
            SelectByTag(FpsBox, s.Framerate.ToString());
            SelectByTag(ResolutionBox, s.ResolutionHeight.ToString());
            SelectByTag(ReplayLenBox, s.ReplayMinutes.ToString());
            BitrateSlider.Value = s.BitrateMbps;
            BitrateLabel.Text = s.BitrateMbps + " " + Loc.T("Mbps");
            SystemAudioCheck.IsChecked = s.CaptureSystemAudio;
            MicCheck.IsChecked = s.CaptureMicrophone;
            ReplayCheck.IsChecked = s.ReplayEnabled;
            FpsOverlayCheck.IsChecked = s.FpsOverlayEnabled;
            SelectByTag(FpsCornerBox, s.FpsCorner.ToString());
            SelectByTag(FpsScaleBox, s.FpsScalePercent.ToString());
            FpsAutoThemeCheck.IsChecked = s.FpsAutoTheme;
            FpsHideDesktopCheck.IsChecked = s.FpsHideOnDesktop;
            NotificationsCheck.IsChecked = s.ShowNotifications;

            // Мониторы берём из DXGI — тот же порядок, что у ddagrab (Screen.AllScreens даёт другой!)
            MonitorBox.Items.Clear();
            var mons = MonitorService.GetMonitors();
            int sel = 0;
            bool found = false;
            for (int i = 0; i < mons.Count; i++)
            {
                var m = mons[i];
                MonitorBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{Loc.T("Monitor")} {i + 1}: {m.Width}x{m.Height}{(m.Primary ? Loc.T("Primary") : "")} — {m.AdapterName}",
                    Tag = m
                });
                if (m.AdapterIndex == s.AdapterIndex && m.OutputIndex == s.MonitorIndex) { sel = i; found = true; }
            }
            MonitorBox.SelectedIndex = sel;
            // Сохранённого монитора больше нет (отключили) — фиксируем в настройках тот, что реально выбран в списке,
            // иначе UI показывает «Монитор 1», а запись идёт в несуществующий выход
            if (!found && mons.Count > 0)
            {
                s.AdapterIndex = mons[sel].AdapterIndex;
                s.MonitorIndex = mons[sel].OutputIndex;
                s.Save();
            }

            _loadingUi = false;
        }

        static void SelectByTag(ComboBox box, string tag)
        {
            foreach (ComboBoxItem item in box.Items)
                if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
            box.SelectedIndex = 0;
        }

        static string TagOf(ComboBox box) => (string)((ComboBoxItem)box.SelectedItem)?.Tag;

        void SaveUiToSettings()
        {
            if (_loadingUi || CodecBox?.SelectedItem == null || FpsBox?.SelectedItem == null ||
                ResolutionBox?.SelectedItem == null || ReplayLenBox?.SelectedItem == null) return;
            var s = _app.Settings;
            s.CaptureMode = TagOf(CaptureModeBox) ?? "Screen";
            s.Codec = TagOf(CodecBox);
            s.Framerate = int.Parse(TagOf(FpsBox));
            s.ResolutionHeight = int.Parse(TagOf(ResolutionBox));
            s.ReplayMinutes = int.Parse(TagOf(ReplayLenBox));
            s.BitrateMbps = (int)BitrateSlider.Value;
            if (MonitorBox.SelectedItem is ComboBoxItem mi && mi.Tag is MonitorInfo mon)
            {
                s.MonitorIndex = mon.OutputIndex;   // ddagrab output_idx (DXGI)
                s.AdapterIndex = mon.AdapterIndex;  // GPU, которому принадлежит выход
            }
            s.CaptureSystemAudio = SystemAudioCheck.IsChecked == true;
            s.CaptureMicrophone = MicCheck.IsChecked == true;
            s.ReplayEnabled = ReplayCheck.IsChecked == true;
            s.FpsOverlayEnabled = FpsOverlayCheck.IsChecked == true;
            if (FpsCornerBox?.SelectedItem != null) s.FpsCorner = int.Parse(TagOf(FpsCornerBox));
            if (FpsScaleBox?.SelectedItem != null) s.FpsScalePercent = int.Parse(TagOf(FpsScaleBox));
            s.FpsAutoTheme = FpsAutoThemeCheck.IsChecked == true;
            s.FpsHideOnDesktop = FpsHideDesktopCheck.IsChecked == true;
            s.ShowNotifications = NotificationsCheck.IsChecked == true;
            s.Save();
            _app.OnSettingsChanged();
        }

        /// <summary>Настройки FPS-счётчика: сохранить и сразу применить к живому окну счётчика.</summary>
        void FpsSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return;
            SaveUiToSettings();
            _app.ApplyFpsOverlaySettings();
        }

        // ---------- статус ----------
        public void RefreshStatus()
        {
            bool rec = _app.Recorder.IsRecording;
            StatusDot.Fill = rec ? (Brush)FindResource("RecBrush") : (Brush)FindResource("OkBrush");
            StatusText.Text = rec ? Loc.T("Recording") : Loc.T("Ready");
            RecordRowTitle.Text = rec ? Loc.T("StopRecord") : Loc.T("Record");
            RecordChevron.Text = rec ? "⏹" : "▶";
            if (!rec) TimerText.Text = "";

            bool replay = _app.ReplayBuffer.IsRunning;
            bool replayPaused = !replay && rec && _app.Settings.ReplayEnabled;
            ReplayStatusText.Text = replay
                ? Loc.F("ReplayOn", _app.Settings.ReplayMinutes)
                : replayPaused ? Loc.T("ReplayPaused") : Loc.T("ReplayOff");
            ReplayStatusText.Foreground = replay
                ? (Brush)FindResource("OkBrush")
                : (Brush)FindResource("SubTextBrush");
            ReplayRowSub.Text = replay
                ? Loc.F("ReplaySubOn", _app.Settings.ReplayMinutes)
                : Loc.T("ReplaySubOff");
            ReplayRow.IsEnabled = replay;

            FpsRowState.Text = _app.IsFpsOverlayActive ? Loc.T("On") : Loc.T("Off");
            FpsRowState.Foreground = _app.IsFpsOverlayActive
                ? (Brush)FindResource("Accent2Brush")
                : (Brush)FindResource("SubTextBrush");

            MicRowState.Text = _app.Settings.CaptureMicrophone ? Loc.T("On") : Loc.T("Off");
            MicRowState.Foreground = _app.Settings.CaptureMicrophone
                ? (Brush)FindResource("Accent2Brush")
                : (Brush)FindResource("SubTextBrush");

            CodecBox.IsEnabled = FpsBox.IsEnabled = ResolutionBox.IsEnabled = BitrateSlider.IsEnabled =
                MonitorBox.IsEnabled = SystemAudioCheck.IsEnabled = MicCheck.IsEnabled =
                ReplayLenBox.IsEnabled = !rec;
        }

        public void UpdateTimer(TimeSpan elapsed) => TimerText.Text = elapsed.ToString(@"hh\:mm\:ss");

        // ---------- события меню ----------
        void GalleryRow_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(_app.Settings.OutputFolder);
            Process.Start(new ProcessStartInfo(_app.Settings.OutputFolder) { UseShellExecute = true });
        }

        void RecordRow_Click(object sender, RoutedEventArgs e) => _app.ToggleRecording();
        void ReplayRow_Click(object sender, RoutedEventArgs e) => _app.SaveReplay();
        void ScreenshotRow_Click(object sender, RoutedEventArgs e) => _app.TakeScreenshot();
        void PhotoModeRow_Click(object sender, RoutedEventArgs e) => _app.TakeWindowScreenshot();
        void FpsRow_Click(object sender, RoutedEventArgs e) => _app.ToggleFpsOverlay();

        void MicRow_Click(object sender, RoutedEventArgs e)
        {
            if (_app.Recorder.IsRecording) return; // идущую запись это не изменит (и галочка в настройках заблокирована)
            MicCheck.IsChecked = MicCheck.IsChecked != true; // дальше сработает Setting_Changed
            RefreshStatus();
        }

        void Setting_Changed(object sender, RoutedEventArgs e)
        {
            SaveUiToSettings();
            if (IsVisible && !_loadingUi) RefreshStatus();
        }

        void NotificationsCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return;
            SaveUiToSettings();
            if (NotificationsCheck.IsChecked == true) _app.ShowTestToast(); // сразу показать, как это выглядит
        }

        void ReplayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return; // иначе буфер стартовал прямо из конструктора оверлея, ещё до OnStartup
            SaveUiToSettings();
            _app.ApplyReplayEnabled();
            if (IsVisible) RefreshStatus();
        }

        void FpsOverlayCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return;
            SaveUiToSettings();
            _app.ApplyFpsOverlayEnabled();
            SyncFpsCheckbox(); // без прав админа счётчик не включится — вернуть галочку в реальное состояние
        }

        void BitrateSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (BitrateLabel != null) BitrateLabel.Text = (int)e.NewValue + " " + Loc.T("Mbps");
            SaveUiToSettings();
        }

        void CloseButton_Click(object sender, RoutedEventArgs e) => HideOverlay();

        void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (SettingsPage.Visibility == Visibility.Visible) ShowMenuPage();
                else HideOverlay();
            }
        }

        /// <summary>Синхронизировать чекбокс FPS с фактическим состоянием (после Alt+R).</summary>
        public void SyncFpsCheckbox()
        {
            _loadingUi = true;
            FpsOverlayCheck.IsChecked = _app.Settings.FpsOverlayEnabled;
            _loadingUi = false;
            if (IsVisible) RefreshStatus();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            HideOverlay();
        }
    }
}
