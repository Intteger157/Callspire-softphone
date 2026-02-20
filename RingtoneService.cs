using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Softphone
{
    /// <summary>
    /// Simple ringtone playback for incoming calls (SIP/WebRTC).
    /// Uses WASAPI (shared mode) by default. Falls back to WaveOutEvent if WASAPI fails.
    /// </summary>
    public sealed class RingtoneService : IDisposable
    {
        public static RingtoneService Instance { get; } = new RingtoneService();

        private readonly object _lock = new object();
        private IWavePlayer? _player;
        private RingtoneWaveProvider? _provider;
        private AudioFileReader? _audioFileReader;
        private LoopStream? _loopStream;
        private bool _isPlaying;
        private string? _deviceId; // MMDevice.ID, null/empty => default
        private float _volume = 0.7f; // 0..1
        private string _soundMode = "wav"; // "wav" or "tone"

        private RingtoneService() { }

        public void Configure(string? outputDeviceId, float volume, string? soundMode = null)
        {
            lock (_lock)
            {
                var newDeviceId = string.IsNullOrWhiteSpace(outputDeviceId) ? null : outputDeviceId;
                var newMode = (soundMode ?? _soundMode)?.Trim().ToLowerInvariant();
                if (newMode != "tone" && newMode != "wav") newMode = "wav";

                bool deviceChanged = !string.Equals(_deviceId, newDeviceId, StringComparison.Ordinal);
                bool modeChanged = !string.Equals(_soundMode, newMode, StringComparison.Ordinal);

                _deviceId = newDeviceId;
                _soundMode = newMode;
                _volume = Math.Max(0f, Math.Min(1f, volume));

                // Apply volume live if already playing.
                if (_provider != null)
                {
                    _provider.Volume = _volume;
                }
                if (_audioFileReader != null)
                {
                    _audioFileReader.Volume = _volume;
                }

                // If device changed mid-playback, restart on new device.
                if (_isPlaying)
                {
                    // restart only if we changed device or ringtone mode
                    if (deviceChanged || modeChanged)
                    {
                        Stop();
                        Start();
                    }
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

                    if (_soundMode == "tone")
                    {
                        // Built-in tone
                        _provider = new RingtoneWaveProvider(
                            sampleRate: 44100,
                            channels: 2,
                            freq1: 440,
                            freq2: 480,
                            gain: 0.18f);
                        _provider.Volume = _volume;
                        _player.Init(_provider);
                    }
                    else
                    {
                        // WAV file preferred (./ringtone/incoming_call.wav), fallback to built-in tone.
                        var wavPath = ResolveRingtoneWavPath();
                        if (!string.IsNullOrWhiteSpace(wavPath))
                        {
                            _audioFileReader = new AudioFileReader(wavPath);
                            _audioFileReader.Volume = _volume;
                            _loopStream = new LoopStream(_audioFileReader);
                            _player.Init(_loopStream);
                        }
                        else
                        {
                            _provider = new RingtoneWaveProvider(
                                sampleRate: 44100,
                                channels: 2,
                                freq1: 440,
                                freq2: 480,
                                gain: 0.18f);
                            _provider.Volume = _volume;
                            _player.Init(_provider);
                        }
                    }

                    _player.Play();
                    _isPlaying = true;
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[RingtoneService] Failed to start ringtone: {ex.Message}");
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
                // Shared mode so we can mix with other apps and not take exclusive control.
                using var enumerator = new MMDeviceEnumerator();
                MMDevice device;
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    device = enumerator.GetDevice(deviceId);
                }
                else
                {
                    device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                }
                return new WasapiOut(device, AudioClientShareMode.Shared, false, latency: 80);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[RingtoneService] WASAPI init failed, falling back to WaveOutEvent: {ex.Message}");
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

        private static string? ResolveRingtoneWavPath()
        {
            try
            {
                // Prefer ./ringtone/incoming_call.wav if present, else first *.wav in ./ringtone/
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var ringtoneDir = System.IO.Path.Combine(baseDir, "ringtone");
                var preferred = System.IO.Path.Combine(ringtoneDir, "incoming_call.wav");
                if (System.IO.File.Exists(preferred)) return preferred;

                if (!System.IO.Directory.Exists(ringtoneDir)) return null;
                var any = System.IO.Directory.GetFiles(ringtoneDir, "*.wav");
                if (any.Length > 0) return any[0];
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
        /// Generates a simple two-tone ringtone with a 2s on / 4s off cadence (looping).
        /// Includes small fade in/out ramps to reduce clicks.
        /// </summary>
        private sealed class RingtoneWaveProvider : WaveProvider32
        {
            private readonly int _sampleRate;
            private readonly int _channels;
            private readonly double _freq1;
            private readonly double _freq2;
            private readonly float _gain;
            public float Volume { get; set; } = 1f;

            // 2s on / 4s off cycle
            private readonly int _cycleSamples;
            private readonly int _onSamples;
            private readonly int _fadeSamples;

            private long _sampleIndex;

            public RingtoneWaveProvider(int sampleRate, int channels, double freq1, double freq2, float gain)
            {
                _sampleRate = sampleRate;
                _channels = Math.Max(1, channels);
                _freq1 = freq1;
                _freq2 = freq2;
                _gain = gain;

                // cycle = 6 seconds
                _cycleSamples = sampleRate * 6;
                _onSamples = sampleRate * 2;
                _fadeSamples = Math.Max(1, (int)(sampleRate * 0.01)); // 10ms

                SetWaveFormat(sampleRate, _channels);
            }

            public override int Read(float[] buffer, int offset, int sampleCount)
            {
                for (int n = 0; n < sampleCount; n += _channels)
                {
                    long posInCycle = _sampleIndex % _cycleSamples;
                    bool isOn = posInCycle < _onSamples;

                    float env = 0f;
                    if (isOn)
                    {
                        // Fade in/out at edges of "on" window
                        if (posInCycle < _fadeSamples)
                        {
                            env = (float)posInCycle / _fadeSamples;
                        }
                        else if (posInCycle > _onSamples - _fadeSamples)
                        {
                            env = (float)(_onSamples - posInCycle) / _fadeSamples;
                        }
                        else
                        {
                            env = 1f;
                        }
                    }

                    float sample = 0f;
                    if (env > 0f)
                    {
                        double t = (double)_sampleIndex / _sampleRate;
                        // Simple two-tone mix
                        double s = Math.Sin(2 * Math.PI * _freq1 * t) + Math.Sin(2 * Math.PI * _freq2 * t);
                        sample = (float)(s * 0.5 * _gain * env * Volume);
                    }

                    for (int ch = 0; ch < _channels; ch++)
                    {
                        buffer[offset + n + ch] = sample;
                    }

                    _sampleIndex++;
                }

                return sampleCount;
            }
        }

        /// <summary>
        /// Minimal loop wrapper for WaveStream.
        /// </summary>
        private sealed class LoopStream : WaveStream
        {
            private readonly WaveStream _source;

            public LoopStream(WaveStream source)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
            }

            public override WaveFormat WaveFormat => _source.WaveFormat;

            public override long Length => long.MaxValue; // infinite

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
                // Do not dispose _source here; outer service owns it.
                base.Dispose(disposing);
            }
        }
    }
}


