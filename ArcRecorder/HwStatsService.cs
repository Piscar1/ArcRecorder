using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArcRecorder
{
    /// <summary>Загрузка CPU (GetSystemTimes) и GPU (perf-счётчики "GPU Engine", engtype_3D) для оверлея статистики.</summary>
    public class HwStatsService : IDisposable
    {
        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        long _prevIdle, _prevKernel, _prevUser;
        DateTime _nextGpuRefresh = DateTime.MinValue;

        public HwStatsService()
        {
            GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser); // праймим CPU-замер
        }

        /// <summary>Общая загрузка CPU в процентах (0..100), -1 если не удалось.</summary>
        public int GetCpuUsage()
        {
            if (!GetSystemTimes(out long idle, out long kernel, out long user)) return -1;
            long dIdle = idle - _prevIdle, dKernel = kernel - _prevKernel, dUser = user - _prevUser;
            _prevIdle = idle; _prevKernel = kernel; _prevUser = user;
            long total = dKernel + dUser; // kernel уже включает idle
            if (total <= 0) return -1;
            return Math.Clamp((int)Math.Round(100.0 * (total - dIdle) / total), 0, 100);
        }

        /// <summary>Загрузка GPU в процентах, -1 если не удалось. Берём максимум по типам движков
        /// (3D / VideoEncode / VideoDecode / Compute) — так во время записи цифра не занижается.</summary>
        public int GetGpuUsage()
        {
            try
            {
                if (DateTime.Now >= _nextGpuRefresh) RefreshGpuCounters();
                // суммируем по каждому типу движка отдельно, показываем самый нагруженный
                var byType = new Dictionary<string, float>();
                foreach (var (counter, engType) in _gpuCounters2)
                {
                    try
                    {
                        byType.TryGetValue(engType, out float cur);
                        byType[engType] = cur + counter.NextValue();
                    }
                    catch { }
                }
                float max = 0;
                foreach (var v in byType.Values) if (v > max) max = v;
                return Math.Clamp((int)Math.Round(max), 0, 100);
            }
            catch { return -1; }
        }

        readonly List<(PerformanceCounter, string)> _gpuCounters2 = new List<(PerformanceCounter, string)>();
        static readonly string[] EngineTypes = { "engtype_3D", "engtype_VideoEncode", "engtype_VideoDecode", "engtype_Compute" };

        void RefreshGpuCounters()
        {
            foreach (var (c, _) in _gpuCounters2) { try { c.Dispose(); } catch { } }
            _gpuCounters2.Clear();
            var cat = new PerformanceCounterCategory("GPU Engine");
            foreach (var inst in cat.GetInstanceNames())
            {
                foreach (var t in EngineTypes)
                {
                    if (inst.EndsWith(t, StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                            c.NextValue(); // первый вызов всегда 0 — праймим
                            _gpuCounters2.Add((c, t));
                        }
                        catch { }
                        break;
                    }
                }
            }
            _nextGpuRefresh = DateTime.Now.AddSeconds(10); // инстансы приходят/уходят вместе с процессами
        }

        public void Dispose()
        {
            foreach (var (c, _) in _gpuCounters2) { try { c.Dispose(); } catch { } }
            _gpuCounters2.Clear();
        }
    }
}
