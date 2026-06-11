using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// Обертка для AudioSource, которая перехватывает outbound PCM аудио для записи
    /// </summary>
    public class RecordingAudioSource : IAudioSource
    {
        private readonly IAudioSource _originalSource;
        private readonly SipService _sipService;
        private bool _isSubscribed = false;
        private readonly object _subscribeLock = new object();
        private static int _onRawSampleCallCount = 0;

        public RecordingAudioSource(IAudioSource originalSource, SipService sipService)
        {
            _originalSource = originalSource ?? throw new ArgumentNullException(nameof(originalSource));
            _sipService = sipService ?? throw new ArgumentNullException(nameof(sipService));
            
            // Подписываемся сразу в конструкторе для гарантированного перехвата всех событий
            SubscribeToRawSamples();
        }

        private void SubscribeToRawSamples()
        {
            lock (_subscribeLock)
            {
                if (!_isSubscribed)
                {
                    _originalSource.OnAudioSourceRawSample += OnRawSample;
                    _isSubscribed = true;
                    
                    AppLog.Log($"[RecordingAudioSource] Subscribed to OnAudioSourceRawSample (source type: {_originalSource.GetType().Name})");
                }
            }
        }

        private void UnsubscribeFromRawSamples()
        {
            lock (_subscribeLock)
            {
                if (_isSubscribed)
                {
                    _originalSource.OnAudioSourceRawSample -= OnRawSample;
                    _isSubscribed = false;
                    AppLog.Log("[RecordingAudioSource] Unsubscribed from OnAudioSourceRawSample");
                }
            }
        }

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample
        {
            add => _originalSource.OnAudioSourceEncodedSample += value;
            remove => _originalSource.OnAudioSourceEncodedSample -= value;
        }

        private event RawAudioSampleDelegate? _onAudioSourceRawSample;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample
        {
            add
            {
                _onAudioSourceRawSample += value;
                // Также подписываемся на оригинальный источник для проброса событий
                _originalSource.OnAudioSourceRawSample += value;
            }
            remove
            {
                _onAudioSourceRawSample -= value;
                _originalSource.OnAudioSourceRawSample -= value;
            }
        }

        public event SourceErrorDelegate? OnAudioSourceError
        {
            add => _originalSource.OnAudioSourceError += value;
            remove => _originalSource.OnAudioSourceError -= value;
        }

        public Task CloseAudio()
        {
            UnsubscribeFromRawSamples();
            return _originalSource.CloseAudio();
        }
        
        public List<AudioFormat> GetAudioSourceFormats() => _originalSource.GetAudioSourceFormats();
        
        public Task StartAudio()
        {
            // Убеждаемся, что подписка активна
            SubscribeToRawSamples();
            
            // Дополнительно подписываемся после StartAudio, на случай если событие поднимается только после старта
            var startTask = _originalSource.StartAudio();
            
            // Подписываемся еще раз после старта, чтобы гарантировать подписку
            startTask.ContinueWith(_ =>
            {
                SubscribeToRawSamples();
                AppLog.Log($"[RecordingAudioSource] Re-subscribed after StartAudio completion");
            }, TaskScheduler.Default);
            
            return startTask;
        }
        
        public Task PauseAudio() => _originalSource.PauseAudio();
        public Task ResumeAudio() => _originalSource.ResumeAudio();
        public void SetAudioSourceFormat(AudioFormat audioFormat) => _originalSource.SetAudioSourceFormat(audioFormat);
        public void RestrictFormats(Func<AudioFormat, bool> filter) => _originalSource.RestrictFormats(filter);
        public bool HasEncodedAudioSubscribers() => _originalSource.HasEncodedAudioSubscribers();
        public bool IsAudioSourcePaused() => _originalSource.IsAudioSourcePaused();
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum sampleRate, uint durationMilliseconds, short[] sample) => _originalSource.ExternalAudioSourceRawSample(sampleRate, durationMilliseconds, sample);
        
        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum sampleRate, uint durationMilliseconds, byte[] sample)
        {
            // Конвертируем byte[] в short[] для передачи оригинальному source
            int sampleCount = sample.Length / 2;
            short[] shortSamples = new short[sampleCount];
            Buffer.BlockCopy(sample, 0, shortSamples, 0, sample.Length);
            _originalSource.ExternalAudioSourceRawSample(sampleRate, durationMilliseconds, shortSamples);
        }

        /// <summary>
        /// Обработчик события получения raw PCM samples для записи
        /// </summary>
        private void OnRawSample(AudioSamplingRatesEnum sampleRate, uint durationMs, short[] samples)
        {
            // ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ для диагностики проблемы с отсутствием outbound записи
            bool isFirstCall = (_onRawSampleCallCount == 0);
            _onRawSampleCallCount++;
            
            // Конвертируем sample rate в int (один раз в начале)
            int sampleRateInt = ConvertSampleRate(sampleRate);
            
            if (isFirstCall || _onRawSampleCallCount <= 10)
            {
                // Логируем детальную информацию о первом и первых 10 вызовах
                int bytesLength = samples?.Length * 2 ?? 0;
                
                AppLog.Log($"[RecordingAudioSource][MIC] OnRawSample called #{_onRawSampleCallCount}: " +
                    $"sampleRate={sampleRate} ({sampleRateInt}Hz), durationMs={durationMs}, " +
                    $"samples.Length={samples?.Length ?? 0}, bytes={bytesLength}, " +
                    $"sipService null={_sipService == null}");
                
                // Проверяем первые несколько сэмплов для диагностики
                if (samples != null && samples.Length > 0)
                {
                    int samplesToShow = Math.Min(5, samples.Length);
                    var firstSamples = string.Join(", ", samples.Take(samplesToShow));
                    int peak = samples.Max(s => Math.Abs(s));
                    AppLog.Log($"[RecordingAudioSource][MIC] First {samplesToShow} samples: [{firstSamples}], peak={peak}");
                }
            }
            else if (_onRawSampleCallCount % 50 == 0)
            {
                AppLog.Log($"[RecordingAudioSource] OnRawSample called #{_onRawSampleCallCount} times, still receiving audio");
            }
            
            // Проверяем валидность данных
            if (samples == null || samples.Length == 0)
            {
                if (_onRawSampleCallCount <= 5)
                {
                    AppLog.Log($"[RecordingAudioSource] WARNING: OnRawSample called with empty samples array");
                }
                return;
            }
            
            // Конвертируем short[] в byte[] (s16le)
            byte[] pcmData = new byte[samples.Length * 2];
            Buffer.BlockCopy(samples, 0, pcmData, 0, pcmData.Length);

            // Передаем в рекордер
            // ПРИМЕЧАНИЕ: Этот класс больше не используется в основной цепочке аудио (теперь используется TapAudioSource)
            // Оставлен для обратной совместимости, но вызов RecordOutboundPcm отключен
            try
            {
                if (_sipService == null)
                {
                    if (_onRawSampleCallCount <= 5)
                    {
                        AppLog.Log($"[RecordingAudioSource] ERROR: _sipService is null, cannot record outbound PCM");
                    }
                    return;
                }
                
                // УСТАРЕЛО: Теперь используется TapAudioSource напрямую
                // _sipService.RecordOutboundPcm(pcmData, sampleRateInt);
                
                if (isFirstCall)
                {
                    AppLog.Log($"[RecordingAudioSource][MIC] OnRawSample called (recording disabled - using TapAudioSource instead): bytes={pcmData.Length}, rate={sampleRateInt}Hz");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[RecordingAudioSource] ERROR in OnRawSample: {ex.Message}, stack: {ex.StackTrace}");
            }
            
            // Пробрасываем событие дальше для подписчиков через VoIPMediaSession
            try
            {
                _onAudioSourceRawSample?.Invoke(sampleRate, durationMs, samples);
            }
            catch (Exception ex)
            {
                if (_onRawSampleCallCount <= 5)
                {
                    AppLog.Log($"[RecordingAudioSource] ERROR invoking _onAudioSourceRawSample: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Конвертирует AudioSamplingRatesEnum в int
        /// </summary>
        private static int ConvertSampleRate(AudioSamplingRatesEnum sampleRate)
        {
            return sampleRate switch
            {
                AudioSamplingRatesEnum.Rate8KHz => 8000,
                AudioSamplingRatesEnum.Rate16KHz => 16000,
                _ => 48000 // По умолчанию 48kHz для Opus
            };
        }
    }
}
