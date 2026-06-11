#if WINDOWS
using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Softphone
{
    /// <summary>
    /// Local hold music playback (SIP local hold / WebRTC remote hold).
    /// Not the incoming-call ringtone — uses hold_music.wav or a soft built-in tone.
    /// </summary>
    public sealed class HoldMusicService : IDisposable
    {
        public static HoldMusicService Instance { get; } = new HoldMusicService();

        private readonly object _lock = new object();
        private IWavePlayer? _player;
        private HoldMusicWaveProvider? _provider;
        private AudioFileReader? _audioFileReader;
        private LoopStream? _loopStream;
        private bool _isPlaying;
        private string? _deviceId;
        private float _volume = 0.5f;

        private HoldMusicService() { }

        public void Configure(string? outputDeviceId, float volume)
        {
            lock (_lock)
            {
                var newDeviceId = string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId;
                bool deviceChanged = !string.Equals(_deviceId, newDeviceId, StringComparison.Ordinal);

                _deviceId = newDeviceId;
                _volume = Math.Max(0f, Math.Min(1f, volume));

                if (_provider != null)
                    _provider.Volume = _volume;
                if (_audioFileReader != null)
                    _audioFileReader.Volume = _volume;

                if (_isPlaying && deviceChanged)
                {
                    Stop();
                    Start();
                }
            }
        }

        public void Start()
        {
            lock (_lock)
            {
                if (_isPlaying) return;

                try
                {
                    _player = CreateWasapiOutOrFallback(_deviceId);

                    var wavPath = ResolveHoldMusicWavPath();
                    if (!string.IsNullOrWhiteSpace(wavPath))
                    {
                        _audioFileReader = new AudioFileReader(wavPath);
                        _audioFileReader.Volume = _volume;
                        _loopStream = new LoopStream(_audioFileReader);
                        _player.Init(_loopStream);
                    }
                    else
                    {
                        _provider = new HoldMusicWaveProvider(sampleRate: 44100, channels: 2, gain: 0.12f);
                        _provider.Volume = _volume;
                        _player.Init(_provider);
                    }

                    _player.Play();
                    _isPlaying = true;
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[HoldMusicService] Failed to start hold music: {ex.Message}");
                    SafeDisposePlayer();
                    SafeDisposeAudioFile();
                    _provider = null;
                    _isPlaying = false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_isPlaying)
                {
                    SafeDisposePlayer();
                    _provider = null;
                    return;
                }

                try
                {
                    _player?.Stop();
                }
                catch
                {
                    // ignore
                }
                finally
                {
                    SafeDisposePlayer();
                    SafeDisposeAudioFile();
                    _provider = null;
                    _isPlaying = false;
                }
            }
        }

        private IWavePlayer CreateWasapiOutOrFallback(string? deviceId)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device;
                if (!string.IsNullOrWhiteSpace(deviceId))
                    device = enumerator.GetDevice(deviceId);
                else
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return new WasapiOut(device, AudioClientShareMode.Shared, false, latency: 80);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[HoldMusicService] WASAPI init failed, falling back to WaveOutEvent: {ex.Message}");
                return new WaveOutEvent();
            }
        }

        private void SafeDisposePlayer()
        {
            try { _player?.Dispose(); } catch { }
            _player = null;
        }

        private void SafeDisposeAudioFile()
        {
            try { _loopStream?.Dispose(); } catch { }
            _loopStream = null;
            try { _audioFileReader?.Dispose(); } catch { }
            _audioFileReader = null;
        }

        private static string? ResolveHoldMusicWavPath()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var ringtoneDir = System.IO.Path.Combine(baseDir, "ringtone");
                foreach (var name in new[] { "hold_music.wav", "on_hold.wav" })
                {
                    var path = System.IO.Path.Combine(ringtoneDir, name);
                    if (System.IO.File.Exists(path)) return path;
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        /// Soft dual-tone with slow swell — distinct from incoming ring cadence (2s on / 4s off).
        /// </summary>
        private sealed class HoldMusicWaveProvider : WaveProvider32
        {
            private readonly int _sampleRate;
            private readonly int _channels;
            private readonly float _gain;
            public float Volume { get; set; } = 1f;

            private readonly double _freq1 = 329.63; // E4
            private readonly double _freq2 = 415.30; // G#4
            private long _sampleIndex;

            public HoldMusicWaveProvider(int sampleRate, int channels, float gain)
            {
                _sampleRate = sampleRate;
                _channels = Math.Max(1, channels);
                _gain = gain;
                SetWaveFormat(sampleRate, _channels);
            }

            public override int Read(float[] buffer, int offset, int sampleCount)
            {
                for (int n = 0; n < sampleCount; n += _channels)
                {
                    double t = (double)_sampleIndex / _sampleRate;
                    // ~6s gentle swell (0.65–1.0), always audible — not ring on/off
                    double swell = 0.825 + 0.175 * Math.Sin(2 * Math.PI * t / 6.0);
                    double s = Math.Sin(2 * Math.PI * _freq1 * t) + Math.Sin(2 * Math.PI * _freq2 * t);
                    float sample = (float)(s * 0.5 * _gain * swell * Volume);

                    for (int ch = 0; ch < _channels; ch++)
                        buffer[offset + n + ch] = sample;

                    _sampleIndex++;
                }

                return sampleCount;
            }
        }

        private sealed class LoopStream : WaveStream
        {
            private readonly WaveStream _source;

            public LoopStream(WaveStream source)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
            }

            public override WaveFormat WaveFormat => _source.WaveFormat;

            public override long Length => long.MaxValue;

            public override long Position
            {
                get => _source.Position;
                set => _source.Position = value;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int totalRead = 0;
                while (totalRead < count)
                {
                    int read = _source.Read(buffer, offset + totalRead, count - totalRead);
                    if (read == 0)
                    {
                        _source.Position = 0;
                        continue;
                    }
                    totalRead += read;
                }
                return totalRead;
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
            }
        }
    }
}
#endif // WINDOWS
