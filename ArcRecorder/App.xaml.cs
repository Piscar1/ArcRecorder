using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace ArcRecorder;

/// <summary>
/// Главный класс: трей, глобальные хоткеи (Alt+Z оверлей, Alt+F9 запись, Alt+F10 повтор,
/// Alt+F1 снимок, Alt+F2 фоторежим, Alt+R счётчик FPS), оркестрация.
/// </summary>
public partial class App : System.Windows.Application
{
    const uint VK_Z = 0x5A;
    const uint VK_R = 0x52;
    const uint VK_F1 = 0x70;
    const uint VK_F2 = 0x71;
    const uint VK_F9 = 0x78;
    const uint VK_F10 = 0x79;

    public AppSettings Settings { get; private set; }
    public RecordingService Recorder { get; private set; }
    public ReplayBufferService ReplayBuffer { get; private set; }
    public bool IsFpsOverlayActive => _fpsWindow != null;

    OverlayWindow _overlay;
    RecIndicatorWindow _indicator;
    HotkeyManager _hotkeys;
    WinForms.NotifyIcon _tray;
    DispatcherTimer _timer;
    FpsService _fps;
    HwStatsService _hwStats;
    FpsOverlayWindow _fpsWindow;
    DispatcherTimer _fpsTimer;
    volatile bool _statsBusy; // защита от наложения фоновых замеров статистики

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Settings = AppSettings.Load();
        Loc.Lang = Settings.Language; // язык до создания окон
        Recorder = new RecordingService();
        ReplayBuffer = new ReplayBufferService();

        _overlay = new OverlayWindow(this);

        // Таймер записи (тикает раз в секунду)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (o, a) =>
        {
            var elapsed = DateTime.Now - Recorder.StartTime;
            _indicator?.UpdateTimer(elapsed);
            if (_overlay.IsVisible) _overlay.UpdateTimer(elapsed);
        };

        // Глобальные хоткеи
        _hotkeys = new HotkeyManager();
        bool okZ = _hotkeys.Register(HotkeyManager.MOD_ALT, VK_Z, ToggleOverlay);
        bool okF9 = _hotkeys.Register(HotkeyManager.MOD_ALT, VK_F9, ToggleRecording);
        bool okF10 = _hotkeys.Register(HotkeyManager.MOD_ALT, VK_F10, SaveReplay);
        bool okF1 = _hotkeys.Register(HotkeyManager.MOD_ALT, VK_F1, TakeScreenshot);
        bool okF2 = _hotkeys.Register(HotkeyManager.MOD_ALT, VK_F2, TakeWindowScreenshot);
        bool okR = _hotkeys.Register(HotkeyManager.MOD_ALT, VK_R, ToggleFpsOverlay);
        if (!okZ || !okF9 || !okF10 || !okF1 || !okF2 || !okR)
            WpfMessageBox.Show(Loc.T("HotkeyFail"),
                            "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);

        _fps = new FpsService();

        SetupTray();
        ApplyReplayEnabled();     // стартуем буфер повтора, если включён
        ApplyFpsOverlayEnabled(); // включаем FPS-счётчик, если был включён
        ToggleOverlay();          // показать оверлей при первом запуске
    }

