using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Media;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;
using SipAudioFormat = SIPSorceryMedia.Abstractions.AudioFormat;

namespace Softphone.Android.Audio
{
    /// <summary>
    /// Microphone capture via Android <see cref="AudioRecord"/> — implements SIPSorcery <see cref="IAudioSource"/>.
    /// </summary>
    public sealed class AndroidAudioRecordSource : IAudioSource, IDisposable
    {
        private const int FrameMs = 20;

        private readonly int _sampleRate;
        private readonly int _channels;

        private AudioRecord? _record;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _isStarted;
        private bool _isPaused;
        private bool _isDisposed;

        private SipAudioFormat? _currentFormat;
        private readonly List<SipAudioFormat> _supportedFormats = new();

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event SourceErrorDelegate? OnAudioSourceError;

        public IEchoCanceller? EchoCanceller { get; set; }
        public Action<short[]>? OnRawPcmFrameTap { get; set; }

        public AndroidAudioRecordSource(int sampleRate = 8000, int channels = 1)
        {
            _sampleRate = sampleRate == 8000 ? 8000 : sampleRate;
            _channels   = channels;
            InitializeFormats();
        }

        private void InitializeFormats()
        {
            _supportedFormats.Clear();
            _supportedFormats.Add(new SipAudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels));
            _supportedFormats.Add(new SipAudioFormat(AudioCodecsEnum.PCMA, _sampleRate, _channels));
        }

        public void RestrictFormats(Func<SipAudioFormat, bool> filter)
        {
            _supportedFormats.RemoveAll(f => !filter(f));
        }

        public void SetAudioSourceFormat(SipAudioFormat audioFormat)
        {
            _currentFormat = audioFormat;
        }

        public List<SipAudioFormat> GetAudioSourceFormats() => _supportedFormats.ToList();

        public bool HasEncodedAudioSubscribers() => OnAudioSourceEncodedSample != null;

        public bool IsAudioSourcePaused() => _isPaused;

        public Task StartAudio()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(AndroidAudioRecordSource));

            if (_isStarted)
                return Task.CompletedTask;

            try
            {
                var channelConfig = ChannelIn.Mono;
                var encoding      = Encoding.Pcm16bit;
                var minBuffer     = AudioRecord.GetMinBufferSize(_sampleRate, channelConfig, encoding);
                if (minBuffer <= 0)
                {
                    OnAudioSourceError?.Invoke($"AudioRecord.GetMinBufferSize failed: {minBuffer}");
                    return Task.CompletedTask;
                }

                var bufferSize = Math.Max(minBuffer, _sampleRate * 2 * FrameMs / 1000);

                _record = new AudioRecord(
                    AudioSource.VoiceCommunication,
                    _sampleRate,
                    channelConfig,
                    encoding,
                    bufferSize);

                if (_record.State != global::Android.Media.State.Initialized)
                {
                    OnAudioSourceError?.Invoke("AudioRecord failed to initialize");
                    _record.Release();
                    _record = null;
                    return Task.CompletedTask;
                }

                _record.StartRecording();
                _isStarted = true;
                _isPaused  = false;

                _cts         = new CancellationTokenSource();
                _captureTask = Task.Run(() => CaptureLoop(_cts.Token, bufferSize));
            }
            catch (Exception ex)
            {
                OnAudioSourceError?.Invoke($"StartAudio failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private void CaptureLoop(CancellationToken token, int bufferSize)
        {
            var buffer       = new short[bufferSize / 2];
            var samplingRate = ToSamplingRateEnum(_sampleRate);
            var durationMs   = (uint)FrameMs;

            while (!token.IsCancellationRequested && _isStarted && !_isPaused && _record != null)
            {
                try
                {
                    var read = _record.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    var frame = new short[read];
                    Array.Copy(buffer, frame, read);

                    try { EchoCanceller?.ProcessCapture(frame, frame.Length); } catch { }
                    try { OnRawPcmFrameTap?.Invoke(frame); } catch { }

                    OnAudioSourceRawSample?.Invoke(samplingRate, durationMs, frame);
                }
                catch (Exception ex)
                {
                    OnAudioSourceError?.Invoke($"Capture loop error: {ex.Message}");
                    Thread.Sleep(20);
                }
            }
        }

        public Task PauseAudio()
        {
            _isPaused = true;
            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            _isPaused = false;
            return Task.CompletedTask;
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
            => OnAudioSourceRawSample?.Invoke(samplingRate, durationMilliseconds, sample);

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, byte[] sample)
        {
            // Not used for VoIP capture path.
        }

        public Task CloseAudio()
        {
            _isStarted = false;
            try { _cts?.Cancel(); } catch { }

            try
            {
                if (_record != null)
                {
                    try { _record.Stop(); } catch { }
                    _record.Release();
                    _record = null;
                }
            }
            catch { }

            return Task.CompletedTask;
        }

        private static AudioSamplingRatesEnum ToSamplingRateEnum(int rate) =>
            rate switch
            {
                8000  => AudioSamplingRatesEnum.Rate8KHz,
                16000 => AudioSamplingRatesEnum.Rate16KHz,
                _     => AudioSamplingRatesEnum.Rate8KHz
            };

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { CloseAudio().Wait(500); } catch { }
            try { EchoCanceller?.Dispose(); } catch { }
        }
    }
}
