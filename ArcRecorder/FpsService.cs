using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace ArcRecorder
{
    /// <summary>
    /// FPS-счётчик по принципу PresentMon: ETW-сессия слушает Present-события
    /// DXGI/D3D9 и считает кадры активного (foreground) процесса.
    /// Требует прав администратора (ограничение ETW real-time сессий).
    /// </summary>
    public class FpsService : IDisposable
    {
        // Microsoft-Windows-DXGI (Present_Start = 42), Microsoft-Windows-D3D9 (Present_Start = 1)
        static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        static readonly Guid D3D9Provider = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        const int DxgiPresentStart = 42;
        const int D3D9PresentStart = 1;
        const string SessionName = "ArcRecorderFpsSession";

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        TraceEventSession _session;
        Thread _thread;
        int _targetPid;
        int _count;

        public bool IsRunning { get; private set; }

        /// <summary>Стартует ETW-сессию. Кидает UnauthorizedAccessException без прав админа.</summary>
        public void Start()
        {
            if (IsRunning) return;
            var session = new TraceEventSession(SessionName); // одноимённая старая сессия перехватывается
            try
            {
                session.EnableProvider(DxgiProvider, TraceEventLevel.Informational);
                session.EnableProvider(D3D9Provider, TraceEventLevel.Informational);
            }
            catch
            {
                session.Dispose(); // без админа падаем здесь — не оставляем полусозданную сессию
                throw;
            }
            _session = session;
            _session.Source.Dynamic.All += OnEvent;
            _session.Source.UnhandledEvents += OnEvent;
            _thread = new Thread(() => { try { _session.Source.Process(); } catch { } })
            { IsBackground = true, Name = "ArcRecorderEtwFps" };
            _thread.Start();
            IsRunning = true;
        }

        void OnEvent(TraceEvent e)
        {
            int id = (int)e.ID;
            if ((e.ProviderGuid == DxgiProvider && id == DxgiPresentStart) ||
                (e.ProviderGuid == D3D9Provider && id == D3D9PresentStart))
            {
                if (e.ProcessID == Volatile.Read(ref _targetPid))
                    Interlocked.Increment(ref _count);
            }
        }

        /// <summary>
        /// Обновить PID активного окна (зовётся раз в секунду из таймера).
        /// Свой процесс игнорируем: если юзер кликнул в наш оверлей, продолжаем
        /// считать FPS последней активной игры (Roblox и любые DXGI/D3D9-приложения).
        /// </summary>
        public void UpdateForegroundPid()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid != 0 && pid != (uint)Environment.ProcessId)
                Volatile.Write(ref _targetPid, (int)pid);
        }

        /// <summary>Забрать накопленное число кадров и обнулить (кадры/сек = FPS).</summary>
        public int TakeFps() => Interlocked.Exchange(ref _count, 0);

        /// <summary>Среднее время кадра в мс для данного FPS (приближение к LAT у NVIDIA).</summary>
        public static double FrameTimeMs(int fps) => fps > 0 ? 1000.0 / fps : 0;

        public void Stop()
        {
            if (!IsRunning) return;
            try { _session?.Dispose(); } catch { }
            _session = null;
            IsRunning = false;
        }

        public void Dispose() => Stop();
    }
}
