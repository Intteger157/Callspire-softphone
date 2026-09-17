using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;

namespace Softphone
{
    /// <summary>
    /// Обертка для AudioSource с программным эхоподавлением
    /// Использует простой алгоритм подавления эха на основе уровня сигнала и временной задержки
    /// </summary>
    public class EchoCancelledAudioSource : IAudioSource
    {
        private readonly IAudioSource _baseSource;
        private readonly Queue<short[]> _referenceBuffer; // Буфер для reference сигнала (из динамиков)
        private readonly int _bufferSize; // Размер буфера в сэмплах
        private readonly int _sampleRate;
        private readonly object _lockObject = new object();
        private bool _isEnabled = true;

        public EchoCancelledAudioSource(IAudioSource baseSource, int sampleRate = 16000, int bufferMs = 200)
        {
            _baseSource = baseSource ?? throw new ArgumentNullException(nameof(baseSource));
            _sampleRate = sampleRate;
            // Буфер на bufferMs миллисекунд для хранения reference сигнала
            _bufferSize = (sampleRate * bufferMs) / 1000;
            _referenceBuffer = new Queue<short[]>();
        }

        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event SourceErrorDelegate? OnAudioSourceError;

        /// <summary>
        /// Добавляет reference сигнал (из динамиков) для эхоподавления
        /// </summary>
        public void AddReferenceSignal(short[] referenceSamples)
        {
            if (!_isEnabled || referenceSamples == null || referenceSamples.Length == 0)
                return;

            lock (_lockObject)
            {
                // Добавляем reference сигнал в буфер
                _referenceBuffer.Enqueue((short[])referenceSamples.Clone());
                
                // Ограничиваем размер буфера
                while (_referenceBuffer.Count > _bufferSize / Math.Max(1, referenceSamples.Length))
                {
                    _referenceBuffer.Dequeue();
                }
            }
        }

        /// <summary>
        /// Включает/выключает эхоподавление
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            _isEnabled = enabled;
        }

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
            _baseSource.OnAudioSourceRawSample += OnBaseSourceRawSample;
            _baseSource.OnAudioSourceEncodedSample += OnBaseSourceEncodedSample;
            _baseSource.OnAudioSourceEncodedFrameReady += OnBaseSourceEncodedFrameReady;
            _baseSource.OnAudioSourceError += OnBaseSourceError;
            return _baseSource.StartAudio();
        }

        public Task PauseAudio()
        {
            return _baseSource.PauseAudio();
        }

        public Task ResumeAudio()
        {
            return _baseSource.ResumeAudio();
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            _baseSource.ExternalAudioSourceRawSample(samplingRate, durationMilliseconds, sample);
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, byte[] sample)
        {
            try
            {
                var method = _baseSource.GetType().GetMethod("ExternalAudioSourceRawSample",
                    new Type[] { typeof(AudioSamplingRatesEnum), typeof(uint), typeof(byte[]) });
                if (method != null)
                {
                    method.Invoke(_baseSource, new object[] { samplingRate, durationMilliseconds, sample });
                }
            }
            catch { }
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

        private void OnBaseSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            if (sample == null || sample.Length == 0)
            {
                OnAudioSourceRawSample?.Invoke(samplingRate, durationMilliseconds, sample);
                return;
            }

            if (!_isEnabled)
            {
                // Если эхоподавление выключено, просто пробрасываем сигнал
                OnAudioSourceRawSample?.Invoke(samplingRate, durationMilliseconds, sample);
                return;
            }

            // Простое эхоподавление: вычитаем reference сигнал из mixed сигнала
            short[] processedSample = ProcessEchoCancellation(sample);
            OnAudioSourceRawSample?.Invoke(samplingRate, durationMilliseconds, processedSample);
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
        /// Обрабатывает эхоподавление: вычитает reference сигнал из mixed сигнала
        /// </summary>
        private short[] ProcessEchoCancellation(short[] mixedSignal)
        {
            if (mixedSignal == null || mixedSignal.Length == 0)
                return mixedSignal ?? Array.Empty<short>();

            lock (_lockObject)
            {
                if (_referenceBuffer.Count == 0)
                {
                    // Если нет reference сигнала, просто возвращаем исходный сигнал
                    return mixedSignal;
                }

                // Берем последний reference сигнал из буфера
                short[]? referenceSignal = _referenceBuffer.Count > 0 ? _referenceBuffer.LastOrDefault() : null;
                if (referenceSignal == null || referenceSignal.Length == 0)
                {
                    return mixedSignal;
                }

                // Выравниваем размеры сигналов
                int minLength = Math.Min(mixedSignal.Length, referenceSignal.Length);
                short[] processed = new short[mixedSignal.Length];

                // Простое вычитание: mixed - reference * коэффициент
                // Коэффициент можно настроить в зависимости от уровня эха
                float echoAttenuation = 0.7f; // Коэффициент ослабления эха (0.0 - 1.0)

                for (int i = 0; i < minLength; i++)
                {
                    // Вычитаем reference сигнал из mixed сигнала
                    float result = mixedSignal[i] - (referenceSignal[i] * echoAttenuation);
                    
                    // Ограничиваем значение для предотвращения клиппинга
                    if (result > short.MaxValue)
                        result = short.MaxValue;
                    else if (result < short.MinValue)
                        result = short.MinValue;
                    
                    processed[i] = (short)result;
                }

                // Копируем остаток mixed сигнала, если он длиннее
                for (int i = minLength; i < mixedSignal.Length; i++)
                {
                    processed[i] = mixedSignal[i];
                }

                return processed;
            }
        }
    }
}
