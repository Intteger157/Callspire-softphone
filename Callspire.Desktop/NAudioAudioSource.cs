#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using NAudio.Wave;

namespace Softphone
{
    /// <summary>
    /// Адаптер NAudio для IAudioSource с улучшенными настройками для низкой задержки
    /// Использует NAudio для захвата аудио с микрофона с оптимизацией для VoIP
    /// </summary>
    public class NAudioAudioSource : IAudioSource, IDisposable
    {
        private readonly int _deviceNumber;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly float _gainMultiplier;
        
        private WaveInEvent? _waveIn;
        private bool _isStarted = false;
        private bool _isPaused = false;
        private bool _isDisposed = false;
        
        private readonly object _lockObject = new object();
        
        private AudioFormat? _currentFormat;
        private readonly List<AudioFormat> _supportedFormats = new List<AudioFormat>();
        
        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event SourceErrorDelegate? OnAudioSourceError;

        public NAudioAudioSource(int deviceNumber = -1, int sampleRate = 16000, int channels = 1, float gainMultiplier = 5.0f)
        {
            _deviceNumber = deviceNumber;
            _sampleRate = sampleRate;
            _channels = channels;
            _gainMultiplier = Math.Max(1.0f, Math.Min(5.0f, gainMultiplier));
            
            InitializeSupportedFormats();
        }

