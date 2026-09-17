using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// Обертка для AudioSource с программным усилением сигнала
    /// </summary>
    public class AmplifiedAudioSource : IAudioSource
    {
        private readonly IAudioSource _baseSource;
        private float _gainMultiplier = 6.0f; // Максимальное усиление в 8 раз (до 18 дБ)

        public AmplifiedAudioSource(IAudioSource baseSource, float gainMultiplier = 6.0f)
        {
            _baseSource = baseSource ?? throw new ArgumentNullException(nameof(baseSource));
            _gainMultiplier = Math.Max(1.0f, Math.Min(8.0f, gainMultiplier)); // Ограничиваем от 1x до 8x
        }

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event SourceErrorDelegate? OnAudioSourceError;

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            _baseSource.RestrictFormats(filter);
        }

        public void SetAudioSourceFormat(AudioFormat audioFormat)
        {
            _baseSource.SetAudioSourceFormat(audioFormat);
        }

        public Task StartAudio()
        {
            AppLog.Log($"[AmplifiedAudioSource] StartAudio called, subscribing to base source events, current subscribers={OnAudioSourceRawSample?.GetInvocationList().Length ?? 0}");
            
            // Подписываемся на события базового источника ДО вызова StartAudio
            // Это важно, чтобы не пропустить события, которые могут быть подняты сразу после старта
            _baseSource.OnAudioSourceRawSample += OnBaseSourceRawSample;
            _baseSource.OnAudioSourceEncodedSample += OnBaseSourceEncodedSample;
            _baseSource.OnAudioSourceEncodedFrameReady += OnBaseSourceEncodedFrameReady;
            _baseSource.OnAudioSourceError += OnBaseSourceError;
            
            AppLog.Log($"[AmplifiedAudioSource] Subscribed to base source events, calling base.StartAudio()");
            
            var result = _baseSource.StartAudio();
            
            // Проверяем подписчиков после старта
            var subscribersAfterStart = OnAudioSourceRawSample?.GetInvocationList().Length ?? 0;
            AppLog.Log($"[AmplifiedAudioSource] StartAudio completed, subscribers after start={subscribersAfterStart}");
            
            // Дополнительная проверка: если событие не поднимается, попробуем подписаться еще раз
            result.ContinueWith(_ =>
            {
                // Проверяем, что подписка все еще активна
                var currentSubscribers = OnAudioSourceRawSample?.GetInvocationList().Length ?? 0;
                AppLog.Log($"[AmplifiedAudioSource] After StartAudio completion, current subscribers={currentSubscribers}");
                
                // Если подписчиков нет, это странно, но логируем
                if (currentSubscribers == 0)
                {
                    AppLog.Log($"[AmplifiedAudioSource] WARNING: No subscribers after StartAudio completion!");
                }
            }, TaskScheduler.Default);
            
            return result;
        }

        public Task PauseAudio()
        {
            try
            {
                return _baseSource.PauseAudio();
            }
            catch
            {
                // Игнорируем ошибки и возвращаем завершенную задачу
                return Task.CompletedTask;
            }
        }

        public Task ResumeAudio()
        {
            try
            {
                return _baseSource.ResumeAudio();
            }
            catch
            {
                // Игнорируем ошибки и возвращаем завершенную задачу
                return Task.CompletedTask;
            }
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            // Усиливаем сигнал перед передачей
            short[] amplifiedSample = AmplifyAudio(sample);
            _baseSource.ExternalAudioSourceRawSample(samplingRate, durationMilliseconds, amplifiedSample);
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, byte[] sample)
        {
            // Для byte[] используем reflection для вызова правильной перегрузки
            try
            {
                var method = _baseSource.GetType().GetMethod("ExternalAudioSourceRawSample", 
                    new Type[] { typeof(AudioSamplingRatesEnum), typeof(uint), typeof(byte[]) });
                if (method != null)
                {
                    method.Invoke(_baseSource, new object[] { samplingRate, durationMilliseconds, sample });
                }
            }
            catch
            {
                // Если метод не существует или произошла ошибка, игнорируем
                // byte[] сэмплы обычно не используются для raw аудио
            }
        }

        public Task CloseAudio()
        {
            _baseSource.OnAudioSourceRawSample -= OnBaseSourceRawSample;
            _baseSource.OnAudioSourceEncodedSample -= OnBaseSourceEncodedSample;
            _baseSource.OnAudioSourceEncodedFrameReady -= OnBaseSourceEncodedFrameReady;
            _baseSource.OnAudioSourceError -= OnBaseSourceError;
            return _baseSource.CloseAudio();
        }

        public List<AudioFormat> GetAudioSourceFormats()
        {
            return _baseSource.GetAudioSourceFormats();
        }

        public bool HasEncodedAudioSubscribers()
        {
            return _baseSource.HasEncodedAudioSubscribers();
        }

        public bool IsAudioSourcePaused()
        {
            return _baseSource.IsAudioSourcePaused();
        }

        private static int _onBaseSourceRawSampleCallCount = 0;
        private void OnBaseSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            // ДЕТАЛЬНОЕ ЛОГИРОВАНИЕ для диагностики
            bool isFirstCall = (_onBaseSourceRawSampleCallCount == 0);
            _onBaseSourceRawSampleCallCount++;
            
            if (isFirstCall || _onBaseSourceRawSampleCallCount <= 10)
            {
                var subscribers = OnAudioSourceRawSample?.GetInvocationList();
                int subscriberCount = subscribers?.Length ?? 0;
                
                AppLog.Log($"[AmplifiedAudioSource][MIC] OnBaseSourceRawSample called #{_onBaseSourceRawSampleCallCount}: " +
                    $"sampleRate={samplingRate}, durationMs={durationMilliseconds}, " +
                    $"samples.Length={sample?.Length ?? 0}, " +
                    $"subscribers={subscriberCount}, " +
                    $"event null={OnAudioSourceRawSample == null}");
                
                if (sample != null && sample.Length > 0)
                {
                    int peak = sample.Max(s => Math.Abs(s));
                    AppLog.Log($"[AmplifiedAudioSource][MIC] Sample peak={peak}, first 3 samples: [{string.Join(", ", sample.Take(3))}]");
                }
            }
            else if (_onBaseSourceRawSampleCallCount % 100 == 0)
            {
                var subscribers = OnAudioSourceRawSample?.GetInvocationList();
                int subscriberCount = subscribers?.Length ?? 0;
                AppLog.Log($"[AmplifiedAudioSource] OnBaseSourceRawSample called #{_onBaseSourceRawSampleCallCount} times, subscribers={subscriberCount}");
            }
            
            // Проверяем наличие данных перед обработкой
            if (sample == null || sample.Length == 0)
            {
                if (_onBaseSourceRawSampleCallCount <= 5)
                {
                    AppLog.Log($"[AmplifiedAudioSource] WARNING: OnBaseSourceRawSample called with null or empty sample");
                }
                return;
            }
            
            // Усиливаем сигнал перед передачей дальше
            short[] amplifiedSample = AmplifyAudio(sample);
            
            // Проверяем наличие подписчиков перед вызовом
            if (OnAudioSourceRawSample != null)
            {
                try
                {
                    OnAudioSourceRawSample.Invoke(samplingRate, durationMilliseconds, amplifiedSample);
                    
                    if (isFirstCall)
                    {
                        AppLog.Log($"[AmplifiedAudioSource][MIC] OnAudioSourceRawSample.Invoke called successfully, subscribers notified");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[AmplifiedAudioSource] ERROR invoking OnAudioSourceRawSample: {ex.Message}, stack: {ex.StackTrace}");
                }
            }
            else
            {
                if (_onBaseSourceRawSampleCallCount <= 5)
                {
                    AppLog.Log($"[AmplifiedAudioSource] WARNING: OnBaseSourceRawSample called but OnAudioSourceRawSample has no subscribers!");
                }
            }
        }

        private void OnBaseSourceEncodedSample(uint durationMilliseconds, byte[] sample)
        {
            OnAudioSourceEncodedSample?.Invoke(durationMilliseconds, sample);
        }

        private void OnBaseSourceEncodedFrameReady(EncodedAudioFrame frame)
        {
            OnAudioSourceEncodedFrameReady?.Invoke(frame);
        }

        private void OnBaseSourceError(string errorMessage)
        {
            OnAudioSourceError?.Invoke(errorMessage);
        }

        /// <summary>
        /// Усиливает аудио сигнал с ограничением для предотвращения клиппинга
        /// </summary>
        private short[] AmplifyAudio(short[] samples)
        {
            if (samples == null || samples.Length == 0)
                return samples ?? Array.Empty<short>();

            short[] amplified = new short[samples.Length];
            
            for (int i = 0; i < samples.Length; i++)
            {
                // Усиливаем сигнал
                float amplifiedValue = samples[i] * _gainMultiplier;
                
                // Ограничиваем значение для предотвращения клиппинга
                if (amplifiedValue > short.MaxValue)
                    amplifiedValue = short.MaxValue;
                else if (amplifiedValue < short.MinValue)
                    amplifiedValue = short.MinValue;
                
                amplified[i] = (short)amplifiedValue;
            }
            
            return amplified;
        }

        /// <summary>
        /// Устанавливает коэффициент усиления (1.0 = без усиления, 2.0 = усиление в 2 раза)
        /// </summary>
        public void SetGain(float gainMultiplier)
        {
            _gainMultiplier = Math.Max(1.0f, Math.Min(8.0f, gainMultiplier));
        }
    }
}
