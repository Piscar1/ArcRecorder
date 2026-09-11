# ArcRecorder

Lightweight screen recorder for **Intel Arc** GPUs (QSV hardware encoding) with an NVIDIA-App-style overlay.
WPF / .NET 8, ffmpeg under the hood.

Лёгкая программа записи экрана для видеокарт **Intel Arc** (аппаратное кодирование QSV) с оверлеем в стиле NVIDIA App.

## Features / Возможности

- **Recording (Alt+F9)** — AV1 / HEVC / H.264 via Intel QSV, 30/60/120 FPS, up to 4K, 10–100 Mbps
- **Instant Replay (Alt+F10)** — ring buffer of the last 1–10 minutes, saved on demand
- **Screenshots** — full monitor (Alt+F1) and active window / photo mode (Alt+F2)
- **FPS counter (Alt+R)** — ETW-based (PresentMon-style), GPU/CPU load, auto light/dark theme, hides on desktop
- **Window capture mode** — records only the chosen app, Alt+Tab stays out of the video
- **Audio** — system sound + microphone (NAudio → ffmpeg pipe)
- **Overlay (Alt+Z)** — Intel-styled in-app overlay, RU/EN interface language
- Multi-monitor / multi-GPU aware (DXGI order, same as ffmpeg ddagrab)

## Requirements / Требования

- Windows 10/11 x64
- Intel Arc GPU (A- or B-series) with up-to-date drivers
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **ffmpeg** with QSV support in `PATH` (or `ffmpeg.exe` next to `ArcRecorder.exe`):
  ```
  winget install ffmpeg
  ```
- Administrator rights are required for the FPS counter (ETW session)

## Download / Скачать

Grab the latest zip from [Releases](../../releases), unpack anywhere, run `ArcRecorder.exe`.

Скачай zip из [Releases](../../releases), распакуй куда угодно, запусти `ArcRecorder.exe`.

## Build from source / Сборка

```
git clone <this repo>
cd ArcRecorder-GitHub/ArcRecorder
dotnet build -c Release
# exe: bin/x64/Release/net8.0-windows/ArcRecorder.exe
```

## Hotkeys / Хоткеи

| Key | Action |
|---|---|
| `Alt+Z` | Toggle overlay / оверлей |
| `Alt+F9` | Start/stop recording / запись |
| `Alt+F10` | Save instant replay / сохранить повтор |
| `Alt+F1` | Screenshot (monitor) / снимок экрана |
| `Alt+F2` | Photo mode (active window) / фоторежим |
| `Alt+R` | FPS counter / счётчик FPS |

## Where files go / Куда падают файлы

- Recordings & screenshots: `Videos\ArcRecorder\`
- Settings: `%APPDATA%\ArcRecorder\settings.json`

## License

MIT