        private void InitializeSupportedFormats()
        {
            // Поддерживаемые форматы для VoIP
            // Используем только форматы с ID <= 127, чтобы избежать ошибки
            try
            {
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMA, _sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.G722, _sampleRate, _channels));
            }
            catch (Exception ex)
            {
                // Если возникает ошибка при создании форматов, создаем только PCMU
                System.Diagnostics.Debug.WriteLine($"Error initializing formats: {ex.Message}");
                try
                {
                    _supportedFormats.Clear();
                    _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels));
                }
                catch
                {
                    // Если даже PCMU не работает, оставляем список пустым
                }
            }
            // OPUS имеет ID > 127, поэтому не добавляем его здесь
            // Формат будет выбран автоматически через SDP negotiation
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            lock (_lockObject)
            {
                _supportedFormats.RemoveAll(f => !filter(f));
            }
        }

        public void SetAudioSourceFormat(AudioFormat audioFormat)
        {
            lock (_lockObject)
            {
                // Проверяем, что формат имеет допустимый ID (<= 127)
                // Если формат имеет OPUS или другой формат с ID > 127, используем PCMU по умолчанию
                try
                {
                    // Проверяем формат через reflection, чтобы избежать ошибки
                    var formatIdProperty = audioFormat.GetType().GetProperty("FormatID");
                    if (formatIdProperty != null)
                    {
                        var formatId = Convert.ToInt32(formatIdProperty.GetValue(audioFormat));
                        if (formatId > 127)
                        {
                            // Используем PCMU по умолчанию вместо проблемного формата
                            _currentFormat = new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels);
                            return;
                        }
                    }
                }
                catch
                {
                    // Если не удалось проверить, используем PCMU по умолчанию
                    _currentFormat = new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels);
                    return;
                }
                
                _currentFormat = audioFormat;
            }
        }

        public Task StartAudio()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(NAudioAudioSource));

            lock (_lockObject)
            {
                if (_isStarted)
                    return Task.CompletedTask;

                try
                {
                    // Создаем WaveInEvent с оптимизацией для низкой задержки
                    _waveIn = new WaveInEvent
                    {
                        DeviceNumber = _deviceNumber >= 0 ? _deviceNumber : WaveIn.DeviceCount > 0 ? 0 : -1,
                        WaveFormat = new WaveFormat(_sampleRate, 16, _channels), // 16-bit PCM
                        BufferMilliseconds = 20 // Низкая задержка: 20ms буфер
                    };

                    // Подписываемся на событие получения данных
                    _waveIn.DataAvailable += WaveIn_DataAvailable;
                    _waveIn.RecordingStopped += WaveIn_RecordingStopped;

                    // Запускаем захват
                    _waveIn.StartRecording();
                    
                    _isStarted = true;
                    _isPaused = false;
                }
                catch (Exception ex)
                {
                    OnAudioSourceError?.Invoke($"Error starting audio: {ex.Message}");
                }
            }
            
            return Task.CompletedTask;
        }

        private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_isPaused || _isDisposed)
                return;

            try
            {
                // Конвертируем byte[] в short[]
                int sampleCount = e.BytesRecorded / 2; // 16-bit = 2 bytes per sample
                short[] samples = new short[sampleCount];
                
                Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

                // Применяем усиление
                short[] amplifiedSamples = ApplyGain(samples);

                // Конвертируем в моно, если нужно
                short[] monoBuffer = _channels > 1 ? ConvertToMono(amplifiedSamples) : amplifiedSamples;

                // Вычисляем длительность в миллисекундах
                uint durationMs = (uint)(monoBuffer.Length * 1000 / _sampleRate);

                // Получаем enum для частоты дискретизации
                AudioSamplingRatesEnum samplingRateEnum = ConvertSampleRate(_sampleRate);

                // Отправляем событие
                OnAudioSourceRawSample?.Invoke(samplingRateEnum, durationMs, monoBuffer);
            }
            catch (Exception ex)
            {
                OnAudioSourceError?.Invoke($"Error processing audio data: {ex.Message}");
            }
        }

        private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                OnAudioSourceError?.Invoke($"Recording stopped with error: {e.Exception.Message}");
            }
        }

        private short[] ApplyGain(short[] samples)
        {
            if (_gainMultiplier <= 1.0f)
                return samples;

            short[] amplified = new short[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                float amplifiedValue = samples[i] * _gainMultiplier;
                if (amplifiedValue > short.MaxValue)
                    amplifiedValue = short.MaxValue;
                else if (amplifiedValue < short.MinValue)
                    amplifiedValue = short.MinValue;
                amplified[i] = (short)amplifiedValue;
            }
            return amplified;
        }

        private short[] ConvertToMono(short[] stereo)
        {
            if (_channels <= 1)
                return stereo;

            int monoLength = stereo.Length / _channels;
            short[] mono = new short[monoLength];
            
            for (int i = 0; i < monoLength; i++)
            {
                int sum = 0;
                for (int ch = 0; ch < _channels; ch++)
                {
                    sum += stereo[i * _channels + ch];
                }
                mono[i] = (short)(sum / _channels);
            }
            
            return mono;
        }

        private AudioSamplingRatesEnum ConvertSampleRate(int sampleRate)
        {
            return sampleRate switch
            {
                8000 => AudioSamplingRatesEnum.Rate8KHz,
                16000 => AudioSamplingRatesEnum.Rate16KHz,
                _ => AudioSamplingRatesEnum.Rate16KHz // Используем 16kHz как по умолчанию
            };
        }

        public Task PauseAudio()
        {
            lock (_lockObject)
            {
                _isPaused = true;
            }
            return Task.CompletedTask;
        }

        public Task ResumeAudio()
        {
            lock (_lockObject)
            {
                _isPaused = false;
            }
            return Task.CompletedTask;
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            // Этот метод используется для внешних источников, не применим здесь
        }

        public void ExternalAudioSourceRawSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, byte[] sample)
        {
            // Этот метод используется для внешних источников, не применим здесь
        }

        public Task CloseAudio()
        {
            lock (_lockObject)
            {
                if (!_isStarted)
                    return Task.CompletedTask;

                _isStarted = false;
                _isPaused = false;

                if (_waveIn != null)
                {
                    try
                    {
                        _waveIn.StopRecording();
                        _waveIn.DataAvailable -= WaveIn_DataAvailable;
                        _waveIn.RecordingStopped -= WaveIn_RecordingStopped;
                        _waveIn.Dispose();
                    }
                    catch { }
                    _waveIn = null;
                }
            }
            
            return Task.CompletedTask;
        }

        public List<AudioFormat> GetAudioSourceFormats()
        {
            lock (_lockObject)
            {
                // Возвращаем только форматы с допустимым ID (<= 127)
                var safeFormats = new List<AudioFormat>();
                foreach (var format in _supportedFormats)
                {
                    try
                    {
                        // Проверяем формат через reflection
                        var formatIdProperty = format.GetType().GetProperty("FormatID");
                        if (formatIdProperty != null)
                        {
                            var formatId = Convert.ToInt32(formatIdProperty.GetValue(format));
                            if (formatId <= 127)
                            {
                                safeFormats.Add(format);
                            }
                        }
                        else
                        {
                            // Если не можем проверить, добавляем формат (может быть безопасным)
                            safeFormats.Add(format);
                        }
                    }
                    catch
                    {
                        // Пропускаем проблемные форматы
                    }
                }
                
                // Если нет безопасных форматов, возвращаем PCMU
                if (safeFormats.Count == 0)
                {
                    try
                    {
                        safeFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels));
                    }
                    catch
                    {
                        // Если даже PCMU не работает, возвращаем пустой список
                    }
                }
                
                return safeFormats;
            }
        }

        public bool HasEncodedAudioSubscribers()
        {
            return OnAudioSourceEncodedSample != null || OnAudioSourceEncodedFrameReady != null;
        }

        public bool IsAudioSourcePaused()
        {
            lock (_lockObject)
            {
                return _isPaused;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                var t = CloseAudio();
                if (!t.IsCompleted && !t.Wait(1000))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await t; } catch { }
                    });
                }
            }
            catch
            {
                // ignore
            }
            _isDisposed = true;
        }
    }
}


#endif // WINDOWS
