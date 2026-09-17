using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SIPSorceryMedia.Abstractions;

namespace Softphone.Audio
{
    /// <summary>
    /// Cross-platform playback of call recordings for the Call Details window.
    /// Decoding: NAudio managed readers (WAV everywhere; MP3 via ACM on Windows). Non-WAV files on
    /// macOS/Linux are transcoded to a temporary WAV with the bundled ffmpeg first.
    /// Output: WASAPI/winmm on Windows (<c>WaveOutEvent</c>), <see cref="PortAudioSink"/> elsewhere.
    /// </summary>
    public sealed class RecordingPlayer : IDisposable
    {
        private const int OutRate = 16000;
        private const int FrameSamples = OutRate / 50;

        private readonly object _lock = new();
        private WaveStream? _reader;
        private ISampleProvider? _provider;
        private string? _tempWav;
        private Thread? _thread;
        private CancellationTokenSource? _cts;
        private volatile bool _paused;
        private long _positionSamples;
        private long _totalSamples;
        private bool _disposed;

#if WINDOWS
        private WaveOutEvent? _waveOut;
        private AudioFileReader? _winReader;
#endif

        public event Action<TimeSpan, TimeSpan>? PositionChanged;
        public event Action? PlaybackEnded;

        public TimeSpan Duration { get; private set; }
        public TimeSpan Position
        {
            get
            {
#if WINDOWS
                if (_winReader != null) return _winReader.CurrentTime;
#endif
                return TimeSpan.FromSeconds((double)Interlocked.Read(ref _positionSamples) / OutRate);
            }
        }
        public bool IsPlaying => _thread is { IsAlive: true } && !_paused
#if WINDOWS
            || _waveOut?.PlaybackState == PlaybackState.Playing
#endif
            ;
        public bool IsLoaded => _reader != null
#if WINDOWS
            || _winReader != null
#endif
            ;

        /// <summary>Opens the file (transcoding if necessary). Returns an error message or null.</summary>
        public async Task<string?> LoadAsync(string path)
        {
            Stop();
            if (!File.Exists(path)) return "Recording file not found.";

            try
            {
#if WINDOWS
                _winReader = await Task.Run(() => new AudioFileReader(path)).ConfigureAwait(false);
                Duration = _winReader.TotalTime;
                return null;
#else
                string wavPath = path;
                if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    var ffmpeg = FfmpegHelper.FindFfmpegPath();
                    if (ffmpeg == null) return "ffmpeg is required to play non-WAV recordings on this platform.";
                    _tempWav = Path.Combine(Path.GetTempPath(), $"callspire-play-{Guid.NewGuid():N}.wav");
                    bool ok = await FfmpegHelper.ConvertAsync(ffmpeg, $"-y -i \"{path}\" -ac 1 -ar {OutRate} \"{_tempWav}\"", _tempWav, "[RecordingPlayer]", path).ConfigureAwait(false);
                    if (!ok) return "Could not decode the recording.";
                    wavPath = _tempWav;
                }

                var reader = new WaveFileReader(wavPath);
                ISampleProvider sp = reader.ToSampleProvider();
                if (sp.WaveFormat.Channels == 2) sp = new StereoToMonoSampleProvider(sp);
                if (sp.WaveFormat.SampleRate != OutRate) sp = new WdlResamplingSampleProvider(sp, OutRate);
                lock (_lock)
                {
                    _reader = reader;
                    _provider = sp;
                    Duration = reader.TotalTime;
                    _totalSamples = (long)(Duration.TotalSeconds * OutRate);
                    _positionSamples = 0;
                }
                return null;
#endif
            }
            catch (Exception ex)
            {
                AppLog.Log($"[RecordingPlayer] load failed: {ex.Message}");
                return ex.Message;
            }
        }

        public void Play()
        {
            if (_disposed || !IsLoaded) return;
#if WINDOWS
            if (_winReader == null) return;
            if (_waveOut == null)
            {
                _waveOut = new WaveOutEvent();
                _waveOut.Init(_winReader);
                _waveOut.PlaybackStopped += (_, _) =>
                {
                    if (_winReader != null && _winReader.CurrentTime >= _winReader.TotalTime - TimeSpan.FromMilliseconds(200))
                        PlaybackEnded?.Invoke();
                };
                StartWindowsTicker();
            }
            _waveOut.Play();
            _paused = false;
#else
            _paused = false;
            if (_thread is { IsAlive: true }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "RecordingPlayer" };
            _thread.Start();
#endif
        }

