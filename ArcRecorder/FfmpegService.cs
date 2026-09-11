using System;
using System.Diagnostics;
using System.IO;
using System.Text;

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

    /// <summary>Один процесс ffmpeg: видео с ddagrab (GPU), звук через stdin (PCM от NAudio-микшера).</summary>
    class FfmpegSession
    {
        Process _proc;
        AudioPipeSource _audio;
        volatile bool _videoStarted;
        readonly StringBuilder _errBuf = new StringBuilder();

        public bool IsRunning => _proc != null && !_proc.HasExited;

        public void Start(string args, AppSettings s)
        {
            var psi = new ProcessStartInfo
            {
                FileName = FfmpegLocator.Find(),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true
            };
            _proc = Process.Start(psi);
            _proc.ErrorDataReceived += (o, e) =>
            {
                if (e.Data == null) return;
                // -progress пишет key=value в stderr; первый блок = видеозахват реально пошёл.
                // До этого момента звук не гоним — иначе он уезжает вперёд на время инициализации QSV.
                if (!_videoStarted && (e.Data.StartsWith("frame=") || e.Data.StartsWith("progress=")))
                {
                    _videoStarted = true;
                    _audio?.SignalVideoStarted();
                }
                if (e.Data.IndexOf('=') > 0 && e.Data.IndexOf(' ') < 0) return; // прогресс-спам не копим в лог ошибок
                lock (_errBuf) { _errBuf.AppendLine(e.Data); if (_errBuf.Length > 8000) _errBuf.Remove(0, 4000); }
            };
            _proc.BeginErrorReadLine();
            // Приоритет НЕ повышаем: AboveNormal у постоянно висящего буфера повтора
            // вытесняет потоки игры и роняет FPS. Кодирование и так на GPU (QSV),
            // а синхронизация звука держится на сигнале -progress, не на приоритете.
            try { _proc.PriorityClass = ProcessPriorityClass.Normal; } catch { }

            // всегда гоним аудиопоток (тишину, если всё выключено) — закрытие stdin = мягкий стоп через -shortest
            _audio = new AudioPipeSource(s.CaptureSystemAudio, s.CaptureMicrophone);
            _audio.Start(_proc.StandardInput.BaseStream);
            if (_videoStarted) _audio.SignalVideoStarted(); // на случай, если progress пришёл раньше
        }

        /// <summary>Мягкая остановка: закрываем аудио-stdin, ffmpeg дописывает файл (-shortest) и выходит.</summary>
        public void Stop(int timeoutMs = 10000)
        {
            if (_proc == null) return;
            try
            {
                _audio?.Stop(); // закрывает stdin
                if (!_proc.WaitForExit(timeoutMs)) _proc.Kill();
            }
            catch { try { _proc.Kill(); } catch { } }
            finally { _proc = null; _audio = null; }
        }

        public string ReadErrorTail()
        {
            lock (_errBuf) return _errBuf.ToString();
        }
    }

    /// <summary>Общий построитель аргументов ffmpeg под Intel Arc (QSV).</summary>
    static class FfmpegArgs
    {
        public static string VideoFilter(AppSettings s)
        {
            string filter = $"ddagrab=output_idx={s.MonitorIndex}:framerate={s.Framerate},hwmap=derive_device=qsv,format=qsv";
            if (s.ResolutionHeight > 0)
                filter += $",scale_qsv=w=-1:h={s.ResolutionHeight}";
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
            string audioIn = "-thread_queue_size 4096 -f s16le -ar 48000 -ac 2 -channel_layout stereo -i pipe:0 ";
            string tail = $"{Encoder(s)} -fps_mode cfr -r {s.Framerate} " +
                          "-c:a aac -b:a 160k -af aresample=async=1 -shortest ";

            if (string.IsNullOrEmpty(windowTitle))
            {
                // d3d11va=hw:N — тот же адаптер, чей выход захватывает ddagrab (иначе на мульти-GPU будет мусор)
                return head +
                       $"-init_hw_device d3d11va=hw:{s.AdapterIndex} -filter_hw_device hw " +
                       audioIn +
                       $"-filter_complex \"{VideoFilter(s)}[v]\" -map \"[v]\" -map 0:a " + tail;
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

    /// <summary>Обычная запись: Alt+F9 старт/стоп → один mp4.</summary>
    public class RecordingService
    {
        FfmpegSession _session;
        public bool IsRecording { get; private set; }
        public DateTime StartTime { get; private set; }
        public string CurrentFilePath { get; private set; }

        /// <summary>Заголовок окна, которое пишем (режим "Window"); null = пишем монитор.</summary>
        public string LastWindowTitle { get; private set; }

        public string Start(AppSettings s)
        {
            if (IsRecording) return null;
            Directory.CreateDirectory(s.OutputFolder);
            string path = Path.Combine(s.OutputFolder, $"ArcRecorder_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");

            // Режим «окно»: берём активное окно в момент старта; не нашли — фолбэк на весь монитор
            LastWindowTitle = s.CaptureMode == "Window" ? WindowCaptureHelper.GetCaptureWindowTitle() : null;

            _session = new FfmpegSession();
            _session.Start(FfmpegArgs.Common(s, LastWindowTitle) + "-movflags +faststart " + FfmpegArgs.Quote(path), s);

            IsRecording = true;
            StartTime = DateTime.Now;
            CurrentFilePath = path;
            return path;
        }

        /// <summary>Проверка, что ffmpeg жив (зовётся асинхронно после старта, чтобы не морозить UI).</summary>
        public bool VerifyRunning(out string error)
        {
            error = null;
            if (_session != null && _session.IsRunning) return true;
            error = _session?.ReadErrorTail() ?? "ffmpeg не стартанул";
            _session = null;
            IsRecording = false;
            return false;
        }

        public string Stop()
        {
            if (!IsRecording) return null;
            _session.Stop();
            _session = null;
            IsRecording = false;
            return CurrentFilePath;
        }
    }
}
