using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PortAudioSharp;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>
    /// PortAudio microphone capture for SIPSorcery (<see cref="IAudioSource"/>), macOS / Linux / Windows.
    /// Built on PortAudioSharp2: an input stream in callback mode delivers 16-bit PCM; frames are
    /// re-chunked to exactly 20 ms, gain/AEC applied, and raised via <see cref="OnAudioSourceRawSample"/>
    /// (SIPSorcery encodes them to the negotiated G.711 format).
    /// </summary>
    public class PortAudioAudioSource : IAudioSource, IDisposable
    {
        private readonly int _deviceIndex;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly float _gainMultiplier;

        private readonly object _lock = new();
        private IDisposable? _runtime;
        private PortAudioSharp.Stream? _stream;
        private PortAudioSharp.Stream.Callback? _callback;
        private int _streamChannels = 1;
        private bool _isStarted;
        private volatile bool _isPaused;
        private bool _isDisposed;

        // 20 ms mono frame assembler
        private readonly short[] _frame;
        private int _frameFill;
        private readonly AudioSamplingRatesEnum _rateEnum;

        private AudioFormat? _currentFormat;
        private readonly List<AudioFormat> _supportedFormats = new();

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event SourceErrorDelegate? OnAudioSourceError;

        /// <summary>Optional AEC (non-Windows: SoftwareAec). Capture frames are processed in-place.</summary>
        public IEchoCanceller? EchoCanceller { get; set; }

        /// <summary>Tap of raw mono PCM (after AEC, before codec) — used for call recording.</summary>
        public Action<short[]>? OnRawPcmFrameTap { get; set; }

        public int SampleRate => _sampleRate;

        public PortAudioAudioSource(int deviceIndex = -1, int sampleRate = 8000, int channels = 1, float gainMultiplier = 1.0f)
        {
            _deviceIndex = deviceIndex;
            _sampleRate = sampleRate == 16000 ? 16000 : 8000; // G.711 = 8 kHz, G.722 = 16 kHz
            _channels = Math.Max(1, channels);
            _gainMultiplier = Math.Max(1.0f, Math.Min(5.0f, gainMultiplier));
            _frame = new short[_sampleRate / 50];
            _rateEnum = _sampleRate == 16000 ? AudioSamplingRatesEnum.Rate16KHz : AudioSamplingRatesEnum.Rate8KHz;
            InitializeSupportedFormats();
        }

        private void InitializeSupportedFormats()
        {
            try
            {
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, 1));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMA, _sampleRate, 1));
            }
            catch
            {
                _supportedFormats.Clear();
                try { _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, 8000, 1)); } catch { }
            }
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            lock (_lock) _supportedFormats.RemoveAll(f => !filter(f));
        }

        public void SetAudioSourceFormat(AudioFormat audioFormat)
        {
            lock (_lock)
            {
                try
                {
                    if (audioFormat.FormatID > 127)
                    {
                        _currentFormat = new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, 1);
                        return;
                    }
                }
                catch { }
                _currentFormat = audioFormat;
            }
        }

        public Task StartAudio()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(PortAudioAudioSource));

            lock (_lock)
            {
                if (_isStarted) return Task.CompletedTask;
                try
                {
                    _runtime ??= PortAudioRuntime.Acquire();

                    var inParams = PortAudioRuntime.MakeParams(_deviceIndex, input: true, _channels, out int device);
                    if (inParams == null)
                    {
                        OnAudioSourceError?.Invoke("No input device available");
                        return Task.CompletedTask;
                    }
                    _streamChannels = inParams.Value.channelCount;

                    _callback = InputCallback;
                    uint framesPerBuffer = (uint)(_sampleRate / 50); // 20 ms
                    _stream = new PortAudioSharp.Stream(inParams, null, _sampleRate, framesPerBuffer, StreamFlags.ClipOff, _callback, IntPtr.Zero);
                    _frameFill = 0;
                    _stream.Start();

                    _isStarted = true;
                    _isPaused = false;
                    AppLog.Log($"[PortAudioSource] started (device={device}, rate={_sampleRate}, ch={_streamChannels}, gain={_gainMultiplier:0.0})");
                }
                catch (Exception ex)
                {
                    OnAudioSourceError?.Invoke($"Error starting audio: {ex.Message}");
                    AppLog.Log($"[PortAudioSource] start failed: {ex}");
                    try { _stream?.Dispose(); } catch { }
                    _stream = null;
                }
            }
            return Task.CompletedTask;
        }

        private StreamCallbackResult InputCallback(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            if (input == IntPtr.Zero || _isPaused || !_isStarted) return StreamCallbackResult.Continue;
            int frames = (int)frameCount;
            try
            {
                unsafe
                {
                    short* src = (short*)input;
                    for (int i = 0; i < frames; i++)
                    {
                        int s;
                        if (_streamChannels == 1)
                        {
                            s = src[i];
                        }
                        else
                        {
                            int sum = 0;
                            for (int c = 0; c < _streamChannels; c++) sum += src[i * _streamChannels + c];
                            s = sum / _streamChannels;
                        }

                        if (_gainMultiplier > 1.0f)
                        {
                            float g = s * _gainMultiplier;
                            s = g > short.MaxValue ? short.MaxValue : g < short.MinValue ? short.MinValue : (int)g;
                        }

                        _frame[_frameFill++] = (short)s;
                        if (_frameFill == _frame.Length)
                        {
                            _frameFill = 0;
                            EmitFrame();
                        }
                    }
                }
            }
            catch
            {
                // never throw across the native boundary
            }
            return StreamCallbackResult.Continue;
        }

        private void EmitFrame()
        {
            // Copy: subscribers may keep the array (recorder tap, encoder queue).
            var mono = new short[_frame.Length];
            Array.Copy(_frame, mono, mono.Length);

            try { EchoCanceller?.ProcessCapture(mono, mono.Length); } catch { }
            try { OnRawPcmFrameTap?.Invoke(mono); } catch { }

            var handler = OnAudioSourceRawSample;
            if (handler != null)
            {
                try { handler.Invoke(_rateEnum, 20, mono); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[PortAudioSource] raw sample handler: {ex.Message}"); }
            }
        }

        public Task PauseAudio() { _isPaused = true; return Task.CompletedTask; }
        public Task ResumeAudio() { _isPaused = false; return Task.CompletedTask; }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample) { }
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, byte[] sample) { }

        public Task CloseAudio()
        {
            lock (_lock)
            {
                if (!_isStarted && _stream == null) return Task.CompletedTask;
                _isStarted = false;
                _isPaused = false;
                try
                {
                    if (_stream != null)
                    {
                        try { _stream.Abort(); } catch { }
                        try { _stream.Dispose(); } catch { }
                    }
                }
                finally
                {
                    _stream = null;
                    _callback = null;
                    _frameFill = 0;
                    try { _runtime?.Dispose(); } catch { }
                    _runtime = null;
                }
            }
            return Task.CompletedTask;
        }

        public List<AudioFormat> GetAudioSourceFormats()
        {
            lock (_lock)
            {
                var safe = _supportedFormats.Where(f => { try { return f.FormatID <= 127 && f.ClockRate == _sampleRate; } catch { return false; } }).ToList();
                if (safe.Count == 0)
                {
                    try { safe.Add(new AudioFormat(AudioCodecsEnum.PCMU, 8000, 1)); } catch { }
                }
                return safe;
            }
        }

        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null || OnAudioSourceEncodedFrameReady != null;

        public bool IsAudioSourcePaused() => _isPaused;

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { CloseAudio(); } catch { }
        }
    }
}
