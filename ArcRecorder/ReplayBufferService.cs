using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ArcRecorder
{
    /// <summary>
    /// Instant Replay как у нвидии: фоновый ffmpeg пишет кольцевой буфер сегментами по 15 сек.
    /// Alt+F10 → буфер мягко останавливается, последние N минут склеиваются (-c copy, мгновенно), буфер рестартует.
    /// </summary>
    public class ReplayBufferService
    {
        const int SegmentSeconds = 15;
        FfmpegSession _session;
        readonly string _bufferDir;
        AppSettings _settings;
        string _sessionPrefix = "seg";
        readonly System.Timers.Timer _cleaner = new System.Timers.Timer(10000);

        public bool IsRunning { get; private set; }

        public ReplayBufferService()
        {
            _bufferDir = Path.Combine(Path.GetTempPath(), "ArcRecorder", "replay");
            _cleaner.Elapsed += (o, e) => CleanOldSegments();
        }

        public void Start(AppSettings s) => Start(s, wipe: true);

        void Start(AppSettings s, bool wipe)
        {
            if (IsRunning) return;
            _settings = s;
            Directory.CreateDirectory(_bufferDir);
            if (wipe)
                foreach (var f in Directory.GetFiles(_bufferDir)) { try { File.Delete(f); } catch { } }

            // уникальный префикс на сессию: при рестарте после сохранения старые сегменты не перезаписываются
            _sessionPrefix = $"seg_{DateTime.Now:yyyyMMdd_HHmmss}";
            string pattern = Path.Combine(_bufferDir, _sessionPrefix + "_%05d.mp4");
            string args = FfmpegArgs.Common(s) +
                          $"-f segment -segment_time {SegmentSeconds} -reset_timestamps 1 -segment_format mp4 " +
                          FfmpegArgs.Quote(pattern);
            _session = new FfmpegSession();
            _session.Start(args, s);
            IsRunning = true;
            _cleaner.Start();
        }

        /// <summary>Проверка, что фоновый ffmpeg жив (зовётся асинхронно, чтобы не морозить UI).</summary>
        public bool VerifyRunning(out string error)
        {
            error = null;
            if (_session != null && _session.IsRunning) return true;
            error = _session?.ReadErrorTail() ?? "ffmpeg не стартанул";
            _cleaner.Stop();
            _session = null;
            IsRunning = false;
            return false;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            _cleaner.Stop();
            _session.Stop();
            IsRunning = false;
        }

        /// <summary>
        /// Сохраняет последние N минут. Буфер рестартует СРАЗУ после мягкого стопа
        /// (склейка идёт уже на фоне работающего буфера — дырка минимальная).
        /// </summary>
        public string SaveReplay()
        {
            if (!IsRunning) return null;
            _cleaner.Stop();
            _session.Stop(); // мягко: все сегменты закрываются корректно
            _session = null;
            IsRunning = false;

            // снимок сегментов этой сессии ДО рестарта
            var all = Directory.GetFiles(_bufferDir, "seg_*.mp4").OrderBy(f => f).ToList();

            // рестарт буфера немедленно — новый префикс, старые файлы не трогаются
            try { Start(_settings, wipe: false); } catch { }

            string result = null;
            string listFile = Path.Combine(_bufferDir, "concat.txt");
            try
            {
                var segs = all.Where(f => new FileInfo(f).Length > 0).ToList();
                int keep = Math.Max(1, (_settings.ReplayMinutes * 60) / SegmentSeconds);
                if (segs.Count > keep) segs = segs.Skip(segs.Count - keep).ToList();
                if (segs.Count > 0)
                {
                    Directory.CreateDirectory(_settings.OutputFolder);
                    result = Path.Combine(_settings.OutputFolder, $"Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
                    File.WriteAllText(listFile, string.Join("\n", segs.Select(f => $"file '{f.Replace("\\", "/")}'")));

                    var psi = new ProcessStartInfo
                    {
                        FileName = FfmpegLocator.Find(),
                        Arguments = "-y -hide_banner -loglevel error -f concat -safe 0 -i " +
                                    FfmpegArgs.Quote(listFile) + " -c copy -movflags +faststart " + FfmpegArgs.Quote(result),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi)) p.WaitForExit(30000);
                    if (!File.Exists(result) || new FileInfo(result).Length == 0) result = null;
                }
            }
            finally
            {
                // подчищаем использованные сегменты старой сессии
                foreach (var f in all) { try { File.Delete(f); } catch { } }
                try { File.Delete(listFile); } catch { }
            }
            return result;
        }

        void CleanOldSegments()
        {
            try
            {
                int keep = (_settings.ReplayMinutes * 60) / SegmentSeconds + 2;
                var segs = Directory.GetFiles(_bufferDir, _sessionPrefix + "_*.mp4").OrderBy(f => f).ToList();
                for (int i = 0; i < segs.Count - keep; i++)
                    try { File.Delete(segs[i]); } catch { }
            }
            catch { }
        }
    }
}