        public void Pause()
        {
#if WINDOWS
            _waveOut?.Pause();
#endif
            _paused = true;
        }

        public void Stop()
        {
#if WINDOWS
            try { _waveOut?.Stop(); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
            _winTicker?.Dispose();
            _winTicker = null;
            if (_winReader != null) _winReader.Position = 0;
#endif
            try { _cts?.Cancel(); } catch { }
            var t = _thread;
            _thread = null;
            _cts = null;
            if (t != null && t != Thread.CurrentThread) { try { t.Join(500); } catch { } }
            _paused = false;
            Seek(TimeSpan.Zero);
        }

        public void Seek(TimeSpan position)
        {
#if WINDOWS
            if (_winReader != null)
            {
                _winReader.CurrentTime = position < TimeSpan.Zero ? TimeSpan.Zero : position > _winReader.TotalTime ? _winReader.TotalTime : position;
                PositionChanged?.Invoke(_winReader.CurrentTime, Duration);
                return;
            }
#endif
            lock (_lock)
            {
                if (_reader == null) return;
                var clamped = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
                _reader.CurrentTime = clamped;
                Interlocked.Exchange(ref _positionSamples, (long)(clamped.TotalSeconds * OutRate));
            }
            PositionChanged?.Invoke(position, Duration);
        }

#if WINDOWS
        private Timer? _winTicker;
        private void StartWindowsTicker()
        {
            _winTicker?.Dispose();
            _winTicker = new Timer(_ =>
            {
                try { if (_winReader != null && _waveOut?.PlaybackState == PlaybackState.Playing) PositionChanged?.Invoke(_winReader.CurrentTime, Duration); }
                catch { }
            }, null, 250, 250);
        }
#endif

        private void Run(CancellationToken token)
        {
            PortAudioSink? sink = null;
            try
            {
                sink = new PortAudioSink(-1, OutRate, 1);
                sink.StartAudioSink().GetAwaiter().GetResult();

                var floats = new float[FrameSamples];
                var frame = new short[FrameSamples];
                int sinceNotify = 0;
                bool ended = false;

                while (!token.IsCancellationRequested)
                {
                    if (_paused || sink.QueuedMilliseconds >= 80)
                    {
                        if (token.WaitHandle.WaitOne(10)) break;
                        continue;
                    }

                    int read;
                    lock (_lock)
                    {
                        if (_provider == null) break;
                        read = _provider.Read(floats, 0, floats.Length);
                    }
                    if (read <= 0) { ended = true; break; }

                    for (int i = 0; i < read; i++) frame[i] = (short)Math.Clamp(floats[i] * short.MaxValue, short.MinValue, short.MaxValue);
                    for (int i = read; i < frame.Length; i++) frame[i] = 0;
                    sink.GotAudioSample(AudioSamplingRatesEnum.Rate16KHz, 20, frame);
                    Interlocked.Add(ref _positionSamples, read);

                    if (++sinceNotify >= 12) // ~250 ms
                    {
                        sinceNotify = 0;
                        PositionChanged?.Invoke(Position, Duration);
                    }
                }

                if (ended)
                {
                    // Let the tail drain before signalling.
                    var until = DateTime.UtcNow.AddMilliseconds(300);
                    while (DateTime.UtcNow < until && sink.QueuedMilliseconds > 0 && !token.IsCancellationRequested) Thread.Sleep(10);
                    PositionChanged?.Invoke(Duration, Duration);
                    PlaybackEnded?.Invoke();
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[RecordingPlayer] playback error: {ex.Message}");
            }
            finally
            {
                try { sink?.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
#if WINDOWS
            try { _winReader?.Dispose(); } catch { }
            _winReader = null;
#endif
            lock (_lock)
            {
                try { _reader?.Dispose(); } catch { }
                _reader = null;
                _provider = null;
            }
            if (_tempWav != null) { try { File.Delete(_tempWav); } catch { } _tempWav = null; }
        }
    }
}
