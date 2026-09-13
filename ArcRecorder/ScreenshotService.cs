using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ArcRecorder
{
    /// <summary>
    /// Скриншоты: весь монитор (Alt+F1) или активное окно (Alt+F2, "фоторежим").
    /// Основной захват — ffmpeg ddagrab (DXGI Desktop Duplication): работает в фуллскрин
    /// DX12-играх (DOOM и т.п.), где GDI CopyFromScreen молча падает. GDI — фолбэк.
    /// </summary>
    public static class ScreenshotService
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>Диагностика скриншотов: %APPDATA%\ArcRecorder\screenshot.log (с ротацией).</summary>
        public static void Log(string msg) => AppLog.Write("screenshot.log", msg);

        public static string ScreenshotFolder(AppSettings s) => Path.Combine(s.OutputFolder, "Screenshots");

        /// <summary>Alt+F1: снимок выбранного в настройках монитора (координаты из DXGI, как у записи).</summary>
        public static string CaptureMonitor(AppSettings s)
        {
            var m = MonitorService.Find(s) ?? MonitorService.GetMonitors()[0];
            string path = NewPath(s);
            Log($"CaptureMonitor: adapter={m.AdapterIndex} output={m.OutputIndex} rect={m.Left},{m.Top} {m.Width}x{m.Height} -> {path}");
            if (CaptureDda(m.AdapterIndex, m.OutputIndex, 0, 0, 0, 0, path)) return path;
            Log("CaptureMonitor: ddagrab не смог, падаем в GDI");
            return CaptureGdi(new Rectangle(m.Left, m.Top, m.Width, m.Height), path);
        }

        /// <summary>Alt+F2 ("фоторежим"): снимок активного окна (границы через DWM, без тени).</summary>
        public static string CaptureActiveWindow(AppSettings s)
        {
            var hwnd = GetForegroundWindow();
            Log($"CaptureActiveWindow: hwnd=0x{hwnd:X}");
            if (hwnd == IntPtr.Zero) return CaptureMonitor(s);

            RECT r;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) != 0)
                GetWindowRect(hwnd, out r);

            var b = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            if (b.Width <= 0 || b.Height <= 0) return CaptureMonitor(s);

            string path = NewPath(s);

            // Ищем DXGI-монитор, на котором лежит центр окна, и режем ddagrab по окну
            var monitors = MonitorService.GetMonitors();
            var cx = b.Left + b.Width / 2; var cy = b.Top + b.Height / 2;
            foreach (var m in monitors)
            {
                if (cx < m.Left || cx >= m.Left + m.Width || cy < m.Top || cy >= m.Top + m.Height) continue;
                // Обрезаем окно рамками монитора (дупликация не умеет за его пределы)
                var mon = new Rectangle(m.Left, m.Top, m.Width, m.Height);
                var clip = Rectangle.Intersect(b, mon);
                if (clip.Width <= 0 || clip.Height <= 0) break;
                if (CaptureDda(m.AdapterIndex, m.OutputIndex,
                               clip.Left - m.Left, clip.Top - m.Top, clip.Width, clip.Height, path))
                    return path;
                break;
            }
            Log($"CaptureActiveWindow: ddagrab не смог (окно {b}), падаем в GDI");
            return CaptureGdi(b, path);
        }

        static string NewPath(AppSettings s)
        {
            string dir = ScreenshotFolder(s);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.png");
        }

        /// <summary>
        /// Снимок через ffmpeg ddagrab (DXGI Desktop Duplication) — один кадр в PNG.
        /// Работает в эксклюзивном фуллскрине (DX11/DX12), где GDI отдаёт пустоту.
        /// w/h = 0 — весь выход монитора.
        /// </summary>
        static bool CaptureDda(int adapter, int output, int x, int y, int w, int h, string path)
        {
            string ffmpeg;
            try { ffmpeg = FfmpegLocator.Find(); }
            catch (Exception ex) { Log("CaptureDda: ffmpeg не найден: " + ex.Message); return false; }

            string crop = w > 0 && h > 0 ? $":offset_x={x}:offset_y={y}:video_size={w}x{h}" : "";
            string args =
                "-y -hide_banner -loglevel error " +
                $"-init_hw_device d3d11va=hw:{adapter} -filter_complex " +
                $"\"ddagrab=output_idx={output}:draw_mouse=0:framerate=30{crop},hwdownload,format=bgra\" " +
                $"-frames:v 1 \"{path}\"";
            Log("CaptureDda: ffmpeg " + args);
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                // stderr читаем параллельно: синхронный ReadToEnd ждал конца процесса,
                // и при зависшем ffmpeg до таймаута дело не доходило никогда
                var stderrTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(10000))
                {
                    try { p.Kill(); } catch { }
                    Log("CaptureDda: таймаут 10с, ffmpeg убит");
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    return false;
                }
                string stderr = stderrTask.Wait(2000) ? stderrTask.Result : "";
                if (p.ExitCode != 0)
                {
                    Log($"CaptureDda: exit={p.ExitCode}. stderr: {stderr.Trim()}");
                    return false;
                }
                var fi = new FileInfo(path);
                bool ok = fi.Exists && fi.Length > 0;
                Log(ok ? $"CaptureDda: OK, {fi.Length} байт"
                       : $"CaptureDda: exit=0, но файла нет/пустой. stderr: {stderr.Trim()}");
                return ok;
            }
            catch (Exception ex)
            {
                Log("CaptureDda: исключение: " + ex);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                return false;
            }
        }

        /// <summary>Фолбэк: старый GDI-захват (рабочий стол, окна без эксклюзивного фуллскрина).</summary>
        static string CaptureGdi(Rectangle b, string path)
        {
            try
            {
                using (var bmp = new Bitmap(b.Width, b.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(b.Location, System.Drawing.Point.Empty, b.Size);
                    bmp.Save(path, ImageFormat.Png);
                }
                Log($"CaptureGdi: OK, {new FileInfo(path).Length} байт ({b})");
                return path;
            }
            catch (Exception ex)
            {
                Log("CaptureGdi: исключение: " + ex.Message);
                throw; // наверх — App покажет ошибку/звук
            }
        }
    }
}
