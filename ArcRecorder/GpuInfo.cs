using System;
using System.Collections.Generic;
using System.Management;
using System.Text.RegularExpressions;

namespace ArcRecorder
{
    /// <summary>Определение реальной видеокарты через WMI (Win32_VideoController).</summary>
    public static class GpuInfo
    {
        static string _cached;

        /// <summary>
        /// Короткое имя GPU для шапки, например "Arc B580", "Arc A770", "GeForce RTX 4070".
        /// Приоритет: дискретный Intel Arc → любой Intel → первый попавшийся адаптер.
        /// Результат кэшируется (WMI-запрос небыстрый).
        /// </summary>
        public static string GetGpuShortName()
        {
            if (_cached != null) return _cached;

            var names = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController WHERE Availability != 8");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var n = mo["Name"] as string;
                    if (!string.IsNullOrWhiteSpace(n)) names.Add(n.Trim());
                }
            }
            catch { /* WMI недоступен — покажем запасной вариант */ }

            // приоритет: Arc → Intel → остальное
            string pick = names.Find(n => n.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? names.Find(n => n.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0)
                       ?? (names.Count > 0 ? names[0] : null);

            _cached = pick != null ? Shorten(pick) : "Intel GPU";
            return _cached;
        }

        /// <summary>"Intel(R) Arc(TM) B580 Graphics" → "Arc B580"; чужие карты просто чистим от мусора.</summary>
        static string Shorten(string full)
        {
            // Intel Arc: вытаскиваем модель (A310..A770, B570/B580, будущие C…)
            var arc = Regex.Match(full, @"Arc\(?TM\)?\s*([A-Z]\d{3})", RegexOptions.IgnoreCase);
            if (arc.Success) return "Arc " + arc.Groups[1].Value.ToUpperInvariant();

            // Intel iGPU: "Intel(R) UHD Graphics 770" → "Intel UHD 770" и т.п.
            string s = full
                .Replace("(R)", "").Replace("(TM)", "").Replace("(r)", "").Replace("(tm)", "")
                .Replace("NVIDIA ", "").Replace("AMD ", "");
            s = Regex.Replace(s, @"\s*Graphics\s*", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            return s.Length > 0 ? s : full;
        }
    }
}
