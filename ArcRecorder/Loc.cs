using System.Collections.Generic;

namespace ArcRecorder
{
    /// <summary>Локализация RU/EN. Lang ставится из AppSettings.Language при старте и при смене в настройках.</summary>
    public static class Loc
    {
        public static string Lang = "ru"; // "ru" | "en"

        /// <summary>Перевод по ключу: T("Gallery") → "Галерея" или "Gallery".</summary>
        public static string T(string key) =>
            _s.TryGetValue(key, out var v) ? (Lang == "en" ? v[1] : v[0]) : key;

        /// <summary>Перевод с подстановкой: F("ReplayOn", 5) → "Повтор: буфер активен (5 мин)".</summary>
        public static string F(string key, params object[] args) => string.Format(T(key), args);

        // [0] = ru, [1] = en
        static readonly Dictionary<string, string[]> _s = new()
        {
            // ---- шапка / низ ----
            ["Hotkeys"] = new[] { "Alt+Z оверлей · Alt+F9 запись · Alt+F10 повтор · Alt+F1 снимок · Alt+F2 фоторежим · Alt+R FPS",
                                  "Alt+Z overlay · Alt+F9 record · Alt+F10 replay · Alt+F1 screenshot · Alt+F2 photo mode · Alt+R FPS" },

            // ---- статус ----
            ["Ready"] = new[] { "Готов", "Ready" },
            ["Recording"] = new[] { "Идёт запись", "Recording" },
            ["ReplayOff"] = new[] { "Повтор: выкл", "Replay: off" },
            ["ReplayOn"] = new[] { "Повтор: буфер активен ({0} мин)", "Replay: buffer active ({0} min)" },

            // ---- строки меню ----
            ["Gallery"] = new[] { "Галерея", "Gallery" },
            ["GallerySub"] = new[] { "Открыть папку записей", "Open recordings folder" },
            ["Record"] = new[] { "Запись", "Record" },
            ["StopRecord"] = new[] { "Остановить запись", "Stop recording" },
            ["RecordSub"] = new[] { "Alt+F9 — запуск / стоп", "Alt+F9 — start / stop" },
            ["Replay"] = new[] { "Мгновенный повтор", "Instant replay" },
            ["ReplaySubOn"] = new[] { "Alt+F10 — сохранить последние {0} мин", "Alt+F10 — save the last {0} min" },
            ["ReplaySubOff"] = new[] { "Буфер выключен — включи в настройках (⚙)", "Buffer is off — enable it in settings (⚙)" },
            ["Screenshot"] = new[] { "Снимок экрана", "Screenshot" },
            ["ScreenshotSub"] = new[] { "Alt+F1 — сохранить снимок экрана", "Alt+F1 — capture the screen" },
            ["PhotoMode"] = new[] { "Фоторежим", "Photo mode" },
            ["PhotoSub"] = new[] { "Alt+F2 — снимок активного окна", "Alt+F2 — capture the active window" },
            ["FpsCounter"] = new[] { "Счётчик FPS", "FPS counter" },
            ["FpsCounterSub"] = new[] { "Alt+R — включение/выключение", "Alt+R — toggle on/off" },
            ["Mic"] = new[] { "Микрофон", "Microphone" },
            ["MicSub"] = new[] { "Писать микрофон в запись", "Record microphone audio" },
            ["On"] = new[] { "вкл", "on" },
            ["Off"] = new[] { "выкл", "off" },

            // ---- настройки ----
            ["Back"] = new[] { "‹  Назад", "‹  Back" },
            ["InterfaceHeader"] = new[] { "ИНТЕРФЕЙС", "INTERFACE" },
            ["Language"] = new[] { "Язык / Language", "Language / Язык" },
            ["CaptureHeader"] = new[] { "ЗАХВАТ", "CAPTURE" },
            ["WhatToRecord"] = new[] { "Что записывать", "What to record" },
            ["CapScreen"] = new[] { "Весь монитор", "Full monitor" },
            ["CapWindow"] = new[] { "Окно (активное при старте записи)", "Window (active when recording starts)" },
            ["WindowHint"] = new[] { "В режиме окна пишется только выбранное приложение — Alt+Tab в запись не попадает",
                                     "Window mode records only the chosen app — Alt+Tab won't show up in the video" },
            ["Codec"] = new[] { "Кодек", "Codec" },
            ["CodecAv1"] = new[] { "AV1 — топ качество/размер (Arc)", "AV1 — best quality/size (Arc)" },
            ["CodecH264"] = new[] { "H.264 (AVC) — совместимость", "H.264 (AVC) — compatibility" },
            ["Resolution"] = new[] { "Разрешение", "Resolution" },
            ["ResNative"] = new[] { "Нативное (как на мониторе)", "Native (same as monitor)" },
            ["Bitrate"] = new[] { "Битрейт", "Bitrate" },
            ["Mbps"] = new[] { "Мбит/с", "Mbps" },
            ["Monitor"] = new[] { "Монитор", "Monitor" },
            ["Primary"] = new[] { " (главный)", " (primary)" },
            ["AudioHeader"] = new[] { "ЗВУК", "AUDIO" },
            ["SystemAudio"] = new[] { "Системный звук", "System audio" },
            ["ReplayHeader"] = new[] { "МГНОВЕННЫЙ ПОВТОР", "INSTANT REPLAY" },
            ["KeepBuffer"] = new[] { "Держать буфер повтора включённым", "Keep replay buffer running" },
            ["BufferLen"] = new[] { "Длина буфера", "Buffer length" },
            ["Min1"] = new[] { "1 минута", "1 minute" },
            ["Min3"] = new[] { "3 минуты", "3 minutes" },
            ["Min5"] = new[] { "5 минут", "5 minutes" },
            ["Min10"] = new[] { "10 минут", "10 minutes" },
            ["OverlaysHeader"] = new[] { "ОВЕРЛЕИ", "OVERLAYS" },
            ["FpsCheck"] = new[] { "Счётчик FPS (Alt+R)", "FPS counter (Alt+R)" },
            ["FpsPos"] = new[] { "Позиция счётчика", "Counter position" },
            ["TL"] = new[] { "Слева сверху", "Top left" },
            ["TR"] = new[] { "Справа сверху", "Top right" },
            ["BL"] = new[] { "Слева снизу", "Bottom left" },
            ["BR"] = new[] { "Справа снизу", "Bottom right" },
            ["FpsSize"] = new[] { "Размер счётчика", "Counter size" },
            ["SizeS"] = new[] { "Маленький (75%)", "Small (75%)" },
            ["SizeM"] = new[] { "Обычный (100%)", "Normal (100%)" },
            ["SizeL"] = new[] { "Большой (125%)", "Large (125%)" },
            ["SizeXL"] = new[] { "Огромный (150%)", "Huge (150%)" },
            ["AutoTheme"] = new[] { "Автоцвет: тёмный текст на светлом фоне", "Auto color: dark text on light background" },
            ["HideDesktop"] = new[] { "Прятать на рабочем столе (только в играх)", "Hide on desktop (games only)" },
            ["Notifications"] = new[] { "Уведомления о записи, повторе и снимках", "Notifications for recording, replay and screenshots" },

            // ---- всплывающие уведомления ----
            ["ToastTest"] = new[] { "Уведомления включены", "Notifications enabled" },
            ["ToastTestSub"] = new[] { "Так будут выглядеть сообщения о записи и снимках", "This is how recording and screenshot messages will look" },
            ["ToastRecStarted"] = new[] { "Запись запущена", "Recording started" },
            ["ToastRecStartedSub"] = new[] { "Alt+F9 — остановить", "Alt+F9 to stop" },
            ["ToastRecSaved"] = new[] { "Запись сохранена", "Recording saved" },
            ["ToastRecDamaged"] = new[] { "Запись сохранена, но может быть повреждена", "Recording saved but may be corrupted" },
            ["ToastRecInterrupted"] = new[] { "Запись прервалась", "Recording interrupted" },
            ["ToastReplaySaved"] = new[] { "Повтор сохранён", "Replay saved" },
            ["ToastReplaySavedSub"] = new[] { "Последние {0} мин · {1}", "Last {0} min · {1}" },
            ["ToastReplayNoRestart"] = new[] { "Буфер не перезапустился: ", "Buffer failed to restart: " },
            ["ToastReplayEmpty"] = new[] { "Буфер повтора пока пустой", "Replay buffer is still empty" },
            ["ToastReplayEmptySub"] = new[] { "Подожди несколько секунд и попробуй снова", "Wait a few seconds and try again" },
            ["ToastReplayOff"] = new[] { "Мгновенный повтор выключен", "Instant replay is off" },
            ["ToastReplayOffSub"] = new[] { "Включи буфер в настройках: Alt+Z → ⚙", "Enable the buffer in settings: Alt+Z → ⚙" },
            ["ToastReplayPaused"] = new[] { "Повтор на паузе", "Replay paused" },
            ["ToastReplayPausedSub"] = new[] { "Во время записи буфер повтора не пишется", "The replay buffer doesn't run while recording" },
            ["ToastReplayDied"] = new[] { "Буфер повтора остановился", "Replay buffer stopped" },
            ["ToastShotFail"] = new[] { "Не удалось сделать снимок", "Screenshot failed" },

            // ---- трей ----
            ["TrayIdle"] = new[] { "ArcRecorder — Alt+Z оверлей, Alt+F9 запись, Alt+F10 повтор",
                                   "ArcRecorder — Alt+Z overlay, Alt+F9 record, Alt+F10 replay" },
            ["TrayRec"] = new[] { "ArcRecorder — ЗАПИСЬ (Alt+F9 — стоп)", "ArcRecorder — RECORDING (Alt+F9 to stop)" },
            ["TrayOverlay"] = new[] { "Оверлей (Alt+Z)", "Overlay (Alt+Z)" },
            ["TrayRecord"] = new[] { "Старт/стоп записи (Alt+F9)", "Start/stop recording (Alt+F9)" },
            ["TraySaveReplay"] = new[] { "Сохранить повтор (Alt+F10)", "Save replay (Alt+F10)" },
            ["TrayScreenshot"] = new[] { "Снимок экрана (Alt+F1)", "Screenshot (Alt+F1)" },
            ["TrayPhoto"] = new[] { "Фоторежим — снимок окна (Alt+F2)", "Photo mode — window shot (Alt+F2)" },
            ["TrayFps"] = new[] { "Счётчик FPS (Alt+R)", "FPS counter (Alt+R)" },
            ["TrayFolder"] = new[] { "Папка записей", "Recordings folder" },
            ["TrayExit"] = new[] { "Выход", "Exit" },

            // ---- сообщения ----
            ["HotkeyFail"] = new[] { "Не удалось зарегистрировать часть хоткеев (Alt+Z / Alt+F9 / Alt+F10 / Alt+F1 / Alt+F2 / Alt+R).\nВозможно, их заняла другая прога с оверлеем.",
                                     "Failed to register some hotkeys (Alt+Z / Alt+F9 / Alt+F10 / Alt+F1 / Alt+F2 / Alt+R).\nAnother overlay app may have grabbed them." },
            ["ShotSaved"] = new[] { "Снимок экрана сохранён", "Screenshot saved" },
            ["WindowShotSaved"] = new[] { "Снимок окна сохранён", "Window shot saved" },
            ["FpsAdmin"] = new[] { "Счётчик FPS требует запуска от администратора (ETW-сессия).\n\n",
                                   "FPS counter requires running as administrator (ETW session).\n\n" },
            ["ReplayProblem"] = new[] { "Проблема с буфером повтора:\n", "Replay buffer problem:\n" },
            ["ReplaySaveFail"] = new[] { "Не удалось сохранить повтор:\n", "Failed to save replay:\n" },
            ["RecWindow"] = new[] { "Пишем окно: ", "Recording window: " },
            ["RecWindowFallback"] = new[] { "Подходящее окно не нашлось — пишем весь монитор.",
                                            "No suitable window found — recording the full monitor." },
            ["RecStartFail"] = new[] { "Не удалось начать запись:\n", "Failed to start recording:\n" },
            ["ReplayPaused"] = new[] { "Повтор: пауза на время записи", "Replay: paused while recording" },
            ["FfmpegFail"] = new[] { "ffmpeg не стартанул:\n", "ffmpeg failed to start:\n" },
            ["AlreadyRunning"] = new[] { "ArcRecorder уже запущен — ищи иконку в трее.",
                                         "ArcRecorder is already running — look for the tray icon." },
            ["Unexpected"] = new[] { "Непредвиденная ошибка (подробности в %APPDATA%\\ArcRecorder\\error.log):\n",
                                     "Unexpected error (details in %APPDATA%\\ArcRecorder\\error.log):\n" },
        };
    }
}
