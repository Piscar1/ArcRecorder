using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ArcRecorder
{
    /// <summary>
    /// Микшер системного звука (WASAPI loopback) + микрофона.
    /// Гонит PCM s16le 48kHz stereo в именованный канал, который ffmpeg читает как вход, в реальном времени.
    /// Если источников нет — гонит тишину, чтобы у файла всегда была звуковая дорожка.
    /// </summary>
    public class AudioPipeSource
    {
        readonly MixingSampleProvider _mixer;
        readonly System.Collections.Generic.List<BufferedWaveProvider> _buffers =
            new System.Collections.Generic.List<BufferedWaveProvider>();
        readonly ManualResetEventSlim _videoStarted = new ManualResetEventSlim(false);
        WasapiLoopbackCapture _loopback;
        WasapiCapture _mic;
        Thread _pump;
        volatile bool _running;
        Stream _out;
        int _stopped;
        long _videoWaitMs = -1, _maxWriteMs;

        /// <summary>Разбивка времени последней остановки — для ffmpeg.log.</summary>
        public string StopTimings { get; private set; }

        readonly NamedPipeServerStream _server;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>Путь канала для ffmpeg: -i "\\.\pipe\ArcRecorder_audio_…".</summary>
        public string PipePath { get; }

        public AudioPipeSource(bool systemAudio, bool mic)
        {
            string pipeName = "ArcRecorder_audio_" + Guid.NewGuid().ToString("N");
            PipePath = @"\\.\pipe\" + pipeName;
            _server = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)) { ReadFully = true };
            if (systemAudio)
            {
                try { _loopback = new WasapiLoopbackCapture(); AddCapture(_loopback); }
                catch { _loopback = null; }
            }
            if (mic)
            {
                try { _mic = new WasapiCapture(); AddCapture(_mic); }
                catch { _mic = null; }
            }
        }

        void AddCapture(WasapiCapture capture)
        {
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(2)
            };
            capture.DataAvailable += (o, e) => buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _buffers.Add(buffer);

            ISampleProvider sp = buffer.ToSampleProvider();
            if (sp.WaveFormat.Channels == 1)
                sp = new MonoToStereoSampleProvider(sp);
            else if (sp.WaveFormat.Channels > 2)
                sp = new TakeStereo(sp);
            if (sp.WaveFormat.SampleRate != 48000)
                sp = new WdlResamplingSampleProvider(sp, 48000);
            _mixer.AddMixerInput(sp);
        }

        /// <summary>Запускает захват и поток звука; сам поток сначала ждёт, пока ffmpeg подключится к каналу.</summary>
        public void Start()
        {
            _out = _server;
            _running = true;
            try { _loopback?.StartRecording(); } catch { }
            try { _mic?.StartRecording(); } catch { }
            _pump = new Thread(PumpLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _pump.Start();
        }

        /// <summary>ffmpeg начал выдавать кадры — можно лить звук (зовёт FfmpegSession по -progress).</summary>
        public void SignalVideoStarted() => _videoStarted.Set();

        void PumpLoop()
        {
            // ffmpeg открывает канал, когда доходит до своих входов; ждём, но не вечно (Stop отменяет ожидание)
            try
            {
                if (!_server.WaitForConnectionAsync(_cts.Token).Wait(15000)) return;
            }
            catch { return; }

            // Прайминг: 100 мс тишины, чтобы ffmpeg смог пробить raw-поток из канала и запустить граф.
            // Без этого он может висеть в ожидании первых байтов звука и не начать захват видео.
            try
            {
                var prime = new byte[4800 * 2 * 2]; // 100 мс s16le stereo 48kHz
                _out.Write(prime, 0, prime.Length);
                _out.Flush();
            }
            catch { return; }

            // Ждём, пока видеозахват реально пойдёт (инициализация ddagrab+QSV занимает до ~1.5 сек).
            // Если начать лить звук сразу — он окажется в файле раньше видео и «отстанет» на всю запись.
            // Таймаут 5 сек — страховка, если ffmpeg не репортит прогресс.
            int waited = 0;
            while (_running && !_videoStarted.IsSet && waited < 5000)
            {
                Thread.Sleep(10);
                waited += 10;
            }
            _videoWaitMs = waited;
            if (!_running) return;
            // выбрасываем звук, накопленный за время инициализации видео — контент совпадёт с первым кадром
            foreach (var b in _buffers) { try { b.ClearBuffer(); } catch { } }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            long sentSamples = 0; // сэмплов на канал
            var floats = new float[48000 * 2];
            var bytes = new byte[floats.Length * 2];
            var scratch = Array.Empty<byte>();
            try
            {
                while (_running)
                {
                    long target = sw.ElapsedMilliseconds * 48; // 48 сэмплов на мс
                    int todo = (int)Math.Min(target - sentSamples, 48000);
                    if (todo > 0)
                    {
                        int read = _mixer.Read(floats, 0, todo * 2); // ReadFully => всегда полный буфер
                        int bi = 0;
                        for (int i = 0; i < read; i++)
                        {
                            float f = floats[i];
                            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
                            short v = (short)(f * short.MaxValue);
                            bytes[bi++] = (byte)v;
                            bytes[bi++] = (byte)(v >> 8);
                        }
                        long w0 = sw.ElapsedMilliseconds;
                        _out.Write(bytes, 0, bi);
                        _out.Flush();
                        long wd = sw.ElapsedMilliseconds - w0; // долгая запись = ffmpeg перестал читать звук
                        if (wd > _maxWriteMs) _maxWriteMs = wd;
                        sentSamples += todo;
                        // Часы звуковой карты и Stopwatch расходятся: если источник обгоняет, в его буфере копится
                        // лишнее и звук медленно отстаёт от видео (до 2 сек — размера буфера). Срезаем излишек.
                        foreach (var b in _buffers) TrimBacklog(b, ref scratch);
                    }
                    Thread.Sleep(20);
                }
            }
            catch { /* пайп закрыт — ffmpeg завершился */ }
        }

        /// <summary>Оставляет в буфере источника ~50 мс, если там накопилось больше 200 мс сверх отданного.</summary>
        static void TrimBacklog(BufferedWaveProvider b, ref byte[] scratch)
        {
            var wf = b.WaveFormat;
            int buffered = b.BufferedBytes;
            if (buffered <= wf.AverageBytesPerSecond / 5) return;
            int drop = buffered - wf.AverageBytesPerSecond / 20;
            drop -= drop % wf.BlockAlign;
            if (drop <= 0) return;
            if (scratch.Length < drop) scratch = new byte[drop];
            b.Read(scratch, 0, drop);
        }

        /// <summary>Останавливает захват и закрывает звуковой канал (зовётся после выхода ffmpeg или при его падении).</summary>
        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 1) return; // зовут и Stop, и обработчик выхода ffmpeg
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _running = false;
            try { _cts.Cancel(); } catch { } // если ffmpeg так и не подключился к каналу
            try { _pump?.Join(500); } catch { }
            long tJoin = sw.ElapsedMilliseconds;
            try { _loopback?.StopRecording(); _loopback?.Dispose(); } catch { }
            long tLoop = sw.ElapsedMilliseconds;
            try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
            long tMic = sw.ElapsedMilliseconds;
            try { _server.Dispose(); } catch { }
            StopTimings = $" (ждали видео {_videoWaitMs} мс, макс. запись в канал {_maxWriteMs} мс; при стопе: поток звука {tJoin} мс, " +
                          $"loopback {tLoop - tJoin} мс, микрофон {tMic - tLoop} мс, закрытие канала {sw.ElapsedMilliseconds - tMic} мс)";
        }

        /// <summary>Берёт первые 2 канала из многоканального источника.</summary>
        class TakeStereo : ISampleProvider
        {
            readonly ISampleProvider _src;
            float[] _tmp = Array.Empty<float>();
            public TakeStereo(ISampleProvider src) { _src = src; }
            public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(_src.WaveFormat.SampleRate, 2);
            public int Read(float[] buffer, int offset, int count)
            {
                int ch = _src.WaveFormat.Channels;
                int frames = count / 2;
                int need = frames * ch;
                if (_tmp.Length < need) _tmp = new float[need];
                int framesRead = _src.Read(_tmp, 0, need) / ch;
                for (int i = 0; i < framesRead; i++)
                {
                    buffer[offset + i * 2] = _tmp[i * ch];
                    buffer[offset + i * 2 + 1] = _tmp[i * ch + 1];
                }
                return framesRead * 2;
            }
        }
    }
}