    void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = Loc.T("TrayIdle"),
            Visible = true
        };
        BuildTrayMenu();
        _tray.DoubleClick += (o, a) => ToggleOverlay();
    }

    /// <summary>(Пере)строит меню трея на текущем языке.</summary>
    void BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(Loc.T("TrayOverlay"), null, (o, a) => ToggleOverlay());
        menu.Items.Add(Loc.T("TrayRecord"), null, (o, a) => ToggleRecording());
        menu.Items.Add(Loc.T("TraySaveReplay"), null, (o, a) => SaveReplay());
        menu.Items.Add(Loc.T("TrayScreenshot"), null, (o, a) => TakeScreenshot());
        menu.Items.Add(Loc.T("TrayPhoto"), null, (o, a) => TakeWindowScreenshot());
        menu.Items.Add(Loc.T("TrayFps"), null, (o, a) => ToggleFpsOverlay());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("TrayFolder"), null, (o, a) =>
        {
            System.IO.Directory.CreateDirectory(Settings.OutputFolder);
            Process.Start(new ProcessStartInfo(Settings.OutputFolder) { UseShellExecute = true });
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("TrayExit"), null, (o, a) => ExitApp());
        _tray.ContextMenuStrip?.Dispose();
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>Вызывается оверлеем после смены языка: перестроить трей на новом языке.</summary>
    public void ApplyLanguageToTray()
    {
        BuildTrayMenu();
        _tray.Text = Recorder.IsRecording ? Loc.T("TrayRec") : Loc.T("TrayIdle");
    }

    public void ToggleOverlay()
    {
        if (_overlay.IsVisible) _overlay.HideOverlay();
        else _overlay.ShowOverlay();
    }

    // ---------- скриншоты ----------

    /// <summary>Alt+F1: снимок выбранного монитора.</summary>
    public void TakeScreenshot() =>
        CaptureAfterOverlayHidden(() => ScreenshotService.CaptureMonitor(Settings), Loc.T("ShotSaved"));

    /// <summary>Alt+F2 (фоторежим): снимок активного окна.</summary>
    public void TakeWindowScreenshot() =>
        CaptureAfterOverlayHidden(() => ScreenshotService.CaptureActiveWindow(Settings), Loc.T("WindowShotSaved"));

    /// <summary>Прячет оверлей и снимает ПОСЛЕ того, как DWM реально убрал окно с экрана.</summary>
    void CaptureAfterOverlayHidden(Func<string> capture, string okTitle)
    {
        if (_overlay.IsVisible) _overlay.HideOverlay(); // не снимать сам оверлей
        // ContextIdle = после прохода рендера WPF; плюс небольшая пауза на композицию DWM
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                System.Threading.Thread.Sleep(80);
                string path = capture();
                _tray.ShowBalloonTip(3000, "ArcRecorder", okTitle + ":\n" + path, WinForms.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(3000, "ArcRecorder", Loc.T("ShotFail") + ex.Message, WinForms.ToolTipIcon.Error);
            }
        }), DispatcherPriority.ContextIdle);
    }

    // ---------- FPS-счётчик ----------

    /// <summary>Alt+R: включить/выключить FPS-счётчик и запомнить в настройках.</summary>
    public void ToggleFpsOverlay()
    {
        Settings.FpsOverlayEnabled = !IsFpsOverlayActive;
        Settings.Save();
        ApplyFpsOverlayEnabled();
        _overlay.SyncFpsCheckbox();
    }

    /// <summary>Применяет Settings.FpsOverlayEnabled: стартует/останавливает ETW и окно счётчика.</summary>
    public void ApplyFpsOverlayEnabled()
    {
        if (Settings.FpsOverlayEnabled && _fpsWindow == null)
        {
            try
            {
                _fps.Start(); // ETW требует прав администратора
            }
            catch (Exception ex)
            {
                Settings.FpsOverlayEnabled = false;
                Settings.Save();
                WpfMessageBox.Show(Loc.T("FpsAdmin") + ex.Message,
                                "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _hwStats = new HwStatsService();
            _fpsWindow = new FpsOverlayWindow();
            _fpsWindow.Show();
            _fpsWindow.ApplySettings(Settings); // угол + масштаб из настроек
            _fpsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _fpsTimer.Tick += (o, a) =>
            {
                _fps.UpdateForegroundPid();
                int fps = _fps.TakeFps();
                if (_fpsWindow == null) return;

                // «Только в играх»: на рабочем столе/таскбаре счётчик прячем
                bool hide = Settings.FpsHideOnDesktop && WindowCaptureHelper.ForegroundIsDesktop();
                _fpsWindow.Visibility = hide ? Visibility.Hidden : Visibility.Visible;
                if (hide || _statsBusy) return;

                // Тяжёлое (перф-каунтеры GPU, GDI-readback для автотемы) — на фоновом потоке,
                // чтобы тик таймера не подвешивал UI и не дёргал плавность игры
                bool wantLuma = Settings.FpsAutoTheme;
                var rect = wantLuma ? _fpsWindow.GetScreenRect() : default;
                var hw = _hwStats;
                _statsBusy = true;
                System.Threading.Tasks.Task.Run(() =>
                {
                    int gpu = -1, cpu = -1; double luma = -1;
                    try
                    {
                        gpu = hw?.GetGpuUsage() ?? -1;
                        cpu = hw?.GetCpuUsage() ?? -1;
                        if (wantLuma) luma = FpsOverlayWindow.SampleLuma(rect);
                    }
                    catch { }
                    finally { _statsBusy = false; }
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (_fpsWindow == null) return;
                        _fpsWindow.SetStats(fps, gpu, cpu, FpsService.FrameTimeMs(fps));
                        // Автотема с гистерезисом, чтобы не мигало на границе яркости
                        if (wantLuma)
                        {
                            if (luma > 150) _fpsWindow.SetTheme(true);
                            else if (luma >= 0 && luma < 115) _fpsWindow.SetTheme(false);
                        }
                    }));
                });
            };
            _fps.UpdateForegroundPid();
            _fpsTimer.Start();
        }
        else if (!Settings.FpsOverlayEnabled && _fpsWindow != null)
        {
            _fpsTimer?.Stop();
            _fpsTimer = null;
            _fpsWindow.Close();
            _fpsWindow = null;
            _fps.Stop();
            _hwStats?.Dispose();
            _hwStats = null;
        }
    }

    /// <summary>Применить настройки FPS-счётчика (угол/масштаб/тема) к уже открытому окну.</summary>
    public void ApplyFpsOverlaySettings()
    {
        if (_fpsWindow == null) return;
        _fpsWindow.ApplySettings(Settings);
        if (!Settings.FpsAutoTheme) _fpsWindow.SetTheme(false);       // выключили автоцвет — вернуть светлый текст
        if (!Settings.FpsHideOnDesktop) _fpsWindow.Visibility = Visibility.Visible;
    }

    /// <summary>Отложенная проверка, что ffmpeg не упал сразу после старта (не морозит UI).</summary>
    void VerifyLater(Func<(bool ok, string err)> check, Action onFail)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        t.Tick += (o, a) =>
        {
            t.Stop();
            var (ok, err) = check();
            if (!ok)
            {
                onFail?.Invoke();
                WpfMessageBox.Show("ffmpeg не стартанул:\n" + err,
                                "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        t.Start();
    }

    /// <summary>Включает/выключает буфер повтора согласно настройке.</summary>
    public void ApplyReplayEnabled()
    {
        try
        {
            if (Settings.ReplayEnabled && !ReplayBuffer.IsRunning && !Recorder.IsRecording)
            {
                ReplayBuffer.Start(Settings);
                VerifyLater(
                    () => { bool ok = ReplayBuffer.VerifyRunning(out string err); return (ok, err); },
                    () => { if (_overlay.IsVisible) _overlay.RefreshStatus(); });
            }
            else if (!Settings.ReplayEnabled && ReplayBuffer.IsRunning)
                ReplayBuffer.Stop();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Loc.T("ReplayProblem") + ex.Message,
                            "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Настройки поменялись: если буфер повтора крутится — перезапускаем с новыми параметрами.</summary>
    public void OnSettingsChanged()
    {
        if (ReplayBuffer.IsRunning && !Recorder.IsRecording)
        {
            try { ReplayBuffer.Stop(); ReplayBuffer.Start(Settings); } catch { }
        }
    }

    /// <summary>Alt+F10: сохранить последние N минут из буфера.</summary>
    public void SaveReplay()
    {
        if (!ReplayBuffer.IsRunning)
        {
            _tray.ShowBalloonTip(3000, "ArcRecorder", Loc.T("ReplayOffWarn"), WinForms.ToolTipIcon.Warning);
            return;
        }
        try
        {
            string path = ReplayBuffer.SaveReplay();
            if (path != null)
                _tray.ShowBalloonTip(4000, "ArcRecorder", Loc.F("ReplaySaved", Settings.ReplayMinutes) + path, WinForms.ToolTipIcon.Info);
            else
                _tray.ShowBalloonTip(3000, "ArcRecorder", Loc.T("BufferEmpty"), WinForms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(Loc.T("ReplaySaveFail") + ex.Message,
                            "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        if (_overlay.IsVisible) _overlay.RefreshStatus();
    }

    public void ToggleRecording()
    {
        if (Recorder.IsRecording)
        {
            string path = Recorder.Stop();
            _timer.Stop();
            _indicator?.Close();
            _indicator = null;
            _tray.Text = Loc.T("TrayIdle");
            _tray.ShowBalloonTip(4000, "ArcRecorder", Loc.T("RecSaved") + path, WinForms.ToolTipIcon.Info);
            ApplyReplayEnabled(); // вернуть буфер повтора после записи
        }
        else
        {
            try
            {
                if (ReplayBuffer.IsRunning) ReplayBuffer.Stop(); // GPU-энкодер один — не дерёмся за него
                Recorder.Start(Settings);
                if (Settings.CaptureMode == "Window")
                    _tray.ShowBalloonTip(3000, "ArcRecorder",
                        Recorder.LastWindowTitle != null
                            ? Loc.T("RecWindow") + Recorder.LastWindowTitle
                            : Loc.T("RecWindowFallback"),
                        WinForms.ToolTipIcon.Info);
                _indicator = new RecIndicatorWindow();
                _indicator.Show();
                _timer.Start();
                _overlay.HideOverlay(); // прячем оверлей, чтобы он не попал в запись
                _tray.Text = Loc.T("TrayRec");
                // проверяем через секунду, что ffmpeg не упал (без Thread.Sleep на UI)
                VerifyLater(
                    () => { bool ok = Recorder.VerifyRunning(out string err); return (ok, err); },
                    () =>
                    {
                        _timer.Stop();
                        _indicator?.Close();
                        _indicator = null;
                        _tray.Text = Loc.T("TrayIdle");
                        ApplyReplayEnabled();
                        if (_overlay.IsVisible) _overlay.RefreshStatus();
                    });
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(Loc.T("RecStartFail") + ex.Message,
                                "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
                ApplyReplayEnabled();
            }
        }
        if (_overlay.IsVisible) _overlay.RefreshStatus();
    }

    void ExitApp()
    {
        if (Recorder.IsRecording) Recorder.Stop();
        if (ReplayBuffer.IsRunning) ReplayBuffer.Stop();
        _fpsTimer?.Stop();
        _fpsWindow?.Close();
        _fps?.Dispose();
        _hwStats?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _tray = null;
        _hotkeys.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose(); // на случай выхода мимо ExitApp
        base.OnExit(e);
    }
}

