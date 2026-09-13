using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
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
    Icon _trayIdleIcon, _trayRecIcon; // светлая точка в простое, красная во время записи
    DispatcherTimer _timer;
    DispatcherTimer _replayRestartTimer; // дебаунс рестарта буфера при смене настроек
    FpsService _fps;
    HwStatsService _hwStats;
    FpsOverlayWindow _fpsWindow;
    DispatcherTimer _fpsTimer;
    volatile bool _statsBusy; // защита от наложения фоновых замеров статистики
    Mutex _singleInstance;
    DateTime _lastErrorBox = DateTime.MinValue;
    ToastWindow _toast;

    // Все операции с ffmpeg (старт/стоп записи и буфера, сохранение повтора) идут строго по очереди в фоне.
    // Остановка ffmpeg занимает секунды; в UI-потоке она морозила интерфейс и тормозила ввод во всей системе.
    // Решение «старт или стоп» принимается в момент выполнения, поэтому двойной Alt+F9 отрабатывает корректно.
    readonly object _opLock = new object();
    Task _opTail = Task.CompletedTask;
    volatile bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SetupErrorHandlers();

        Settings = AppSettings.Load();
        Loc.Lang = Settings.Language; // язык до создания окон

        // Второй экземпляр чистил бы общий буфер повтора первого и перехватывал его ETW-сессию
        _singleInstance = new Mutex(true, @"Local\ArcRecorder_SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            WpfMessageBox.Show(Loc.T("AlreadyRunning"), "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Recorder = new RecordingService();
        ReplayBuffer = new ReplayBufferService();
        Recorder.Failed += (path, err, ran) => UI(() => OnRecordingDied(path, err, ran));
        ReplayBuffer.Failed += (err, ran) => UI(() => OnReplayDied(err, ran));

        _replayRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _replayRestartTimer.Tick += (o, a) =>
        {
            _replayRestartTimer.Stop();
            RestartReplayIfNeeded();
        };

        _overlay = new OverlayWindow(this);

        // Таймер записи (тикает раз в секунду)
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (o, a) =>
        {
            var elapsed = DateTime.Now - Recorder.StartTime;
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

    /// <summary>Необработанное исключение больше не роняет программу молча (вместе с идущей записью).</summary>
    void SetupErrorHandlers()
    {
        DispatcherUnhandledException += (o, a) =>
        {
            AppLog.Error("UI", a.Exception);
            a.Handled = true;
            ShowError(Loc.T("Unexpected") + a.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (o, a) =>
            AppLog.Error("AppDomain", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (o, a) =>
        {
            AppLog.Error("Task", a.Exception);
            a.SetObserved();
        };
    }

    /// <summary>Окно ошибки не чаще раза в 5 сек — ошибка в цикле не должна заваливать экран окнами.</summary>
    void ShowError(string text)
    {
        if ((DateTime.Now - _lastErrorBox).TotalSeconds < 5) return;
        _lastErrorBox = DateTime.Now;
        WpfMessageBox.Show(text, "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void UI(Action a)
    {
        try { Dispatcher.BeginInvoke(a); } catch { }
    }

    /// <summary>
    /// Всплывающее уведомление в углу экрана (вместо балунов трея — их Windows глушит в полноэкранных играх).
    /// Если уведомления выключены в настройках, предупреждения и ошибки всё равно уходят балуном.
    /// </summary>
    void Notify(ToastKind kind, string iconKey, string title, string sub = null)
    {
        if (_exiting) return;
        if (Settings.ShowNotifications)
        {
            _toast ??= new ToastWindow();
            // правый верхний угол: встаём под FPS-счётчик, если он там
            double top = 0;
            if (_fpsWindow != null && _fpsWindow.IsVisible && Settings.FpsCorner == 1) top += _fpsWindow.ActualHeight + 4;
            _toast.ShowToast(kind, TryFindResource(iconKey) as System.Windows.Media.Geometry, title, sub, top);
            return;
        }
        if (kind is ToastKind.Warning or ToastKind.Error)
            _tray?.ShowBalloonTip(5000, title, string.IsNullOrEmpty(sub) ? title : sub,
                                  kind == ToastKind.Error ? WinForms.ToolTipIcon.Error : WinForms.ToolTipIcon.Warning);
    }

    /// <summary>Пример уведомления — при включении галочки в настройках.</summary>
    public void ShowTestToast() => Notify(ToastKind.Info, "IconBell", Loc.T("ToastTest"), Loc.T("ToastTestSub"));

    void RefreshOverlay()
    {
        if (_overlay != null && _overlay.IsVisible) _overlay.RefreshStatus();
    }

    /// <summary>Последняя непустая строка stderr — коротко, для уведомления.</summary>
    static string ErrLine(string err)
    {
        var lines = (err ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string s = lines.Length > 0 ? lines[^1] : "";
        return s.Length > 140 ? s.Substring(0, 140) + "…" : s;
    }

    static string ErrTail(string err)
    {
        err = (err ?? "").Trim();
        return err.Length > 400 ? "…" + err.Substring(err.Length - 400) : err;
    }

    /// <summary>Ставит работу с ffmpeg в фоновую очередь: операции выполняются строго по одной.</summary>
    void Enqueue(Action work)
    {
        lock (_opLock)
            _opTail = _opTail.ContinueWith(_ =>
            {
                try { work(); }
                catch (Exception ex)
                {
                    AppLog.Error("ffmpeg-op", ex);
                    UI(() => ShowError(Loc.T("Unexpected") + ex.Message));
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Иконка из ресурсов сборки в размере трея; если ресурса нет — стандартная.</summary>
    static Icon LoadTrayIcon(string name)
    {
        try
        {
            using var s = typeof(App).Assembly.GetManifestResourceStream(name);
            if (s != null) return new Icon(s, WinForms.SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    void SetupTray()
    {
        _trayIdleIcon = LoadTrayIcon("ArcRecorderIdle.ico");
        _trayRecIcon = LoadTrayIcon("ArcRecorder.ico");
        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIdleIcon,
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
        if (_tray == null) return;
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
        CaptureAfterOverlayHidden(() => ScreenshotService.CaptureMonitor(Settings), Loc.T("ShotSaved"), "IconScreenshot");

    /// <summary>Alt+F2 (фоторежим): снимок активного окна.</summary>
    public void TakeWindowScreenshot() =>
        CaptureAfterOverlayHidden(() => ScreenshotService.CaptureActiveWindow(Settings), Loc.T("WindowShotSaved"), "IconPhoto");

    /// <summary>Прячет оверлей и снимает ПОСЛЕ того, как DWM реально убрал окно с экрана.</summary>
    void CaptureAfterOverlayHidden(Func<string> capture, string okTitle, string iconKey)
    {
        ScreenshotService.Log("Хоткей/кнопка скриншота сработала (" + okTitle + ")");
        if (_overlay.IsVisible) _overlay.HideOverlay(); // не снимать сам оверлей
        // ContextIdle = после прохода рендера WPF; сам захват — на фоне (ffmpeg-скриншот
        // может занять секунду-две, UI-поток морозить нельзя)
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Task.Run(() =>
            {
                try
                {
                    Thread.Sleep(80); // пауза на композицию DWM
                    string path = capture();
                    // Звук — единственный сигнал в фуллскрин-игре: Windows там включает
                    // «Не беспокоить» и глушит балуны из трея
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                    UI(() => Notify(ToastKind.Info, iconKey, okTitle, System.IO.Path.GetFileName(path)));
                }
                catch (Exception ex)
                {
                    ScreenshotService.Log("Захват сдох с исключением: " + ex);
                    try { System.Media.SystemSounds.Hand.Play(); } catch { }
                    UI(() => Notify(ToastKind.Error, "IconAlert", Loc.T("ToastShotFail"), ex.Message));
                }
            });
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

                // держим счётчик на мониторе игры, а не всегда на главном
                _fpsWindow.FollowWindow(WindowCaptureHelper.ForegroundExternalWindow());

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
                Task.Run(() =>
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
                    UI(() =>
                    {
                        if (_fpsWindow == null) return;
                        _fpsWindow.SetStats(fps, gpu, cpu, FpsService.FrameTimeMs(fps));
                        // Автотема с гистерезисом, чтобы не мигало на границе яркости
                        if (wantLuma)
                        {
                            if (luma > 150) _fpsWindow.SetTheme(true);
                            else if (luma >= 0 && luma < 115) _fpsWindow.SetTheme(false);
                        }
                    });
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

    // ---------- буфер повтора ----------

    /// <summary>Включает/выключает буфер повтора согласно настройке (в фоновой очереди).</summary>
    public void ApplyReplayEnabled() => Enqueue(ApplyReplayEnabledCore);

    /// <summary>Выполняется внутри очереди.</summary>
    void ApplyReplayEnabledCore()
    {
        try
        {
            if (Settings.ReplayEnabled && !ReplayBuffer.IsRunning && !Recorder.IsRecording && !_exiting)
                ReplayBuffer.Start(Settings);
            else if (!Settings.ReplayEnabled && ReplayBuffer.IsRunning)
                ReplayBuffer.Stop();
        }
        catch (Exception ex)
        {
            UI(() => WpfMessageBox.Show(Loc.T("ReplayProblem") + ex.Message,
                                     "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        UI(RefreshOverlay);
    }

    /// <summary>
    /// Настройки поменялись. Буфер перезапускаем (он при этом теряет накопленное), только если изменилось
    /// то, что влияет на поток ffmpeg, и с задержкой — ползунок битрейта шлёт событие на каждый шаг.
    /// </summary>
    public void OnSettingsChanged()
    {
        if (!ReplayBuffer.IsRunning || ReplayBuffer.RunningSignature == Settings.CaptureSignature()) return;
        _replayRestartTimer.Stop();
        _replayRestartTimer.Start();
    }

    void RestartReplayIfNeeded() => Enqueue(() =>
    {
        if (_exiting || Recorder.IsRecording || !ReplayBuffer.IsRunning ||
            ReplayBuffer.RunningSignature == Settings.CaptureSignature()) return;
        ReplayBuffer.Stop();
        try { ReplayBuffer.Start(Settings); }
        catch (Exception ex)
        {
            UI(() => WpfMessageBox.Show(Loc.T("ReplayProblem") + ex.Message,
                                     "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        UI(RefreshOverlay);
    });

    /// <summary>Alt+F10: сохранить последние N минут из буфера.</summary>
    public void SaveReplay()
    {
        if (Recorder.IsRecording)
        {
            Notify(ToastKind.Warning, "IconReplay", Loc.T("ToastReplayPaused"), Loc.T("ToastReplayPausedSub"));
            return;
        }
        if (!ReplayBuffer.IsRunning)
        {
            Notify(ToastKind.Warning, "IconReplay", Loc.T("ToastReplayOff"), Loc.T("ToastReplayOffSub"));
            return;
        }
        int minutes = Settings.ReplayMinutes;
        Enqueue(() =>
        {
            ReplaySaveJob job;
            try { job = ReplayBuffer.DetachSegments(); }
            catch (Exception ex)
            {
                UI(() => WpfMessageBox.Show(Loc.T("ReplaySaveFail") + ex.Message,
                                         "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error));
                UI(RefreshOverlay);
                return;
            }
            UI(RefreshOverlay);
            if (job == null)
            {
                UI(() => Notify(ToastKind.Warning, "IconReplay", Loc.T("ToastReplayOff"), Loc.T("ToastReplayOffSub")));
                return;
            }
            if (job.RestartError != null) AppLog.Error("replay-restart", job.RestartError);

            // Склейка — мимо очереди: не задерживает старт записи, если сразу после Alt+F10 нажали Alt+F9
            Task.Run(() =>
            {
                try
                {
                    string path = ReplayBufferService.Concat(job);
                    UI(() =>
                    {
                        if (path == null)
                            Notify(ToastKind.Warning, "IconReplay", Loc.T("ToastReplayEmpty"), Loc.T("ToastReplayEmptySub"));
                        else if (job.RestartError != null)
                            Notify(ToastKind.Warning, "IconReplay", Loc.T("ToastReplaySaved"),
                                   Loc.T("ToastReplayNoRestart") + job.RestartError.Message);
                        else
                            Notify(ToastKind.Info, "IconReplay", Loc.T("ToastReplaySaved"),
                                   Loc.F("ToastReplaySavedSub", minutes, System.IO.Path.GetFileName(path)));
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Error("replay-concat", ex);
                    UI(() => WpfMessageBox.Show(Loc.T("ReplaySaveFail") + ex.Message,
                                             "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error));
                }
            });
        });
    }

    void OnReplayDied(string err, TimeSpan ran)
    {
        RefreshOverlay();
        if (_exiting) return;
        AppLog.Write("error.log", "Буфер повтора: ffmpeg завершился сам: " + err);
        if (ran.TotalSeconds < 5)
            WpfMessageBox.Show(Loc.T("FfmpegFail") + ErrTail(err),
                            "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            Notify(ToastKind.Error, "IconAlert", Loc.T("ToastReplayDied"), ErrLine(err));
    }

    // ---------- запись ----------

    public void ToggleRecording()
    {
        if (_exiting) return;
        if (!Recorder.IsRecording) _overlay.HideOverlay(); // прячем сразу, не дожидаясь остановки буфера

        Enqueue(() =>
        {
            if (Recorder.IsRecording)
            {
                string path = Recorder.Stop(out bool graceful);
                if (path == null) return;
                UI(() =>
                {
                    EndRecordingUi();
                    string file = System.IO.Path.GetFileName(path);
                    if (graceful) Notify(ToastKind.Info, "IconSaved", Loc.T("ToastRecSaved"), file);
                    else Notify(ToastKind.Warning, "IconAlert", Loc.T("ToastRecDamaged"), file);
                });
                ApplyReplayEnabledCore(); // вернуть буфер повтора после записи
                return;
            }

            if (_exiting) return;
            bool windowMode = Settings.CaptureMode == "Window";
            try
            {
                if (ReplayBuffer.IsRunning) ReplayBuffer.Stop(); // GPU-энкодер один — не дерёмся за него
                Recorder.Start(Settings);
            }
            catch (Exception ex)
            {
                UI(() => WpfMessageBox.Show(Loc.T("RecStartFail") + ex.Message,
                                         "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error));
                ApplyReplayEnabledCore();
                return;
            }
            string title = Recorder.LastWindowTitle;
            UI(() => BeginRecordingUi(windowMode, title));
        });
    }

    void BeginRecordingUi(bool windowMode, string windowTitle)
    {
        if (!Recorder.IsRecording) return; // ffmpeg успел умереть — OnRecordingDied уже всё показал
        Notify(ToastKind.Record, "IconRecord", Loc.T("ToastRecStarted"),
               !windowMode ? Loc.T("ToastRecStartedSub")
               : windowTitle != null ? Loc.T("RecWindow") + windowTitle
               : Loc.T("RecWindowFallback"));
        _indicator?.Close();
        _indicator = new RecIndicatorWindow();
        _indicator.Show();
        PlaceIndicator(windowMode);
        _timer.Start();
        if (_tray != null)
        {
            _tray.Text = Loc.T("TrayRec");
            _tray.Icon = _trayRecIcon;
        }
        RefreshOverlay();
    }

    /// <summary>Точка записи — на записываемом мониторе; в режиме окна — на мониторе этого окна.</summary>
    void PlaceIndicator(bool windowMode)
    {
        if (windowMode)
        {
            _indicator.SetMonitorOf(WindowCaptureHelper.ForegroundExternalWindow());
            return;
        }
        var all = MonitorService.GetMonitors();
        var m = MonitorService.Find(Settings) ?? (all.Count > 0 ? all[0] : null); // тот же фолбэк, что у FfmpegArgs
        if (m != null) _indicator.SetMonitorAt(m.Left + m.Width / 2, m.Top + m.Height / 2);
    }

    void EndRecordingUi()
    {
        _timer.Stop();
        _indicator?.Close();
        _indicator = null;
        if (_tray != null)
        {
            _tray.Text = Loc.T("TrayIdle");
            _tray.Icon = _trayIdleIcon;
        }
        RefreshOverlay();
    }

    /// <summary>ffmpeg записи завершился сам: не стартанул, окно закрыли, кончилось место и т.п.</summary>
    void OnRecordingDied(string path, string err, TimeSpan ran)
    {
        EndRecordingUi();
        if (_exiting) return;
        AppLog.Write("error.log", "Запись: ffmpeg завершился сам: " + err);
        if (ran.TotalSeconds < 5)
            WpfMessageBox.Show(Loc.T("FfmpegFail") + ErrTail(err),
                            "ArcRecorder", MessageBoxButton.OK, MessageBoxImage.Error);
        else
            Notify(ToastKind.Error, "IconAlert", Loc.T("ToastRecInterrupted"),
                   System.IO.Path.GetFileName(path) + " · " + ErrLine(err));
        ApplyReplayEnabled();
    }

    // ---------- выход ----------

    void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _replayRestartTimer?.Stop();
        _fpsTimer?.Stop();
        _fpsWindow?.Close();
        _fpsWindow = null;
        _fps?.Dispose();
        _hwStats?.Dispose();
        _hwStats = null;
        _hotkeys?.Dispose();
        _hotkeys = null;
        _overlay?.HideOverlay();
        _timer?.Stop();
        _indicator?.Close();
        _indicator = null;
        _toast?.Close();
        _toast = null;
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        // ffmpeg дописывает файлы в фоне; процесс завершаем, когда очередь опустеет
        Enqueue(() =>
        {
            Recorder.Stop(out _);
            ReplayBuffer.Stop();
        });
        Task tail;
        lock (_opLock) tail = _opTail;
        tail.ContinueWith(_ => UI(Shutdown));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting && Recorder != null)
        {
            // выход мимо ExitApp (выключение/выход из системы): дописываем файлы, пока дают
            _exiting = true;
            try { Recorder.Stop(out _); } catch { }
            try { ReplayBuffer.Stop(); } catch { }
        }
        _tray?.Dispose();
        _hotkeys?.Dispose();
        if (_singleInstance != null)
        {
            try { _singleInstance.ReleaseMutex(); } catch { }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }
}
