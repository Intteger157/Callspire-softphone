using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using System.Text;
using System.Threading;

namespace Softphone
{
    /// <summary>
    /// Tap/tee обёртка для AudioSource, которая перехватывает raw PCM samples
    /// для записи, не изменяя поток данных для VoIPMediaSession
    /// </summary>
    public sealed class TapAudioSource : IAudioSource
    {
        private readonly IAudioSource _inner;
        private bool _started = false;
        private readonly object _lockObject = new object();
        private int _tapCount = 0; // Счетчик для логирования

        public TapAudioSource(IAudioSource inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            
            // Пробрасываем ошибки от внутреннего источника
            _inner.OnAudioSourceError += (error) => OnAudioSourceError?.Invoke(error);
        }

        /// <summary>
        /// Хук для записи outbound PCM samples (вызывается ПЕРЕД пробросом в медиасессию)
        /// </summary>
        public Action<AudioSamplingRatesEnum, uint, short[]>? OnTapRawSample;

        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
        public event SourceErrorDelegate? OnAudioSourceError;

        public Task StartAudio()
        {
            lock (_lockObject)
            {
                if (_started)
                    return Task.CompletedTask;
                
                _started = true;
                
                // Подписываемся на внутренний источник
                _inner.OnAudioSourceRawSample += Inner_OnAudioSourceRawSample;
                _inner.OnAudioSourceEncodedSample += Inner_OnAudioSourceEncodedSample;
                _inner.OnAudioSourceEncodedFrameReady += Inner_OnAudioSourceEncodedFrameReady;
                
                AppLog.Log($"[TapAudioSource] Subscribed to inner source ({_inner.GetType().Name}), starting audio...");
            }
            
            return _inner.StartAudio();
        }

        public Task CloseAudio()
        {
            lock (_lockObject)
            {
                if (_started)
                {
                    _inner.OnAudioSourceRawSample -= Inner_OnAudioSourceRawSample;
                    _inner.OnAudioSourceEncodedSample -= Inner_OnAudioSourceEncodedSample;
                    _inner.OnAudioSourceEncodedFrameReady -= Inner_OnAudioSourceEncodedFrameReady;
                    _started = false;
                    AppLog.Log("[TapAudioSource] Unsubscribed from inner source");
                }
            }
            
            return _inner.CloseAudio();
        }

        private void Inner_OnAudioSourceRawSample(AudioSamplingRatesEnum rate, uint durationMs, short[] samples)
        {
            // ЖЁСТКОЕ ЛОГИРОВАНИЕ для диагностики: вызывается ли событие вообще?
            var n = Interlocked.Increment(ref _tapCount);
            if (n == 1 || n % 50 == 0)
            {
                int peak = 0;
                if (samples != null && samples.Length > 0)
                {
                    peak = samples.Max(s => Math.Abs(s));
                }
                AppLog.Log($"[TapAudioSource] RAW #{n}: rate={rate}, dur={durationMs}ms, samples={samples?.Length ?? 0}, peak={peak}");
            }
            
            // 1) TAP: сюда гарантированно приходят те же samples, что идут в RTP sender
            // Вызываем ПЕРЕД пробросом, чтобы гарантировать запись
            try
            {
                if (samples != null && samples.Length > 0)
                {
                    OnTapRawSample?.Invoke(rate, durationMs, samples);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[TapAudioSource] ERROR in OnTapRawSample: {ex.Message}");
            }

            // 2) Проброс в медиасессию (чтобы звонок работал)
            try
            {
                if (samples != null)
                {
                    OnAudioSourceRawSample?.Invoke(rate, durationMs, samples);
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[TapAudioSource] ERROR invoking OnAudioSourceRawSample: {ex.Message}");
            }
        }

        private void Inner_OnAudioSourceEncodedSample(uint durationMs, byte[] sample)
        {
            OnAudioSourceEncodedSample?.Invoke(durationMs, sample);
        }

        private void Inner_OnAudioSourceEncodedFrameReady(EncodedAudioFrame frame)
        {
            OnAudioSourceEncodedFrameReady?.Invoke(frame);
        }

        public Task PauseAudio() => _inner.PauseAudio();
        public Task ResumeAudio() => _inner.ResumeAudio();
        public void SetAudioSourceFormat(AudioFormat audioFormat) => _inner.SetAudioSourceFormat(audioFormat);
        public void RestrictFormats(Func<AudioFormat, bool> filter) => _inner.RestrictFormats(filter);
        public bool HasEncodedAudioSubscribers() =>
            _inner.HasEncodedAudioSubscribers() || OnAudioSourceEncodedFrameReady != null;
        public bool IsAudioSourcePaused() => _inner.IsAudioSourcePaused();
        public List<AudioFormat> GetAudioSourceFormats() => _inner.GetAudioSourceFormats();
        
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum sampleRate, uint durationMilliseconds, short[] sample)
        {
            _inner.ExternalAudioSourceRawSample(sampleRate, durationMilliseconds, sample);
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum sampleRate, uint durationMilliseconds, byte[] sample)
        {
            // Конвертируем byte[] в short[] для передачи внутреннему источнику
            if (sample != null && sample.Length > 0)
            {
                int sampleCount = sample.Length / 2;
                short[] shortSamples = new short[sampleCount];
                Buffer.BlockCopy(sample, 0, shortSamples, 0, sample.Length);
                _inner.ExternalAudioSourceRawSample(sampleRate, durationMilliseconds, shortSamples);
            }
        }
    }
}

