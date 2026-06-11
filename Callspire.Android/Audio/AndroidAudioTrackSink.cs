using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Android.Media;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;
using SipAudioFormat = SIPSorceryMedia.Abstractions.AudioFormat;

namespace Softphone.Android.Audio
{
    /// <summary>
    /// Speaker playback via Android <see cref="AudioTrack"/> — implements SIPSorcery <see cref="IAudioSink"/>.
    /// </summary>
    public sealed class AndroidAudioTrackSink : IAudioSink, IDisposable
    {
        private readonly int _sampleRate;
        private readonly int _channels;

        private AudioTrack? _track;
        private bool _isStarted;
        private bool _isPaused;
        private bool _isDisposed;

        private SipAudioFormat? _currentFormat;
        private readonly List<SipAudioFormat> _supportedFormats = new();

        public event SourceErrorDelegate? OnAudioSinkError;

        public IEchoCanceller? EchoCanceller { get; set; }

        public AndroidAudioTrackSink(int sampleRate = 8000, int channels = 1)
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

        public void SetAudioSinkFormat(SipAudioFormat audioFormat)
        {
            _currentFormat = audioFormat;
        }

        public List<SipAudioFormat> GetAudioSinkFormats() => _supportedFormats.ToList();

        public Task StartAudioSink()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(AndroidAudioTrackSink));

            if (_isStarted)
                return Task.CompletedTask;

            try
            {
                var channelConfig = ChannelOut.Mono;
                var encoding      = Encoding.Pcm16bit;
                var minBuffer     = AudioTrack.GetMinBufferSize(_sampleRate, channelConfig, encoding);
                if (minBuffer <= 0)
                {
                    OnAudioSinkError?.Invoke($"AudioTrack.GetMinBufferSize failed: {minBuffer}");
                    return Task.CompletedTask;
                }

                _track = new AudioTrack(
                    Stream.VoiceCall,
                    _sampleRate,
                    channelConfig,
                    encoding,
                    minBuffer,
                    AudioTrackMode.Stream);

                if (_track.State != AudioTrackState.Initialized)
                {
                    OnAudioSinkError?.Invoke("AudioTrack failed to initialize");
                    _track.Release();
                    _track = null;
                    return Task.CompletedTask;
                }

                _track.Play();
                _isStarted = true;
                _isPaused  = false;
            }
            catch (Exception ex)
            {
                OnAudioSinkError?.Invoke($"StartAudioSink failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task PauseAudioSink()
        {
            _isPaused = true;
            try { _track?.Pause(); } catch { }
            return Task.CompletedTask;
        }

        public Task ResumeAudioSink()
        {
            _isPaused = false;
            try { _track?.Play(); } catch { }
            return Task.CompletedTask;
        }

        public Task CloseAudioSink()
        {
            _isStarted = false;
            try
            {
                if (_track != null)
                {
                    _track.Stop();
                    _track.Release();
                    _track = null;
                }
            }
            catch { }

            return Task.CompletedTask;
        }

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
        {
            // VoIPMediaSession decodes RTP and calls GotAudioSample.
        }

        public void GotAudioSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            if (_isPaused || _isDisposed || !_isStarted || _track == null)
                return;

            try { EchoCanceller?.ProcessRender(sample, sample.Length); } catch { }

            try
            {
                _track.Write(sample, 0, sample.Length);
            }
            catch (Exception ex)
            {
                OnAudioSinkError?.Invoke($"AudioTrack.Write failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { CloseAudioSink().Wait(500); } catch { }
            try { EchoCanceller?.Dispose(); } catch { }
        }
    }
}
