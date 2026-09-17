using System;
using System.IO;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone.Audio
{
    /// <summary>
    /// Cross-platform (macOS / Linux) tone and ringtone player on top of <see cref="PortAudioSink"/>.
    /// NAudio's <c>WaveOutEvent</c> is winmm-only, so the Windows <c>ToneGenerator</c> /
    /// <c>RingtoneService</c> cannot run here. This class generates 16 kHz mono PCM in a background
    /// thread and pushes 20 ms frames into a PortAudio output stream, pacing by the sink's queue depth.
    ///
    /// Patterns:
    /// <list type="bullet">
    ///   <item>Ringback — 425 Hz, 1 s on / 4 s off</item>
    ///   <item>Busy — 425 Hz, 0.35 s on / 0.35 s off</item>
    ///   <item>Ringtone — <c>./ringtone/incoming_call.wav</c> looped, or 440+480 Hz 2 s on / 4 s off</item>
    /// </list>
    /// </summary>
    public sealed class PortAudioTonePlayer : ITonePlayer
    {
        private enum Pattern { None, Ringback, Busy, Ringtone }

        private const int SampleRate = 16000;
        private const int FrameSamples = SampleRate / 50; // 20 ms

        private readonly object _lock = new();
        private PortAudioSink? _sink;
        private Thread? _thread;
        private CancellationTokenSource? _cts;
        private Pattern _pattern = Pattern.None;
        private float _volume = 0.7f;
        private int _deviceIndex = -1;
        private bool _disposed;

        /// <summary>Shared instance used by the ringtone hooks.</summary>
        public static PortAudioTonePlayer Shared { get; } = new();

        public bool IsPlaying { get { lock (_lock) return _pattern != Pattern.None; } }

        public void Configure(int deviceIndex, float volume)
        {
            lock (_lock)
            {
                _deviceIndex = deviceIndex;
                _volume = Math.Clamp(volume, 0f, 1f);
            }
        }

        public void PlayRingbackTone() => Start(Pattern.Ringback);
        public void PlayBusyTone() => Start(Pattern.Busy);
        public void PlayRingtone() => Start(Pattern.Ringtone);

        private void Start(Pattern pattern)
        {
            lock (_lock)
            {
                if (_disposed) return;
                if (_pattern == pattern && _thread is { IsAlive: true }) return;
                StopInternal();

                _pattern = pattern;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _thread = new Thread(() => Run(pattern, token))
                {
                    IsBackground = true,
                    Name = $"PortAudioTone-{pattern}",
                    Priority = ThreadPriority.AboveNormal,
                };
                _thread.Start();
            }
        }

        public void Stop()
        {
            lock (_lock) StopInternal();
        }

        private void StopInternal()
        {
            _pattern = Pattern.None;
            try { _cts?.Cancel(); } catch { }
            var t = _thread;
            _thread = null;
            _cts = null;
            if (t != null && t != Thread.CurrentThread)
            {
                try { t.Join(500); } catch { }
            }
            var sink = _sink;
            _sink = null;
            try { sink?.Dispose(); } catch { }
        }

        private void Run(Pattern pattern, CancellationToken token)
        {
            PortAudioSink? sink = null;
            try
            {
                sink = new PortAudioSink(_deviceIndex, SampleRate, 1);
                sink.OnAudioSinkError += msg => AppLog.Log($"[PortAudioTonePlayer] sink error: {msg}");
                sink.StartAudioSink().GetAwaiter().GetResult();
                lock (_lock) _sink = sink;

                var frame = new short[FrameSamples];
                long sampleIndex = 0;

                using var wav = pattern == Pattern.Ringtone ? TryOpenRingtoneWav() : null;

                // The sink is callback-driven (non-blocking writes), so pace ourselves by keeping
                // ~80-120 ms queued: top up while below the low-water mark, otherwise sleep a bit.
                const int lowWaterMs = 80;
                while (!token.IsCancellationRequested)
                {
                    if (sink.QueuedMilliseconds >= lowWaterMs)
                    {
                        if (token.WaitHandle.WaitOne(10)) break;
                        continue;
                    }

                    if (wav != null)
                    {
                        if (!wav.ReadFrame(frame)) break;
                    }
                    else
                    {
                        FillTone(frame, ref sampleIndex, pattern);
                    }

                    sink.GotAudioSample(AudioSamplingRatesEnum.Rate16KHz, 20, frame);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PortAudioTonePlayer] {pattern} error: {ex.Message}");
            }
            finally
            {
                try { sink?.Dispose(); } catch { }
                lock (_lock) { if (ReferenceEquals(_sink, sink)) _sink = null; }
            }
        }

        private void FillTone(short[] frame, ref long sampleIndex, Pattern pattern)
        {
            (double onSec, double offSec, double f1, double f2) = pattern switch
            {
                Pattern.Ringback => (1.0, 4.0, 425.0, 0.0),
                Pattern.Busy => (0.35, 0.35, 425.0, 0.0),
                Pattern.Ringtone => (2.0, 4.0, 440.0, 480.0),
                _ => (0.0, 1.0, 0.0, 0.0),
            };
            long cycle = (long)((onSec + offSec) * SampleRate);
            long on = (long)(onSec * SampleRate);
            int fade = SampleRate / 100; // 10 ms
            float gain = 0.25f * _volume;

            for (int i = 0; i < frame.Length; i++, sampleIndex++)
            {
                long pos = cycle > 0 ? sampleIndex % cycle : 0;
                float env = 0f;
                if (pos < on)
                {
                    env = pos < fade ? (float)pos / fade
                        : pos > on - fade ? (float)(on - pos) / fade
                        : 1f;
                }
                double t = (double)sampleIndex / SampleRate;
                double s = f2 > 0
                    ? 0.5 * (Math.Sin(2 * Math.PI * f1 * t) + Math.Sin(2 * Math.PI * f2 * t))
                    : Math.Sin(2 * Math.PI * f1 * t);
                frame[i] = (short)Math.Clamp(s * env * gain * short.MaxValue, short.MinValue, short.MaxValue);
            }
        }

        private WavLoop? TryOpenRingtoneWav()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ringtone");
                var preferred = Path.Combine(dir, "incoming_call.wav");
                string? path = File.Exists(preferred) ? preferred
                    : Directory.Exists(dir) ? Array.Find(Directory.GetFiles(dir, "*.wav"), _ => true)
                    : null;
                if (path == null) return null;
                return new WavLoop(path, _volume);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PortAudioTonePlayer] ringtone wav open failed: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                StopInternal();
            }
        }

        /// <summary>Loops a WAV file resampled to 16 kHz mono using NAudio's managed (cross-platform) providers.</summary>
        private sealed class WavLoop : IDisposable
        {
            private readonly WaveFileReader _reader;
            private readonly ISampleProvider _provider;
            private readonly float[] _buf = new float[FrameSamples];
            private readonly float _volume;

            public WavLoop(string path, float volume)
            {
                _volume = volume;
                _reader = new WaveFileReader(path);
                ISampleProvider sp = _reader.ToSampleProvider();
                if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
                if (sp.WaveFormat.SampleRate != SampleRate) sp = new WdlResamplingSampleProvider(sp, SampleRate);
                _provider = sp;
            }

            public bool ReadFrame(short[] frame)
            {
                int total = 0;
                while (total < frame.Length)
                {
                    int read = _provider.Read(_buf, 0, frame.Length - total);
                    if (read == 0)
                    {
                        _reader.Position = 0;
                        continue;
                    }
                    for (int i = 0; i < read; i++)
                        frame[total + i] = (short)Math.Clamp(_buf[i] * _volume * short.MaxValue, short.MinValue, short.MaxValue);
                    total += read;
                }
                return true;
            }

            public void Dispose() => _reader.Dispose();
        }
    }
}
