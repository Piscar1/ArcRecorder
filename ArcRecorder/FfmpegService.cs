using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace ArcRecorder
{
    public static class FfmpegLocator
    {
        public static string Find()
        {
            // сначала рядом с exe — удобно таскать портативно
            try
            {
                var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
                if (File.Exists(local)) return local;
            }
            catch { }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
            var wingetLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "ffmpeg.exe");
            if (File.Exists(wingetLink)) return wingetLink;
            throw new FileNotFoundException("ffmpeg.exe не найден. Поставь: winget install Gyan.FFmpeg");
        }
    }

    /// <summary>
    /// Один процесс ffmpeg: видео с ddagrab (GPU), звук через именованный канал (PCM от NAudio-микшера),
    /// stdin — только для команды "q" (штатная остановка).
    /// </summary>
    class FfmpegSession
    {
        Process _proc;
        AudioPipeSource _audio;
        volatile bool _videoStarted;
        volatile bool _stopRequested;
        readonly StringBuilder _errBuf = new StringBuilder();
        static int _nextId;
        readonly string _tag;
        readonly Stopwatch _clock = new Stopwatch();
        long _lastFrame = -1;

        public FfmpegSession(string tag)
        {
            _tag = tag + "#" + Interlocked.Increment(ref _nextId);
        }

        /// <summary>Хронометраж старта/остановки в %APPDATA%\ArcRecorder\ffmpeg.log — чтобы разбирать задержки.</summary>
        void Log(string msg) => AppLog.Write("ffmpeg.log", $"[{_tag} +{_clock.ElapsedMilliseconds} мс] {msg}");

        /// <summary>
        /// ffmpeg завершился сам, а не через Stop: упал на старте, потерял окно, кончилось место и т.п.
        /// Аргумент — хвост stderr. Зовётся с пула потоков.
        /// </summary>
        public event Action<FfmpegSession, string> Died;

        public bool IsRunning
        {
            get
            {
                var p = _proc;
                try { return p != null && !p.HasExited; } catch { return false; }
            }
        }

        public void Start(string args, AppSettings s)
        {
            // Звук идёт через именованный канал, stdin свободен под "q". От -shortest отказались:
            // в свежих ffmpeg (9.x) его очередь синхронизации не отдавала кадры до своего 10-секундного лимита —
            // запись обрезалась до долей секунды, а остановка ждала этот лимит.
            // Звук гоним всегда (тишину, если всё выключено) — так у файла всегда есть звуковая дорожка.
            _audio = new AudioPipeSource(s.CaptureSystemAudio, s.CaptureMicrophone);
            args = args.Replace(FfmpegArgs.AudioPipePlaceholder, _audio.PipePath);

            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Find(),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            _clock.Restart();
            try { _proc = Process.Start(psi); }
            catch
            {
                _audio.Stop(); // закрыть канал, раз ffmpeg так и не запустился
                throw;
            }
            ChildProcessJob.Add(_proc); // ffmpeg не переживёт ArcRecorder, даже если того убьют
            Log("старт: " + args);
            _proc.ErrorDataReceived += (o, e) =>
            {
                if (e.Data == null) return;
                if (e.Data.StartsWith("frame=") && long.TryParse(e.Data.AsSpan(6), out long frame))
                    Interlocked.Exchange(ref _lastFrame, frame);
                // -progress пишет key=value в stderr; первый блок = видеозахват реально пошёл.
                // До этого момента звук не гоним — иначе он уезжает вперёд на время инициализации QSV.
                if (!_videoStarted && (e.Data.StartsWith("frame=") || e.Data.StartsWith("progress=")))
                {
                    _videoStarted = true;
                    _audio?.SignalVideoStarted();
                    Log("видео пошло");
                }
                if (e.Data.IndexOf('=') > 0 && e.Data.IndexOf(' ') < 0) return; // прогресс-спам не копим в лог ошибок
                lock (_errBuf) { _errBuf.AppendLine(e.Data); if (_errBuf.Length > 8000) _errBuf.Remove(0, 4000); }
            };
            _proc.BeginErrorReadLine();
            // Приоритет НЕ повышаем: AboveNormal у постоянно висящего буфера повтора
            // вытесняет потоки игры и роняет FPS. Кодирование и так на GPU (QSV),
            // а синхронизация звука держится на сигнале -progress, не на приоритете.
            try { _proc.PriorityClass = ProcessPriorityClass.Normal; } catch { }

            _audio.Start(); // поток звука дождётся, пока ffmpeg откроет канал
            if (_videoStarted) _audio.SignalVideoStarted(); // на случай, если progress пришёл раньше

            // Следим за смертью процесса всё время работы, а не одной проверкой через 800 мс:
            // QSV может упасть на 1.5-й секунде, gdigrab — когда окно закрыли посреди записи.
            // Если процесс уже успел выйти, .NET всё равно поднимет Exited.
            _proc.Exited += OnExited;
            _proc.EnableRaisingEvents = true;
        }

        void OnExited(object sender, EventArgs e)
        {
            // Звук освобождаем в любом случае — иначе микрофон/loopback остаются захваченными
            // (и в трее Windows висит «микрофон используется») до выхода из программы.
            try { _audio?.Stop(); } catch { }
            if (_stopRequested) return;
            try { ((Process)sender).WaitForExit(); } catch { } // дочитать stderr до конца — там причина падения
            if (_stopRequested) return;
            string code = "?";
            try { code = ((Process)sender).ExitCode.ToString(); } catch { }
            Log($"ffmpeg завершился сам, код {code}, последний кадр {Interlocked.Read(ref _lastFrame)}");
            Died?.Invoke(this, ReadErrorTail());
        }

        /// <summary>
        /// Мягкая остановка: команда "q" в stdin, ffmpeg дописывает файл и выходит.
        /// true — ffmpeg закрылся сам (файл целый), false — пришлось убить (mp4 может быть битым).
        /// </summary>
        public bool Stop(int timeoutMs = 10000)
        {
            var proc = _proc;
            if (proc == null) return true;
            _stopRequested = true;
            bool graceful = true;
            Log($"стоп запрошен, последний кадр {Interlocked.Read(ref _lastFrame)}");
            try
            {
                // "q" — штатная остановка: ffmpeg перестаёт читать входы, дописывает файл и выходит
                try { proc.StandardInput.Write('q'); proc.StandardInput.Flush(); } catch { }
                if (!proc.WaitForExit(timeoutMs))
                {
                    graceful = false;
                    Log($"ffmpeg не вышел за {timeoutMs} мс — убиваем, последний кадр {Interlocked.Read(ref _lastFrame)}");
                    proc.Kill();
                }
                else
                {
                    Log($"ffmpeg вышел, код {proc.ExitCode}, последний кадр {Interlocked.Read(ref _lastFrame)}");
                }
            }
            catch
            {
                graceful = false;
                try { proc.Kill(); } catch { }
            }
            finally
            {
                // звук гасим после выхода ffmpeg: канал уже закрыт с его стороны, поток звука не висит на записи
                _audio?.Stop();
                Log("звук остановлен" + _audio?.StopTimings);
                _proc = null;
                try { proc.Dispose(); } catch { }
            }
            return graceful;
        }

        public string ReadErrorTail()
        {
            lock (_errBuf) return _errBuf.ToString();
        }
    }

    /// <summary>Общий построитель аргументов ffmpeg под Intel Arc (QSV).</summary>
    static class FfmpegArgs
    {
        /// <summary>Заглушка пути звукового канала в аргументах; FfmpegSession подставляет реальный \\.\pipe\….</summary>
        public const string AudioPipePlaceholder = "{audio-pipe}";

        /// <summary>
        /// Монитор из настроек; если конфигурация поменялась (монитор отключили) — первый доступный,
        /// чтобы ffmpeg не упирался в несуществующий output_idx.
        /// </summary>
        static MonitorInfo ResolveMonitor(AppSettings s)
        {
            var m = MonitorService.Find(s);
            if (m != null) return m;
            var all = MonitorService.GetMonitors();
            return all.Count > 0 ? all[0] : null;
        }

        static string VideoFilter(AppSettings s, MonitorInfo m)
        {
            int output = m?.OutputIndex ?? s.MonitorIndex;
            string filter = $"ddagrab=output_idx={output}:framerate={s.Framerate},hwmap=derive_device=qsv,format=qsv";
            if (s.ResolutionHeight > 0)
            {
                // Ширину считаем сами и выравниваем до чётной: w=-1 на 21:9 даёт нечётную (2560x1080 → 1707x720),
                // а энкодеры QSV нечётные размеры не любят
                string w = m != null && m.Height > 0
                    ? (2 * (int)Math.Round(m.Width * (double)s.ResolutionHeight / m.Height / 2)).ToString()
                    : "-1";
                filter += $",scale_qsv=w={w}:h={s.ResolutionHeight}";
            }
            return filter;
        }

        public static string Encoder(AppSettings s)
        {
            string codec;
            switch (s.Codec)
            {
                case "AV1": codec = "av1_qsv"; break;
                case "HEVC": codec = "hevc_qsv"; break;
                default: codec = "h264_qsv"; break;
            }
            // veryfast — минимальная нагрузка на энкодер (не лагает в играх), bufsize — стабильный битрейт
            return $"-c:v {codec} -preset veryfast -b:v {s.BitrateMbps}M " +
                   $"-maxrate {s.BitrateMbps * 3 / 2}M -bufsize {s.BitrateMbps * 2}M -g {s.Framerate * 2}";
        }

        public static string Common(AppSettings s) => Common(s, null);

        /// <summary>windowTitle != null — захват конкретного окна через gdigrab (следует за окном при Alt+Tab).</summary>
        public static string Common(AppSettings s, string windowTitle)
        {
            // -progress pipe:2 — сигнал «видео пошло» для синхронизации старта звука
            // -fps_mode cfr — ровный постоянный fps вместо дёрганого VFR
            // aresample=async=1 — подтягивает звук при мелком дрейфе часов
            string head = "-y -hide_banner -loglevel error -stats_period 0.1 -progress pipe:2 ";
            string audioIn = $"-thread_queue_size 4096 -f s16le -ar 48000 -ac 2 -channel_layout stereo -i \"{AudioPipePlaceholder}\" ";
            // без -shortest: остановка — командой "q" (см. FfmpegSession.Stop)
            string tail = $"{Encoder(s)} -fps_mode cfr -r {s.Framerate} " +
                          "-c:a aac -b:a 160k -af aresample=async=1 ";

            if (string.IsNullOrEmpty(windowTitle))
            {
                var m = ResolveMonitor(s);
                int adapter = m?.AdapterIndex ?? s.AdapterIndex;
                // d3d11va=hw:N — тот же адаптер, чей выход захватывает ddagrab (иначе на мульти-GPU будет мусор)
                return head +
                       $"-init_hw_device d3d11va=hw:{adapter} -filter_hw_device hw " +
                       audioIn +
                       $"-filter_complex \"{VideoFilter(s, m)}[v]\" -map \"[v]\" -map 0:a " + tail;
            }

            // Захват окна: gdigrab берёт окно по заголовку (следует за окном при перемещении).
            // Кадры отдаём энкодеру QSV в системной памяти (nv12) — он сам заливает их в GPU;
            // явный hwupload на Arc с gdigrab не заводится (проверено).
            // crop до чётных размеров — энкодеры не любят нечётную ширину/высоту окна.
            string title = windowTitle.Replace("\"", "");
            string scale = s.ResolutionHeight > 0 ? $",scale=-2:{s.ResolutionHeight}" : "";
            return head +
                   audioIn +
                   $"-thread_queue_size 1024 -f gdigrab -framerate {s.Framerate} -draw_mouse 1 -i \"title={title}\" " +
                   "-filter_complex \"[1:v]crop=trunc(iw/2)*2:trunc(ih/2)*2" + scale +
                   ",format=nv12[v]\" -map \"[v]\" -map 0:a " + tail;
        }

        public static string Quote(string p) => "\"" + p + "\"";
    }

    /// <summary>Обычная запись: Alt+F9 старт/стоп → один mp4. Потокобезопасна: App зовёт её из фоновой очереди.</summary>
    public class RecordingService
    {
        readonly object _sync = new object();
        FfmpegSession _session;
        volatile bool _isRecording;

        public bool IsRecording => _isRecording;
        public DateTime StartTime { get; private set; }
        public string CurrentFilePath { get; private set; }

        /// <summary>Заголовок окна, которое пишем (режим "Window"); null = пишем монитор.</summary>
        public string LastWindowTitle { get; private set; }

        /// <summary>ffmpeg записи умер сам. Аргументы: путь файла, хвост stderr, сколько успел проработать.</summary>
        public event Action<string, string, TimeSpan> Failed;

        public string Start(AppSettings s)
        {
            lock (_sync)
            {
                if (_isRecording) return null;
                Directory.CreateDirectory(s.OutputFolder);
                string path = Path.Combine(s.OutputFolder, $"ArcRecorder_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

                // Режим «окно»: берём активное окно в момент старта; не нашли — фолбэк на весь монитор
                string title = s.CaptureMode == "Window" ? WindowCaptureHelper.GetCaptureWindowTitle() : null;

                var session = new FfmpegSession("rec");
                session.Died += OnSessionDied; // сработает не раньше, чем мы отпустим _sync
                session.Start(FfmpegArgs.Common(s, title) + "-movflags +faststart " + FfmpegArgs.Quote(path), s);

                _session = session;
                LastWindowTitle = title;
                StartTime = DateTime.Now;
                CurrentFilePath = path;
                _isRecording = true;
                return path;
            }
        }

        void OnSessionDied(FfmpegSession session, string error)
        {
            string path;
            TimeSpan ran;
            lock (_sync)
            {
                if (session != _session) return; // уже остановлена/заменена
                session.Stop(0);
                _session = null;
                _isRecording = false;
                path = CurrentFilePath;
                ran = DateTime.Now - StartTime;
            }
            Failed?.Invoke(path, error, ran);
        }

        /// <summary>Останавливает запись. graceful=false — ffmpeg пришлось убить, файл может быть битым.</summary>
        public string Stop(out bool graceful)
        {
            lock (_sync)
            {
                graceful = true;
                if (!_isRecording) return null;
                graceful = _session.Stop(15000);
                _session = null;
                _isRecording = false;
                return CurrentFilePath;
            }
        }
    }
}
