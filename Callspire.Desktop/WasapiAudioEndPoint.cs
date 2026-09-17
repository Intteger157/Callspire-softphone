#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// WASAPI-based audio endpoint for SIP calls.
    /// Uses Windows Communications device role which provides built-in:
    /// - Acoustic Echo Cancellation (AEC)
    /// - Noise Suppression (NS)
    /// - Automatic Gain Control (AGC)
    /// This is the same audio processing pipeline that browsers use for WebRTC.
    /// </summary>
    public class WasapiAudioEndPoint : IAudioSource, IAudioSink, IDisposable
    {
        private const int CODEC_RATE = 8000;
        private const int FRAME_MS = 20;
        private const int FRAME_SAMPLES = CODEC_RATE * FRAME_MS / 1000; // 160 samples per 20ms frame

        private readonly AudioEncoder _audioEncoder;
        private readonly int _audioInDeviceIndex;
        private readonly int _audioOutDeviceIndex;

        private const int RECORDING_RATE = 48000;

        /// <summary>
        /// Optional tap for raw PCM outbound frames at 48 kHz mono s16le — full bandwidth
        /// directly from the microphone, bypassing the 8 kHz codec downsample.
        /// Chunk sizes vary (depends on WASAPI buffer). Suitable for writing to a PCM file.
        /// </summary>
        public Action<short[]>? OnRawPcmFrameTap;

        // Capture (microphone)
        private WasapiCapture? _capture;
        private int _captureNativeRate;
        private int _captureNativeChannels;
        private int _captureNativeBitsPerSample;
        private bool _captureIsFloat;
        private short[] _captureFrame = new short[FRAME_SAMPLES];
        private int _captureFramePos;
        private readonly object _captureLock = new();

        // High-quality resampler for codec path (→ 8 kHz)
        private BufferedWaveProvider? _resamplerInput;
        private WdlResamplingSampleProvider? _resampler;

        // Second resampler for recording tap (→ 48 kHz) — preserves full voice bandwidth
        private BufferedWaveProvider? _resamplerInput48k;
        private WdlResamplingSampleProvider? _resampler48k;

        // Guard against concurrent StartAudio / StartAudioSink (VoIPMediaSession + SipService both call these).
        private readonly object _startLock = new();

        // Playback (speaker)
        private IWavePlayer? _playback;
        private BufferedWaveProvider? _playbackBuffer;
        private AecTapWaveProvider? _aecTap;
        private readonly AecMetrics _aecMetrics = new();

        // Codec
        private AudioFormat? _sendFormat;
        private AudioFormat? _recvFormat;
        private List<AudioFormat> _supportedFormats;

        // State
        private bool _isCapturing;
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isClosed;

        /// <summary>
        /// Native AEC handle (if a native AEC DLL is loaded).
        /// When non-zero, far-end audio is fed to the native AEC as the reference
        /// and captured microphone audio is processed through echo cancellation.
        /// </summary>
        private Softphone.Audio.IEchoCanceller? _echoCanceller;

        /// <summary>
        /// Enable or disable acoustic echo cancellation.
        /// The canceller is selected by EchoCancellerFactory (Windows: native webrtc_apm.dll;
        /// other platforms would get SoftwareAec — WASAPI itself is Windows-only).
        /// </summary>
        public bool AecEnabled
        {
            get => _echoCanceller != null;
            set
            {
                if (value && _echoCanceller == null)
                {
                    _echoCanceller = Softphone.Audio.EchoCancellerFactory.Create(CODEC_RATE, 1, FRAME_MS);
                    if (_echoCanceller != null)
                        MainWindow.Log($"[WasapiAudio] AEC enabled: {_echoCanceller.Description}");
                    else if (!NativeAec.IsNativeAecRuntimeSupported)
                        MainWindow.Log("[WasapiAudio] Native AEC unavailable: CPU has no AVX2 (webrtc_apm.dll requires it). Echo cancellation off; calls still work.");
                    else
                        MainWindow.Log("[WasapiAudio] ⚠ Native AEC DLL not found — echo cancellation disabled. " +
                                       "Place webrtc_apm.dll next to Callspire.exe to enable AEC.");
                }
                else if (!value && _echoCanceller != null)
                {
                    try { _echoCanceller.Dispose(); } catch { }
                    _echoCanceller = null;
                    MainWindow.Log("[WasapiAudio] AEC disabled");
                }
            }
        }

        // Stats
        private int _captureFramesSent;
        private int _playbackFramesDecoded;

        // ── Playback noise gate (remove low-level hiss on silence) ──
        // Some decoders/PLC paths output low-level noise during silence.
        // We apply a light RMS-based gate on the *incoming* (remote) audio.
        private bool _playbackNoiseGateEnabled = true;
        private float _playbackNoiseGateThresholdRms = 0.0045f; // ~ -47 dBFS
        private int _playbackNoiseGateHangoverFrames = 8; // keep open for ~160ms at 20ms frames
        private int _playbackNoiseGateBelowCount = 0;
        private float _playbackNoiseGateGain = 1.0f; // smoothed gain to avoid clicks

        // ── AEC debug (RMS before/after on a small interval) ──
        private long _aecDebugFrames;
        private long _aecDebugLastLogTicks;

        // Events (IAudioSource)
        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
#pragma warning disable CS0067 // Event is never used (required by interface)
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
#pragma warning restore CS0067
        public event SourceErrorDelegate? OnAudioSourceError;

        // Events (IAudioSink)
        public event SourceErrorDelegate? OnAudioSinkError;

        // IAudioSource interface methods (not properties)
        public bool HasEncodedAudioSubscribers() =>
            OnAudioSourceEncodedSample != null || OnAudioSourceEncodedFrameReady != null;
        public bool IsAudioSourcePaused() => _isPaused;

        public WasapiAudioEndPoint(AudioEncoder audioEncoder, int audioOutDeviceIndex = -1, int audioInDeviceIndex = -1)
        {
            _audioEncoder = audioEncoder ?? throw new ArgumentNullException(nameof(audioEncoder));
            _audioInDeviceIndex = audioInDeviceIndex;
            _audioOutDeviceIndex = audioOutDeviceIndex;
            _supportedFormats = new List<AudioFormat>(_audioEncoder.SupportedFormats);
        }

        /// <summary>
        /// Creates MediaEndPoints for VoIPMediaSession.
        /// </summary>
        public MediaEndPoints ToMediaEndPoints()
        {
            return new MediaEndPoints
            {
                AudioSource = this,
                AudioSink = this
            };
        }

        #region IAudioSource

        public List<AudioFormat> GetAudioSourceFormats() => _supportedFormats;

        public void SetAudioSourceFormat(AudioFormat format)
        {
            _sendFormat = format;
            MainWindow.Log($"[WasapiAudio] Send format set: {format.FormatName} PT={format.FormatID}");
        }

        /// <summary>
        /// Move the specified codec to the front of the supported formats list.
        /// Must be called BEFORE VoIPMediaSession is created, so that SDP offer
        /// lists this codec first and VoIPMediaSession negotiates it correctly.
        /// </summary>
        public void PrioritizeFormat(int formatID)
        {
            var priority = _supportedFormats.Where(f => f.FormatID == formatID).ToList();
            var rest = _supportedFormats.Where(f => f.FormatID != formatID).ToList();
            _supportedFormats = priority.Concat(rest).ToList();
            MainWindow.Log($"[WasapiAudio] ✓ Format PT={formatID} prioritized. Codec order: " +
                string.Join(", ", _supportedFormats.Select(f => $"{f.FormatName}({f.FormatID})")));
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            _supportedFormats = _supportedFormats.Where(filter).ToList();
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            // Not used for WASAPI capture
        }

        public Task StartAudio()
        {
            lock (_startLock)
            {
                if (_isCapturing || _isClosed) return Task.CompletedTask;

                try
                {
                    var device = GetCaptureDevice();
                    _capture = new WasapiCapture(device);

                    var nf = _capture.WaveFormat;
                    _captureNativeRate = nf.SampleRate;
                    _captureNativeChannels = nf.Channels;
                    _captureNativeBitsPerSample = nf.BitsPerSample;
                    _captureIsFloat = nf.Encoding == WaveFormatEncoding.IeeeFloat;

                    MainWindow.Log($"[WasapiAudio] Capture device: {device.FriendlyName}");
                    MainWindow.Log($"[WasapiAudio] Capture native format: {nf.SampleRate}Hz, {nf.BitsPerSample}bit, {nf.Channels}ch, {nf.Encoding}");

                    // Set up high-quality resampler chain:
                    // BufferedWaveProvider (mono float @ native rate)
                    //   → WaveToSampleProvider (handles IEEE float natively)
                    //     → WdlResamplingSampleProvider (→ 8000 Hz)
                    // WDL Resampler uses windowed sinc interpolation — same quality as Cockos Reaper DAW
                    if (_captureNativeRate != CODEC_RATE)
                    {
                        var monoFloatFormat = WaveFormat.CreateIeeeFloatWaveFormat(_captureNativeRate, 1);
                        _resamplerInput = new BufferedWaveProvider(monoFloatFormat)
                        {
                            ReadFully = false,
                            DiscardOnBufferOverflow = true,
                            BufferDuration = TimeSpan.FromMilliseconds(200)
                        };
                        var sampleProvider = new WaveToSampleProvider(_resamplerInput);
                        _resampler = new WdlResamplingSampleProvider(sampleProvider, CODEC_RATE);
                        MainWindow.Log($"[WasapiAudio] ✓ WDL Resampler initialized: {_captureNativeRate} → {CODEC_RATE} Hz (codec path)");
                    }
                    else
                    {
                        MainWindow.Log($"[WasapiAudio] Capture rate matches codec rate ({CODEC_RATE} Hz), no resampling needed");
                    }

                    // Second resampler: native rate → 48 kHz for recording tap (full voice bandwidth)
                    if (_captureNativeRate != RECORDING_RATE)
                    {
                        var monoFloatFormat48k = WaveFormat.CreateIeeeFloatWaveFormat(_captureNativeRate, 1);
                        _resamplerInput48k = new BufferedWaveProvider(monoFloatFormat48k)
                        {
                            ReadFully = false,
                            DiscardOnBufferOverflow = true,
                            BufferDuration = TimeSpan.FromMilliseconds(200)
                        };
                        var sampleProvider48k = new WaveToSampleProvider(_resamplerInput48k);
                        _resampler48k = new WdlResamplingSampleProvider(sampleProvider48k, RECORDING_RATE);
                        MainWindow.Log($"[WasapiAudio] ✓ WDL Resampler initialized: {_captureNativeRate} → {RECORDING_RATE} Hz (recording path)");
                    }

                    _capture.DataAvailable += OnCaptureData;
                    _capture.RecordingStopped += (s, e) =>
                    {
                        if (e.Exception != null)
                            MainWindow.Log($"[WasapiAudio] Capture stopped with error: {e.Exception.Message}");
                    };

                    _capture.StartRecording();
                    _isCapturing = true;
                    MainWindow.Log("[WasapiAudio] ✓ Capture started (WASAPI Communications mode)");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WasapiAudio] Error starting capture: {ex.Message}");
                    OnAudioSourceError?.Invoke(ex.Message);
                }
            }

            return Task.CompletedTask;
        }

        public Task PauseAudio()
        {
            _isPaused = true;
            MainWindow.Log("[WasapiAudio] Capture paused (PauseAudio)");
            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            _isPaused = false;
            MainWindow.Log("[WasapiAudio] Capture resumed (ResumeAudio)");
            return Task.CompletedTask;
        }

        public Task CloseAudio()
        {
            _isClosed = true;
            _isCapturing = false;

            try
            {
                if (_capture != null)
                {
                    _capture.StopRecording();
                    _capture.Dispose();
                    _capture = null;
                }

                _resampler = null;
                _resamplerInput = null;
                _resampler48k = null;
                _resamplerInput48k = null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WasapiAudio] Error closing capture: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void OnCaptureData(object? sender, WaveInEventArgs e)
        {
            if (_isClosed || _sendFormat == null || !HasEncodedAudioSubscribers())
                return;

            try
            {
                if (_isPaused)
                {
                    // Keep sending RTP (silence) while muted to avoid NAT pinhole expiry
                    // and to ensure the far-end continues to send audio.
                    int muteBytesPerSample = _captureNativeBitsPerSample / 8;
                    int muteBytesPerFrame = muteBytesPerSample * Math.Max(1, _captureNativeChannels);
                    int muteTotalFrames = muteBytesPerFrame > 0 ? e.BytesRecorded / muteBytesPerFrame : 0;
                    if (muteTotalFrames > 0)
                    {
                        int silenceSamples = (int)(muteTotalFrames * (CODEC_RATE / (double)Math.Max(1, _captureNativeRate)));
                        AppendAndSendSilence(silenceSamples);
                    }
                    return;
                }

                // Step 1: Convert captured audio to mono float samples
                int bytesPerSample = _captureNativeBitsPerSample / 8;
                int bytesPerFrame = bytesPerSample * _captureNativeChannels;
                int totalFrames = e.BytesRecorded / bytesPerFrame;
                if (totalFrames == 0) return;

                float[] monoFloat = new float[totalFrames];

                if (_captureIsFloat && _captureNativeBitsPerSample == 32)
                {
                    // 32-bit IEEE float (most common WASAPI format)
                    for (int i = 0; i < totalFrames; i++)
                    {
                        float sum = 0;
                        for (int ch = 0; ch < _captureNativeChannels; ch++)
                            sum += BitConverter.ToSingle(e.Buffer, (i * _captureNativeChannels + ch) * 4);
                        monoFloat[i] = sum / _captureNativeChannels;
                    }
                }
                else if (_captureNativeBitsPerSample == 16)
                {
                    // 16-bit PCM
                    for (int i = 0; i < totalFrames; i++)
                    {
                        float sum = 0;
                        for (int ch = 0; ch < _captureNativeChannels; ch++)
                            sum += BitConverter.ToInt16(e.Buffer, (i * _captureNativeChannels + ch) * 2) / 32768f;
                        monoFloat[i] = sum / _captureNativeChannels;
                    }
                }
                else if (_captureIsFloat && _captureNativeBitsPerSample == 64)
                {
                    // 64-bit float (rare but possible)
                    for (int i = 0; i < totalFrames; i++)
                    {
                        double sum = 0;
                        for (int ch = 0; ch < _captureNativeChannels; ch++)
                            sum += BitConverter.ToDouble(e.Buffer, (i * _captureNativeChannels + ch) * 8);
                        monoFloat[i] = (float)(sum / _captureNativeChannels);
                    }
                }
                else
                {
                    return; // Unsupported format
                }

                // Convert monoFloat to bytes once — shared by both resamplers
                byte[] monoFloatBytes = new byte[monoFloat.Length * 4];
                Buffer.BlockCopy(monoFloat, 0, monoFloatBytes, 0, monoFloatBytes.Length);

                // ── Recording tap: resample to 48 kHz (full voice bandwidth) ──
                var rawTap = OnRawPcmFrameTap;
                if (rawTap != null)
                {
                    short[] samples48k;
                    if (_resampler48k != null && _resamplerInput48k != null)
                    {
                        _resamplerInput48k.AddSamples(monoFloatBytes, 0, monoFloatBytes.Length);
                        int max48 = (int)(totalFrames * RECORDING_RATE / (double)_captureNativeRate) + 960;
                        float[] buf48 = new float[max48];
                        int n48 = _resampler48k.Read(buf48, 0, max48);
                        samples48k = new short[n48];
                        for (int i = 0; i < n48; i++)
                            samples48k[i] = (short)(Math.Clamp(buf48[i], -1f, 1f) * 32767);
                    }
                    else if (_captureNativeRate == RECORDING_RATE)
                    {
                        samples48k = new short[monoFloat.Length];
                        for (int i = 0; i < monoFloat.Length; i++)
                            samples48k[i] = (short)(Math.Clamp(monoFloat[i], -1f, 1f) * 32767);
                    }
                    else
                    {
                        samples48k = Array.Empty<short>();
                    }
                    if (samples48k.Length > 0)
                        rawTap(samples48k);
                }

                // ── Codec path: resample to 8 kHz, encode, send via RTP ──
                short[] resampled;

                if (_resampler != null && _resamplerInput != null)
                {
                    _resamplerInput.AddSamples(monoFloatBytes, 0, monoFloatBytes.Length);
                    int maxOutputSamples = (int)(totalFrames * CODEC_RATE / (double)_captureNativeRate) + FRAME_SAMPLES;
                    float[] resampledFloat = new float[maxOutputSamples];
                    int samplesRead = _resampler.Read(resampledFloat, 0, maxOutputSamples);

                    resampled = new short[samplesRead];
                    for (int i = 0; i < samplesRead; i++)
                        resampled[i] = (short)(Math.Clamp(resampledFloat[i], -1f, 1f) * 32767);
                }
                else
                {
                    resampled = new short[monoFloat.Length];
                    for (int i = 0; i < monoFloat.Length; i++)
                        resampled[i] = (short)(Math.Clamp(monoFloat[i], -1f, 1f) * 32767);
                }

                lock (_captureLock)
                {
                    int srcOff = 0;
                    while (srcOff < resampled.Length)
                    {
                        int toCopy = Math.Min(resampled.Length - srcOff, FRAME_SAMPLES - _captureFramePos);
                        Array.Copy(resampled, srcOff, _captureFrame, _captureFramePos, toCopy);
                        _captureFramePos += toCopy;
                        srcOff += toCopy;

                        if (_captureFramePos >= FRAME_SAMPLES)
                        {
                            if (_echoCanceller != null)
                            {
                                // Measure RMS before/after AEC to verify it actually suppresses echo.
                                // This is computed every N frames only to keep CPU low.
                                if ((Interlocked.Increment(ref _aecDebugFrames) % 25) == 0)
                                {
                                    float rmsBefore = ComputeRms(_captureFrame);
                                    _echoCanceller.ProcessCapture(_captureFrame, FRAME_SAMPLES);
                                    float rmsAfter = ComputeRms(_captureFrame);

                                    long nowTicks = DateTime.UtcNow.Ticks;
                                    if (_aecDebugLastLogTicks == 0 || (nowTicks - _aecDebugLastLogTicks) > TimeSpan.TicksPerSecond)
                                    {
                                        _aecDebugLastLogTicks = nowTicks;
                                        MainWindow.Log($"[AEC-RMS] before={rmsBefore:F4}, after={rmsAfter:F4}, delta={(rmsBefore - rmsAfter):F4}");
                                    }
                                }
                                else
                                {
                                    _echoCanceller.ProcessCapture(_captureFrame, FRAME_SAMPLES);
                                }
                            }
                            _aecMetrics.OnCaptureProcessed(FRAME_SAMPLES);

                            var encoded = _audioEncoder.EncodeAudio(_captureFrame, _sendFormat.Value);
                            if (encoded != null && encoded.Length > 0)
                            {
                                NotifyEncodedFrameSent(encoded);

                                int count = Interlocked.Increment(ref _captureFramesSent);
                                if (count == 1)
                                    MainWindow.Log($"[WasapiAudio] ✓ First encoded frame sent ({encoded.Length} bytes, {_sendFormat.Value.FormatName})");
                                else if (count == 50)
                                    MainWindow.Log($"[WasapiAudio] Capture running: {count} frames sent (1 second of audio)");
                            }
                            _captureFramePos = 0;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (_captureFramesSent < 5)
                    MainWindow.Log($"[WasapiAudio] Capture processing error: {ex.Message}");
            }
        }

        private void AppendAndSendSilence(int samples)
        {
            if (samples <= 0 || _sendFormat == null) return;

            // Feed zeros into the same framing pipeline (20ms @ 8kHz).
            lock (_captureLock)
            {
                int remaining = samples;
                while (remaining > 0)
                {
                    int toCopy = Math.Min(remaining, FRAME_SAMPLES - _captureFramePos);
                    Array.Clear(_captureFrame, _captureFramePos, toCopy);
                    _captureFramePos += toCopy;
                    remaining -= toCopy;

                    if (_captureFramePos >= FRAME_SAMPLES)
                    {
                        var encoded = _audioEncoder.EncodeAudio(_captureFrame, _sendFormat.Value);
                        if (encoded != null && encoded.Length > 0)
                        {
                            NotifyEncodedFrameSent(encoded);

                            int count = Interlocked.Increment(ref _captureFramesSent);
                            if (count == 1)
                                MainWindow.Log($"[WasapiAudio] ✓ First encoded frame sent ({encoded.Length} bytes, {_sendFormat.Value.FormatName})");
                            else if (count == 50)
                                MainWindow.Log($"[WasapiAudio] Capture running: {count} frames sent (1 second of audio)");
                        }
                        _captureFramePos = 0;
                    }
                }
            }
        }

        private static float ComputeRms(short[] samples)
        {
            // RMS in normalized units (0..~1). Expected for 20ms / 160 samples at 8kHz.
            double sumSq = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                double v = samples[i] / 32768.0;
                sumSq += v * v;
            }
            return (float)Math.Sqrt(sumSq / Math.Max(1, samples.Length));
        }

        #endregion

        #region IAudioSink

        public List<AudioFormat> GetAudioSinkFormats() => _supportedFormats;

        public void SetAudioSinkFormat(AudioFormat format)
        {
            _recvFormat = format;
            MainWindow.Log($"[WasapiAudio] Recv format set: {format.FormatName} PT={format.FormatID}");
        }

        public Task StartAudioSink()
        {
            lock (_startLock)
            {
                if (_isPlaying || _isClosed) return Task.CompletedTask;

                try
                {
                    // Create playback buffer at codec rate (8000 Hz, 16-bit, mono).
                    // Keep buffer short to minimise echo delay (large buffers push
                    // the acoustic round-trip beyond what AEC can handle).
                    // Keep playback buffer small enough to avoid overflow discards,
                    // but still large enough to prevent starvation.
                    // If discards happen, AEC reference becomes discontinuous and
                    // users hear "themselves".
                    _playbackBuffer = new BufferedWaveProvider(new WaveFormat(CODEC_RATE, 16, 1))
                    {
                        // Slightly lower buffer duration reduces overrun risk and keeps AEC reference aligned.
                        BufferDuration = TimeSpan.FromMilliseconds(160),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };

                    _aecTap = new AecTapWaveProvider(_playbackBuffer, _echoCanceller, sampleCount =>
                    {
                        _aecMetrics.OnRenderPlayed(sampleCount, _playbackBuffer?.BufferedBytes ?? 0);
                    });

                    // Try WASAPI playback first
                    try
                    {
                        var device = GetRenderDevice();
                        // Lower latency reduces acoustic round-trip delay and helps AEC stay stable under jitter.
                        _playback = new WasapiOut(device, AudioClientShareMode.Shared, true, 10);
                        _playback.Init(_aecTap);
                        MainWindow.Log($"[WasapiAudio] Playback device (WASAPI): {device.FriendlyName}");
                    }
                    catch (Exception wasapiEx)
                    {
                        // Fall back to WaveOut (WinMM) for playback - always works
                        MainWindow.Log($"[WasapiAudio] WASAPI playback failed ({wasapiEx.Message}), using WaveOut");
                        int deviceNum = _audioOutDeviceIndex >= 0 ? _audioOutDeviceIndex : -1;
                        var waveOut = new WaveOutEvent { DeviceNumber = deviceNum };
                        waveOut.Init(_aecTap);
                        _playback = waveOut;
                        MainWindow.Log($"[WasapiAudio] Playback device (WaveOut): device #{deviceNum}");
                    }

                    _playback.Play();
                    _isPlaying = true;
                    MainWindow.Log("[WasapiAudio] ✓ Playback started");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WasapiAudio] Error starting playback: {ex.Message}");
                    OnAudioSinkError?.Invoke(ex.Message);
                }
            }

            return Task.CompletedTask;
        }

        public Task PauseAudioSink()
        {
            try
            {
                _playback?.Pause();
                MainWindow.Log("[WasapiAudio] Playback paused (PauseAudioSink)");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WasapiAudio] Playback pause failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task ResumeAudioSink()
        {
            try
            {
                _playback?.Play();
                MainWindow.Log("[WasapiAudio] Playback resumed (ResumeAudioSink)");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WasapiAudio] Playback resume failed: {ex.Message}");
            }
            return Task.CompletedTask;
        }

        public Task CloseAudioSink()
        {
            _isPlaying = false;

            try
            {
                _playback?.Stop();
                _playback?.Dispose();
                _playback = null;
                _playbackBuffer = null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WasapiAudio] Error closing playback: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[]? payload)
        {
            if (_isClosed || _playbackBuffer == null || payload == null || payload.Length == 0)
                return;

            AudioFormat format;
            if (_recvFormat != null && _recvFormat.Value.FormatID == payloadID)
            {
                format = _recvFormat.Value;
            }
            else
            {
                var matchingFormat = _supportedFormats.FirstOrDefault(f => f.FormatID == payloadID);
                if (matchingFormat.FormatID != payloadID)
                    return;
                format = matchingFormat;
            }

            TryPlayEncodedPayload(payload, format, payloadID);
        }

        public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
        {
            if (_isClosed || _playbackBuffer == null)
                return;

            var payload = encodedMediaFrame.EncodedAudio;
            if (payload == null || payload.Length == 0)
                return;

            var format = encodedMediaFrame.AudioFormat;
            TryPlayEncodedPayload(payload, format, format.FormatID);
        }

        private void TryPlayEncodedPayload(byte[] payload, AudioFormat format, int payloadID)
        {
            // Filter non-audio payload types (DTMF, Comfort Noise)
            if (payloadID == 101 || payloadID == 13 || payloadID >= 96)
            {
                if (_recvFormat == null || payloadID != _recvFormat.Value.FormatID)
                    return;
            }

            try
            {
                var pcm = _audioEncoder.DecodeAudio(payload, format);
                if (pcm == null || pcm.Length == 0) return;

                // Optional noise gate: when remote side is silent, suppress low-level noise.
                if (_playbackNoiseGateEnabled)
                {
                    float rms = ComputeRms(pcm);
                    if (rms < _playbackNoiseGateThresholdRms)
                        _playbackNoiseGateBelowCount++;
                    else
                        _playbackNoiseGateBelowCount = 0;

                    // Gate closes only after hangover (prevents chattering).
                    float targetGain = (_playbackNoiseGateBelowCount >= _playbackNoiseGateHangoverFrames) ? 0.0f : 1.0f;

                    // Smooth gain per-sample to avoid clicks.
                    // For a 20ms frame at 8kHz (160 samples), this is a short ramp.
                    float startGain = _playbackNoiseGateGain;
                    float endGain = targetGain;
                    if (startGain != endGain)
                    {
                        for (int i = 0; i < pcm.Length; i++)
                        {
                            float t = (pcm.Length <= 1) ? 1f : (i / (float)(pcm.Length - 1));
                            float g = startGain + (endGain - startGain) * t;
                            pcm[i] = (short)Math.Clamp((int)MathF.Round(pcm[i] * g), short.MinValue, short.MaxValue);
                        }
                    }
                    else if (endGain == 0.0f)
                    {
                        Array.Clear(pcm, 0, pcm.Length);
                    }

                    _playbackNoiseGateGain = endGain;
                }

                int count = Interlocked.Increment(ref _playbackFramesDecoded);
                if (count == 1)
                    MainWindow.Log($"[WasapiAudio] ✓ First decoded frame: {pcm.Length} samples, PT={payloadID} ({format.FormatName})");
                else if (count == 50)
                    MainWindow.Log($"[WasapiAudio] Playback running: {count} frames decoded (1 second of audio)");

                // Convert short[] to byte[] for BufferedWaveProvider
                byte[] pcmBytes = new byte[pcm.Length * 2];
                Buffer.BlockCopy(pcm, 0, pcmBytes, 0, pcmBytes.Length);

                int beforeBuffered = _playbackBuffer.BufferedBytes;
                _playbackBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);
                _aecMetrics.OnRenderQueued(
                    pcm.Length,
                    beforeBuffered,
                    _playbackBuffer.BufferedBytes,
                    _playbackBuffer.BufferLength);
            }
            catch (Exception ex)
            {
                if (_playbackFramesDecoded < 5)
                    MainWindow.Log($"[WasapiAudio] Decode/playback error: {ex.Message}");
            }
        }

        private void NotifyEncodedFrameSent(byte[] encoded)
        {
            if (_sendFormat == null)
                return;

            OnAudioSourceEncodedSample?.Invoke((uint)FRAME_SAMPLES, encoded);
            OnAudioSourceEncodedFrameReady?.Invoke(
                new EncodedAudioFrame(0, _sendFormat.Value, (uint)FRAME_MS, encoded));
        }


        #endregion

        #region Device Selection

        private MMDevice GetCaptureDevice()
        {
            var enumerator = new MMDeviceEnumerator();

            // First try to get the Communications default device (has AEC/NS/AGC)
            try
            {
                var commDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                MainWindow.Log($"[WasapiAudio] Communications capture device: {commDevice.FriendlyName}");
                return commDevice;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WasapiAudio] No communications capture device: {ex.Message}");
            }

            // Fall back to specific device by index
            if (_audioInDeviceIndex >= 0)
            {
                try
                {
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    if (_audioInDeviceIndex < devices.Count)
                    {
                        var device = devices[_audioInDeviceIndex];
                        MainWindow.Log($"[WasapiAudio] Using capture device [{_audioInDeviceIndex}]: {device.FriendlyName}");

                        // Best-effort: ensure this device is treated as Role.Communications,
                        // otherwise Windows AEC/NS/AGC may not engage and echo can appear.
                        try
                        {
                            EchoCancellationHelper.TrySetDeviceAsDefaultForCommunications(
                                device,
                                label: "[WasapiAudio] (capture) ");
                        }
                        catch { }
                        return device;
                    }
                }
                catch { }
            }

            // Last resort: multimedia default
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }

        private MMDevice GetRenderDevice()
        {
            var enumerator = new MMDeviceEnumerator();

            // First try Communications default
            try
            {
                var commDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                MainWindow.Log($"[WasapiAudio] Communications render device: {commDevice.FriendlyName}");
                return commDevice;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WasapiAudio] No communications render device: {ex.Message}");
            }

            // Fall back to specific device by index
            if (_audioOutDeviceIndex >= 0)
            {
                try
                {
                    var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    if (_audioOutDeviceIndex < devices.Count)
                    {
                        var device = devices[_audioOutDeviceIndex];
                        MainWindow.Log($"[WasapiAudio] Using render device [{_audioOutDeviceIndex}]: {device.FriendlyName}");

                        // Best-effort: ensure this device is treated as Role.Communications,
                        // otherwise Windows AEC/NS/AGC may not engage and echo can appear.
                        try
                        {
                            EchoCancellationHelper.TrySetDeviceAsDefaultForCommunications(
                                device,
                                label: "[WasapiAudio] (render) ");
                        }
                        catch { }
                        return device;
                    }
                }
                catch { }
            }

            // Last resort: multimedia default
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        #endregion

        public void Dispose()
        {
            if (!_isClosed)
            {
                _isClosed = true;
                OnRawPcmFrameTap = null;
                try { CloseAudio().Wait(); } catch { }
                try { CloseAudioSink().Wait(); } catch { }

                if (_echoCanceller != null)
                {
                    try { _echoCanceller.Dispose(); } catch { }
                    _echoCanceller = null;
                }
            }
        }
    }

    /// <summary>
    /// Wraps a BufferedWaveProvider and feeds every chunk that WasapiOut
    /// actually plays to the native AEC as the far-end reference signal.
    /// This ensures perfect timing alignment between what the speaker outputs
    /// and what the AEC uses for echo subtraction.
    /// </summary>
    internal sealed class AecTapWaveProvider : IWaveProvider
    {
        // Must be EXACTly 20ms at 8kHz (codec rate for native AEC wrapper).
        // ProcessRender/ProcessCapture expect aligned 160-sample frames.
        private const int FrameSamples = 160;

        private readonly BufferedWaveProvider _source;
        private readonly Softphone.Audio.IEchoCanceller? _aec;
        private readonly Action<int>? _onRenderPlayedSamples;
        private short[] _renderScratch = Array.Empty<short>();

        // Accumulate far-end reference samples into 20ms frames for AEC.
        private readonly short[] _renderFrame = new short[FrameSamples];
        private int _renderFramePos;

        // Render-side debug: check reference signal chunk sizes and that
        // audio passed into AEC is not wildly fragmenting.
        private long _renderDebugCalls;
        private long _renderDebugLastLogTicks;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public AecTapWaveProvider(BufferedWaveProvider source, Softphone.Audio.IEchoCanceller? aec, Action<int>? onRenderPlayedSamples = null)
        {
            _source = source;
            _aec = aec;
            _onRenderPlayedSamples = onRenderPlayedSamples;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);

            if (_aec != null && read > 0)
            {
                int sampleCount = read / 2;
                if (_renderScratch.Length < sampleCount)
                    _renderScratch = new short[Math.Max(sampleCount, 4096)];
                Buffer.BlockCopy(buffer, offset, _renderScratch, 0, read);

                for (int i = 0; i < sampleCount; i++)
                {
                    _renderFrame[_renderFramePos++] = _renderScratch[i];

                    if (_renderFramePos == FrameSamples)
                    {
                        _aec.ProcessRender(_renderFrame, FrameSamples);
                        _onRenderPlayedSamples?.Invoke(FrameSamples);

                        // Log every N frames only.
                        if ((Interlocked.Increment(ref _renderDebugCalls) % 25) == 0)
                        {
                            double sumSq = 0;
                            for (int j = 0; j < FrameSamples; j++)
                            {
                                double v = _renderFrame[j] / 32768.0;
                                sumSq += v * v;
                            }
                            float rmsRef = (float)Math.Sqrt(sumSq / FrameSamples);

                            long nowTicks = DateTime.UtcNow.Ticks;
                            if (_renderDebugLastLogTicks == 0 || (nowTicks - _renderDebugLastLogTicks) > TimeSpan.TicksPerSecond)
                            {
                                _renderDebugLastLogTicks = nowTicks;
                                MainWindow.Log($"[AEC-RENDER] samples={FrameSamples} rmsRef={rmsRef:F4} (frame160=yes)");
                            }
                        }

                        _renderFramePos = 0;
                    }
                }
            }

            return read;
        }
    }

    internal sealed class AecMetrics
    {
        private const int SampleRate = 8000;
        private const int LogPeriodMs = 1000;
        private const int FrameSamples = SampleRate / 50; // 20 ms

        private long _captureFrames;
        private long _renderFramesQueued;
        private long _renderFramesPlayed;
        private long _overrunCount;
        private long _starvationCount;
        private int _lastBufferedBytes;
        private int _maxBufferedBytes;
        private long _lastRenderPlayedAtTicks;
        private long _lastLogAtTicks;

        public void OnRenderQueued(int samples, int beforeBufferedBytes, int afterBufferedBytes, int bufferCapacityBytes)
        {
            Interlocked.Add(ref _renderFramesQueued, Math.Max(1, samples / FrameSamples));
            Interlocked.Exchange(ref _lastBufferedBytes, afterBufferedBytes);
            UpdateMaxBuffered(afterBufferedBytes);

            // Overrun indicator: queue was already near capacity before enqueuing.
            // Use capacity-based threshold to avoid misleading numbers when buffer duration changes.
            if (bufferCapacityBytes > 0 && beforeBufferedBytes >= (int)(bufferCapacityBytes * 0.85))
                Interlocked.Increment(ref _overrunCount);

            TryLog();
        }

        public void OnRenderPlayed(int samples, int currentBufferedBytes)
        {
            Interlocked.Add(ref _renderFramesPlayed, Math.Max(1, samples / FrameSamples));
            Interlocked.Exchange(ref _lastBufferedBytes, currentBufferedBytes);
            UpdateMaxBuffered(currentBufferedBytes);
            Interlocked.Exchange(ref _lastRenderPlayedAtTicks, DateTime.UtcNow.Ticks);
            TryLog();
        }

        public void OnCaptureProcessed(int samples)
        {
            Interlocked.Add(ref _captureFrames, Math.Max(1, samples / FrameSamples));

            long lastRenderTicks = Interlocked.Read(ref _lastRenderPlayedAtTicks);
            if (lastRenderTicks > 0)
            {
                long msSinceRender = (DateTime.UtcNow.Ticks - lastRenderTicks) / TimeSpan.TicksPerMillisecond;
                if (msSinceRender > 80)
                    Interlocked.Increment(ref _starvationCount);
            }

            TryLog();
        }

        private void UpdateMaxBuffered(int value)
        {
            int currentMax;
            do
            {
                currentMax = Interlocked.CompareExchange(ref _maxBufferedBytes, 0, 0);
                if (value <= currentMax) return;
            } while (Interlocked.CompareExchange(ref _maxBufferedBytes, value, currentMax) != currentMax);
        }

        private void TryLog()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref _lastLogAtTicks);
            if ((now - last) / TimeSpan.TicksPerMillisecond < LogPeriodMs) return;
            if (Interlocked.CompareExchange(ref _lastLogAtTicks, now, last) != last) return;

            long capture = Interlocked.Read(ref _captureFrames);
            long queued = Interlocked.Read(ref _renderFramesQueued);
            long played = Interlocked.Read(ref _renderFramesPlayed);
            long starvation = Interlocked.Read(ref _starvationCount);
            long overrun = Interlocked.Read(ref _overrunCount);
            int buffered = Interlocked.CompareExchange(ref _lastBufferedBytes, 0, 0);
            int maxBuffered = Interlocked.Exchange(ref _maxBufferedBytes, buffered);

            MainWindow.Log($"[AEC-Metrics] cap={capture}, renQ={queued}, renP={played}, " +
                           $"starve={starvation}, overrun={overrun}, buf={buffered}B, bufMax={maxBuffered}B");
        }
    }
}

#endif // WINDOWS
