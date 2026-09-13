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
        public bool ShowNotifications { get; set; } = true;  // всплывающие уведомления (запись/повтор/снимок)
        public string Language { get; set; } = "ru";          // язык интерфейса: ru | en
        public string OutputFolder { get; set; } = DefaultOutputFolder;

        static string DefaultOutputFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ArcRecorder");

        static string ConfigPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArcRecorder", "settings.json");

        /// <summary>
        /// Всё, что влияет на поток фонового ffmpeg. Буфер повтора перезапускается (и теряет накопленное)
        /// только когда меняется это — а не угол FPS-счётчика или язык.
        /// </summary>
        public string CaptureSignature() => string.Join("|",
            Codec, Framerate, BitrateMbps, ResolutionHeight, MonitorIndex, AdapterIndex,
            CaptureSystemAudio, CaptureMicrophone);

        public static AppSettings Load()
        {
            AppSettings s = null;
            try
            {
                if (File.Exists(ConfigPath))
                    s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath));
            }
            catch { /* битый конфиг — берём дефолт */ }
            s ??= new AppSettings();
            s.Normalize();
            return s;
        }

        /// <summary>Правленый руками/старый конфиг не должен давать -g 0, null-папку и прочий мусор в ffmpeg.</summary>
        void Normalize()
        {
            if (Codec is not ("AV1" or "HEVC" or "H264")) Codec = "AV1";
            if (Framerate is not (30 or 60 or 120)) Framerate = 60;
            BitrateMbps = Math.Clamp(BitrateMbps, 10, 100);
            if (ResolutionHeight is not (0 or 720 or 1080 or 1440 or 2160)) ResolutionHeight = 0;
            if (MonitorIndex < 0) MonitorIndex = 0;
            if (AdapterIndex < 0) AdapterIndex = 0;
            if (ReplayMinutes is not (1 or 3 or 5 or 10)) ReplayMinutes = 5;
            if (CaptureMode is not ("Screen" or "Window")) CaptureMode = "Screen";
            FpsCorner = Math.Clamp(FpsCorner, 0, 3);
            if (FpsScalePercent is not (75 or 100 or 125 or 150)) FpsScalePercent = 100;
            if (Language is not ("ru" or "en")) Language = "ru";
            if (string.IsNullOrWhiteSpace(OutputFolder)) OutputFolder = DefaultOutputFolder;
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
