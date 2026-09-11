using System;
using System.IO;
using System.Text.Json;

namespace ArcRecorder
{
    public class AppSettings
    {
        public string Codec { get; set; } = "AV1";           // AV1 | HEVC | H264
        public int Framerate { get; set; } = 60;             // 30 | 60 | 120
        public int BitrateMbps { get; set; } = 35;           // 10..100
        public int ResolutionHeight { get; set; } = 0;       // 0 = нативное, иначе 720/1080/1440/2160
        public int MonitorIndex { get; set; } = 0;           // индекс выхода GPU (ddagrab output_idx, порядок DXGI)
        public int AdapterIndex { get; set; } = 0;           // индекс GPU (d3d11va=hw:N), если мониторов несколько на разных картах
        public bool CaptureSystemAudio { get; set; } = true;
        public bool CaptureMicrophone { get; set; } = false;
        public bool ReplayEnabled { get; set; } = true;      // instant replay буфер при старте
        public int ReplayMinutes { get; set; } = 5;          // 1 | 3 | 5 | 10
        public bool FpsOverlayEnabled { get; set; } = false; // FPS-счётчик (Alt+R)
        public string CaptureMode { get; set; } = "Screen";  // Screen | Window (окно, активное при старте записи)
        public int FpsCorner { get; set; } = 1;              // позиция счётчика: 0 TL, 1 TR, 2 BL, 3 BR
        public int FpsScalePercent { get; set; } = 100;      // размер счётчика: 75 | 100 | 125 | 150
        public bool FpsAutoTheme { get; set; } = true;       // автоцвет: тёмный текст на светлом фоне
        public bool FpsHideOnDesktop { get; set; } = true;   // прятать счётчик на рабочем столе
        public string Language { get; set; } = "ru";          // язык интерфейса: ru | en
        public string OutputFolder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ArcRecorder");

        static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArcRecorder", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new AppSettings();
            }
            catch { /* битый конфиг — берём дефолт */ }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }
}
