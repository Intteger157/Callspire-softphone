using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using PortAudioSharp;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>
    /// PortAudio speaker output for SIPSorcery (<see cref="IAudioSink"/>), macOS / Linux / Windows.
    /// Built on PortAudioSharp2: an output stream in callback mode pulls 16-bit mono PCM from an
    /// internal ring buffer that <see cref="GotAudioSample"/> fills. Incoming PCM at a different
    /// rate (e.g. 8 kHz G.711 into a 16 kHz stream) is resampled linearly.
    /// </summary>
    public class PortAudioSink : IAudioSink, IDisposable
    {
        private readonly int _deviceIndex;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly AudioEncoder? _audioEncoder;

        private readonly object _lock = new();
        private readonly PcmRingBuffer _ring;
        private IDisposable? _runtime;
        private PortAudioSharp.Stream? _stream;
        private PortAudioSharp.Stream.Callback? _callback; // keep delegate alive
        private int _streamChannels = 1;
        private bool _isStarted;
        private bool _isPaused;
        private bool _isDisposed;

        private AudioFormat? _currentFormat;
        private readonly List<AudioFormat> _supportedFormats = new();

        public event SourceErrorDelegate? OnAudioSinkError;

        /// <summary>Optional AEC: playback frames are fed as the far-end reference signal.</summary>
        public IEchoCanceller? EchoCanceller { get; set; }

        public int SampleRate => _sampleRate;

        /// <summary>Milliseconds of audio currently buffered and not yet played.</summary>
        public int QueuedMilliseconds => (int)(_ring.Count * 1000L / Math.Max(1, _sampleRate));

        public PortAudioSink(int deviceIndex = -1, int sampleRate = 16000, int channels = 1, AudioEncoder? audioEncoder = null)
        {
            _deviceIndex = deviceIndex;
            _sampleRate = sampleRate <= 0 ? 16000 : sampleRate;
            _channels = Math.Max(1, channels);
            _audioEncoder = audioEncoder;
            // ~1 s of headroom; jitter target is ~60-100 ms.
            _ring = new PcmRingBuffer(_sampleRate);
            InitializeSupportedFormats();
        }

        private void InitializeSupportedFormats()
        {
            try
            {
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMA, _sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.G722, _sampleRate, _channels));
            }
            catch
            {
                _supportedFormats.Clear();
                try { _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels)); } catch { }
            }
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            lock (_lock) _supportedFormats.RemoveAll(f => !filter(f));
        }

        public void SetAudioSinkFormat(AudioFormat audioFormat)
        {
            lock (_lock)
            {
                try
                {
                    if (audioFormat.FormatID > 127)
                    {
                        _currentFormat = new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels);
                        return;
                    }
                }
                catch { }
                _currentFormat = audioFormat;
            }
        }

        public Task StartAudioSink()
        {
            if (_isDisposed) throw new ObjectDisposedException(nameof(PortAudioSink));

            lock (_lock)
            {
                if (_isStarted) return Task.CompletedTask;
                try
                {
                    _runtime ??= PortAudioRuntime.Acquire();

                    var outParams = PortAudioRuntime.MakeParams(_deviceIndex, input: false, _channels, out int device);
                    if (outParams == null)
                    {
                        OnAudioSinkError?.Invoke("No output device available");
                        return Task.CompletedTask;
                    }
                    _streamChannels = outParams.Value.channelCount;

                    _callback = OutputCallback;
                    uint framesPerBuffer = (uint)(_sampleRate / 50); // 20 ms
                    _stream = new PortAudioSharp.Stream(null, outParams, _sampleRate, framesPerBuffer, StreamFlags.ClipOff, _callback, IntPtr.Zero);
                    _stream.Start();

                    _isStarted = true;
                    _isPaused = false;
                    AppLog.Log($"[PortAudioSink] started (device={device}, rate={_sampleRate}, ch={_streamChannels})");
                }
                catch (Exception ex)
                {
                    OnAudioSinkError?.Invoke($"Error starting audio sink: {ex.Message}");
                    AppLog.Log($"[PortAudioSink] start failed: {ex}");
                    try { _stream?.Dispose(); } catch { }
                    _stream = null;
                }
            }
            return Task.CompletedTask;
        }

        private StreamCallbackResult OutputCallback(IntPtr input, IntPtr output, uint frameCount, ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
        {
            if (output == IntPtr.Zero) return StreamCallbackResult.Continue;
            int frames = (int)frameCount;
            try
            {
                unsafe
                {
                    short* dst = (short*)output;
                    if (_isPaused)
                    {
                        new Span<short>(dst, frames * _streamChannels).Clear();
                        return StreamCallbackResult.Continue;
                    }

                    if (_streamChannels == 1)
                    {
                        int got = _ring.Read(new Span<short>(dst, frames));
                        if (got < frames) new Span<short>(dst + got, frames - got).Clear();
                    }
                    else
                    {
                        Span<short> mono = frames <= 2048 ? stackalloc short[frames] : new short[frames];
                        int got = _ring.Read(mono);
                        if (got < frames) mono.Slice(got).Clear();
                        for (int i = 0; i < frames; i++)
                            for (int c = 0; c < _streamChannels; c++)
                                dst[i * _streamChannels + c] = mono[i];
                    }
                }
            }
            catch
            {
                // never throw across the native boundary
            }
            return StreamCallbackResult.Continue;
        }

        public Task PauseAudioSink() { lock (_lock) { _isPaused = true; _ring.Clear(); } return Task.CompletedTask; }
        public Task ResumeAudioSink() { lock (_lock) _isPaused = false; return Task.CompletedTask; }

        public Task CloseAudioSink()
        {
            lock (_lock)
            {
                if (!_isStarted && _stream == null) return Task.CompletedTask;
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
                    _isStarted = false;
                    _isPaused = false;
                    _ring.Clear();
                    try { _runtime?.Dispose(); } catch { }
                    _runtime = null;
                }
            }
            return Task.CompletedTask;
        }

        public List<AudioFormat> GetAudioSinkFormats()
        {
            lock (_lock)
                return _supportedFormats.Where(f => { try { return f.FormatID <= 127; } catch { return false; } }).ToList();
        }

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
        {
            // Legacy RTP path; SIPSorcery 10+ prefers GotEncodedMediaFrame.
        }

        public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
        {
            if (_isPaused || _isDisposed || !_isStarted || _audioEncoder == null) return;
            var payload = encodedMediaFrame.EncodedAudio;
            if (payload == null || payload.Length == 0) return;
            try
            {
                var pcm = _audioEncoder.DecodeAudio(payload, encodedMediaFrame.AudioFormat);
                if (pcm != null && pcm.Length > 0)
                {
                    var rate = encodedMediaFrame.AudioFormat.ClockRate == 16000 ? AudioSamplingRatesEnum.Rate16KHz : AudioSamplingRatesEnum.Rate8KHz;
                    GotAudioSample(rate, encodedMediaFrame.DurationMilliSeconds, pcm);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error decoding encoded audio frame: {ex.Message}");
            }
        }

        public void GotAudioSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            if (_isPaused || _isDisposed || !_isStarted || sample == null || sample.Length == 0) return;

            // AEC: speaker output is the far-end reference for the echo canceller.
            try { EchoCanceller?.ProcessRender(sample, sample.Length); } catch { }

            int inRate = samplingRate == AudioSamplingRatesEnum.Rate16KHz ? 16000 : 8000;
            if (inRate == _sampleRate)
            {
                _ring.Write(sample);
                return;
            }

            _ring.Write(Resample(sample, inRate, _sampleRate));
        }

        internal static short[] Resample(short[] src, int inRate, int outRate)
        {
            if (inRate == outRate || src.Length == 0) return src;
            int outLen = (int)((long)src.Length * outRate / inRate);
            var dst = new short[outLen];
            double step = (double)inRate / outRate;
            for (int i = 0; i < outLen; i++)
            {
                double pos = i * step;
                int i0 = (int)pos;
                int i1 = Math.Min(i0 + 1, src.Length - 1);
                double frac = pos - i0;
                dst[i] = (short)(src[i0] + (src[i1] - src[i0]) * frac);
            }
            return dst;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            try { CloseAudioSink(); } catch { }
        }
    }

    /// <summary>Lock-free-ish single-producer/single-consumer ring buffer of 16-bit samples.</summary>
    internal sealed class PcmRingBuffer
    {
        private readonly short[] _buf;
        private readonly object _gate = new();
        private int _head; // read position
        private int _tail; // write position
        private int _count;

        public PcmRingBuffer(int capacity) { _buf = new short[Math.Max(1024, capacity)]; }

        public int Count { get { lock (_gate) return _count; } }

        public void Clear() { lock (_gate) { _head = _tail = _count = 0; } }

        public void Write(ReadOnlySpan<short> data)
        {
            lock (_gate)
            {
                // Drop oldest audio if the consumer stalled (keeps latency bounded).
                int overflow = _count + data.Length - _buf.Length;
                if (overflow > 0)
                {
                    _head = (_head + overflow) % _buf.Length;
                    _count -= overflow;
                }
                foreach (var s in data)
                {
                    _buf[_tail] = s;
                    _tail = (_tail + 1) % _buf.Length;
                }
                _count += data.Length;
            }
        }

        public int Read(Span<short> dst)
        {
            lock (_gate)
            {
                int n = Math.Min(dst.Length, _count);
                for (int i = 0; i < n; i++)
                {
                    dst[i] = _buf[_head];
                    _head = (_head + 1) % _buf.Length;
                }
                _count -= n;
                return n;
            }
        }
    }
}
