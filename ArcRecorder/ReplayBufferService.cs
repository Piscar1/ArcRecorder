using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace ArcRecorder
{
    /// <summary>Сегменты, унесённые из буфера на склейку (см. <see cref="ReplayBufferService.DetachSegments"/>).</summary>
    public class ReplaySaveJob
    {
        public string Dir;
        public List<string> Segments;
        public int Minutes;
        public string OutputFolder;
        /// <summary>Буфер не смог перезапуститься после сохранения (null — всё ок).</summary>
        public Exception RestartError;
    }

    /// <summary>
    /// Instant Replay как у нвидии: фоновый ffmpeg пишет кольцевой буфер сегментами по 15 сек.
    /// Alt+F10 → буфер мягко останавливается, сегменты уносятся в отдельную папку, буфер сразу рестартует,
    /// а склейка (-c copy) идёт параллельно. Потокобезопасен: App зовёт его из фоновой очереди.
    /// </summary>
    public class ReplayBufferService
    {
        const int SegmentSeconds = 15;
        readonly object _sync = new object();
        FfmpegSession _session;
        readonly string _bufferDir;
        AppSettings _settings;
        string _sessionPrefix = "seg";
        DateTime _startedAt;
        readonly System.Timers.Timer _cleaner = new System.Timers.Timer(10000);
        volatile bool _isRunning;

        public bool IsRunning => _isRunning;

        /// <summary>Параметры захвата, с которыми запущен буфер — чтобы не рестартовать его зря.</summary>
        public string RunningSignature { get; private set; }

        /// <summary>Фоновый ffmpeg умер сам. Аргументы: хвост stderr, сколько успел проработать.</summary>
        public event Action<string, TimeSpan> Failed;

        public ReplayBufferService()
        {
            _bufferDir = Path.Combine(Path.GetTempPath(), "ArcRecorder", "replay");
            _cleaner.Elapsed += (o, e) => CleanOldSegments();
            // хвосты сохранений, прерванных падением/выключением прошлого запуска
            try
            {
                if (Directory.Exists(_bufferDir))
                    foreach (var d in Directory.GetDirectories(_bufferDir, "save_*"))
                        try { Directory.Delete(d, true); } catch { }
            }
            catch { }
        }

        public void Start(AppSettings s)
        {
            lock (_sync) StartLocked(s, wipe: true);
        }

        void StartLocked(AppSettings s, bool wipe)
        {
            if (_isRunning) return;
            _settings = s;
            Directory.CreateDirectory(_bufferDir);
            // только файлы верхнего уровня: папки save_* — это идущие прямо сейчас склейки
            if (wipe)
                foreach (var f in Directory.GetFiles(_bufferDir)) { try { File.Delete(f); } catch { } }

            // уникальный префикс на сессию: при рестарте после сохранения старые сегменты не перезаписываются
            _sessionPrefix = $"seg_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
            string pattern = Path.Combine(_bufferDir, _sessionPrefix + "_%05d.mp4");
            string args = FfmpegArgs.Common(s) +
                          $"-f segment -segment_time {SegmentSeconds} -reset_timestamps 1 -segment_format mp4 " +
                          FfmpegArgs.Quote(pattern);
            var session = new FfmpegSession("replay");
            session.Died += OnSessionDied;
            session.Start(args, s);
            _session = session;
            _startedAt = DateTime.Now;
            RunningSignature = s.CaptureSignature();
            _isRunning = true;
            _cleaner.Start();
        }

        void OnSessionDied(FfmpegSession session, string error)
        {
            TimeSpan ran;
            lock (_sync)
            {
                if (session != _session) return;
                _cleaner.Stop();
                session.Stop(0);
                _session = null;
                _isRunning = false;
                ran = DateTime.Now - _startedAt;
            }
            Failed?.Invoke(error, ran);
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (!_isRunning) return;
                _cleaner.Stop();
                _session.Stop();
                _session = null;
                _isRunning = false;
            }
        }

        /// <summary>
        /// Шаг 1 сохранения повтора: мягко стопает буфер (сегменты закрываются корректно), уносит их
        /// в отдельную папку и сразу рестартует буфер. null — буфер не работал.
        /// Склейку делает <see cref="Concat"/> — отдельно, чтобы не держать очередь операций.
        /// </summary>
        public ReplaySaveJob DetachSegments()
        {
            lock (_sync)
            {
                if (!_isRunning) return null;
                _cleaner.Stop();
                _session.Stop();
                _session = null;
                _isRunning = false;

                // Переносим в свою папку: иначе рестарт буфера с wipe (смена настроек) снёс бы файлы посреди склейки
                string jobDir = Path.Combine(_bufferDir, $"save_{DateTime.Now:yyyyMMdd_HHmmss_fff}");
                Directory.CreateDirectory(jobDir);
                var moved = new List<string>();
                foreach (var f in Directory.GetFiles(_bufferDir, "seg_*.mp4").OrderBy(f => f))
                {
                    try
                    {
                        string dst = Path.Combine(jobDir, Path.GetFileName(f));
                        File.Move(f, dst);
                        moved.Add(dst);
                    }
                    catch { }
                }

                var job = new ReplaySaveJob
                {
                    Dir = jobDir,
                    Segments = moved,
                    Minutes = _settings.ReplayMinutes,
                    OutputFolder = _settings.OutputFolder
                };
                // рестарт буфера немедленно — дырка в буфере минимальная
                try { StartLocked(_settings, wipe: false); }
                catch (Exception ex) { job.RestartError = ex; }
                return job;
            }
        }

        /// <summary>
        /// Шаг 2: склеивает последние N минут в mp4 (-c copy, быстро). null — сегментов нет (буфер пустой).
        /// Ошибка ffmpeg — исключение. Папка сегментов удаляется в любом случае.
        /// </summary>
        public static string Concat(ReplaySaveJob job)
        {
            string listFile = Path.Combine(job.Dir, "concat.txt");
            try
            {
                var segs = job.Segments.Where(f => File.Exists(f) && new FileInfo(f).Length > 0).ToList();
                int keep = Math.Max(1, (job.Minutes * 60) / SegmentSeconds);
                if (segs.Count > keep) segs = segs.Skip(segs.Count - keep).ToList();
                if (segs.Count == 0) return null;

                Directory.CreateDirectory(job.OutputFolder);
                string result = Path.Combine(job.OutputFolder, $"Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
                File.WriteAllText(listFile, string.Join("\n", segs.Select(f => $"file '{f.Replace("\\", "/")}'")));

                var psi = new ProcessStartInfo
                {
                    FileName = FfmpegLocator.Find(),
                    Arguments = "-y -hide_banner -loglevel error -f concat -safe 0 -i " +
                                FfmpegArgs.Quote(listFile) + " -c copy -movflags +faststart " + FfmpegArgs.Quote(result),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(120000))
                    {
                        // не бросаем висящий ffmpeg: иначе он читает сегменты, которые мы сейчас удалим,
                        // а наружу ушёл бы недописанный файл
                        try { p.Kill(); p.WaitForExit(5000); } catch { }
                        TryDelete(result);
                        throw new TimeoutException("ffmpeg склеивал повтор дольше 2 минут и был остановлен");
                    }
                    if (p.ExitCode != 0 || !File.Exists(result) || new FileInfo(result).Length == 0)
                    {
                        TryDelete(result);
                        throw new IOException($"ffmpeg не смог склеить повтор (код {p.ExitCode})");
                    }
                }
                return result;
            }
            finally
            {
                try { Directory.Delete(job.Dir, true); } catch { }
            }
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        void CleanOldSegments()
        {
            // таймер может сработать посреди Stop/DetachSegments — тогда просто пропускаем тик
            if (!Monitor.TryEnter(_sync)) return;
            try
            {
                if (!_isRunning) return;
                int keep = (_settings.ReplayMinutes * 60) / SegmentSeconds + 2;
                var segs = Directory.GetFiles(_bufferDir, _sessionPrefix + "_*.mp4").OrderBy(f => f).ToList();
                for (int i = 0; i < segs.Count - keep; i++)
                    try { File.Delete(segs[i]); } catch { }
            }
            catch { }
            finally { Monitor.Exit(_sync); }
        }
    }
}
