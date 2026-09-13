using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace ArcRecorder
{
    /// <summary>Загрузка CPU (GetSystemTimes) и GPU (perf-счётчики "GPU Engine") для оверлея статистики.</summary>
    public class HwStatsService : IDisposable
    {
        [DllImport("kernel32.dll")]
        static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        long _prevIdle, _prevKernel, _prevUser;
        DateTime _nextGpuRefresh = DateTime.MinValue;
        bool _disposed;

        // instance name → (счётчик, группа "адаптер|тип движка")
        readonly Dictionary<string, (PerformanceCounter Counter, string Group)> _gpuCounters =
            new Dictionary<string, (PerformanceCounter, string)>();
        static readonly string[] EngineTypes = { "engtype_3D", "engtype_VideoEncode", "engtype_VideoDecode", "engtype_Compute" };

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

        /// <summary>
        /// Загрузка GPU в процентах, -1 если не удалось. Суммируем по каждому адаптеру и типу движка
        /// (3D / VideoEncode / VideoDecode / Compute) и берём максимум — так во время записи цифра не занижается,
        /// а встроенная видеокарта не складывается с дискретной.
        /// </summary>
        public int GetGpuUsage()
        {
            lock (_gpuCounters)
            {
                if (_disposed) return -1;
                try
                {
                    if (DateTime.Now >= _nextGpuRefresh) RefreshGpuCounters();
                    var byGroup = new Dictionary<string, float>();
                    foreach (var (counter, group) in _gpuCounters.Values)
                    {
                        try
                        {
                            byGroup.TryGetValue(group, out float cur);
                            byGroup[group] = cur + counter.NextValue();
                        }
                        catch { }
                    }
                    float max = 0;
                    foreach (var v in byGroup.Values) if (v > max) max = v;
                    return Math.Clamp((int)Math.Round(max), 0, 100);
                }
                catch { return -1; }
            }
        }

        /// <summary>
        /// Синхронизирует счётчики с живыми инстансами: ушедшие процессы выкидываем, новые добавляем,
        /// существующие НЕ пересоздаём — раньше полное пересоздание раз в 10 сек обнуляло показания
        /// и тратило CPU на сотни новых PerformanceCounter прямо во время игры.
        /// </summary>
        void RefreshGpuCounters()
        {
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames();
            var alive = new HashSet<string>(names);
            foreach (var gone in _gpuCounters.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                try { _gpuCounters[gone].Counter.Dispose(); } catch { }
                _gpuCounters.Remove(gone);
            }
            foreach (var inst in names)
            {
                if (_gpuCounters.ContainsKey(inst)) continue;
                string type = EngineTypes.FirstOrDefault(t => inst.EndsWith(t, StringComparison.OrdinalIgnoreCase));
                if (type == null) continue;
                try
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst);
                    c.NextValue(); // первый вызов всегда 0 — праймим
                    _gpuCounters[inst] = (c, AdapterOf(inst) + "|" + type);
                }
                catch { }
            }
            _nextGpuRefresh = DateTime.Now.AddSeconds(10); // инстансы приходят/уходят вместе с процессами
        }

        /// <summary>"pid_1_luid_0x00000000_0x0000D1B2_phys_0_eng_3_engtype_3D" → "luid_0x00000000_0x0000D1B2_phys_0".</summary>
        static string AdapterOf(string instance)
        {
            int a = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
            int b = instance.IndexOf("_eng_", StringComparison.OrdinalIgnoreCase);
            return a >= 0 && b > a ? instance.Substring(a, b - a) : "";
        }

        public void Dispose()
        {
            lock (_gpuCounters)
            {
                _disposed = true;
                foreach (var (c, _) in _gpuCounters.Values) { try { c.Dispose(); } catch { } }
                _gpuCounters.Clear();
            }
        }
    }
}
