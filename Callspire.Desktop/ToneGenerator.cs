using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>
    /// Генератор тональных сигналов для софтфона (dial tone, ringback tone, busy tone).
    /// Desktop-реализация <see cref="ITonePlayer"/> на NAudio (WaveOutEvent).
    /// </summary>
    public class ToneGenerator : ITonePlayer
    {
        private WaveOutEvent? _waveOut;
        private ToneWaveProvider? _toneProvider;
        private bool _isDisposed = false;
        private bool _isPlaying = false; // Флаг для отслеживания воспроизведения
        private readonly object _lockObject = new object();

        /// <summary>
        /// Воспроизводит ringback tone (гудок ожидания ответа)
        /// Ringback tone: 440 Hz + 480 Hz (400 ms on, 200 ms off, 400 ms on, 2000 ms off)
        /// </summary>
        public void PlayRingbackTone()
        {
            lock (_lockObject)
            {
                if (_isDisposed || _isPlaying) return; // Не запускаем, если уже играет

                Stop(); // Останавливаем предыдущий тон, если был

                try
                {
                    // Ringback tone: прерывистый тон с паттерном 400ms on, 200ms off, 400ms on, 2000ms off
                    // Используем два тона: 440 Hz и 480 Hz одновременно (упрощенно - один тон 440 Hz)
                    _toneProvider = new ToneWaveProvider(440, 8000, 1, ringbackPattern: true);
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(_toneProvider);
                    _waveOut.Play();
                    _isPlaying = true;
                    AppLog.Log("[ToneGenerator] Ringback tone started");
                }
                catch (Exception ex)
                {
                    _isPlaying = false;
                    AppLog.Log($"[ToneGenerator] Error playing ringback tone: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Error playing ringback tone: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Воспроизводит busy tone (занято)
        /// </summary>
        public void PlayBusyTone()
        {
            lock (_lockObject)
            {
                if (_isDisposed || _isPlaying) return; // Не запускаем, если уже играет

                Stop();

                try
                {
                    // Busy tone: 480 Hz + 620 Hz (500 ms on, 500 ms off)
                    // Упрощенная версия: прерывистый тон 480 Hz
                    _toneProvider = new ToneWaveProvider(480, 8000, 1, true); // true = прерывистый
                    _waveOut = new WaveOutEvent();
                    _waveOut.Init(_toneProvider);
                    _waveOut.Play();
                    _isPlaying = true;
                    AppLog.Log("[ToneGenerator] Busy tone started");
                }
                catch (Exception ex)
                {
                    _isPlaying = false;
                    AppLog.Log($"[ToneGenerator] Error playing busy tone: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Error playing busy tone: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Останавливает воспроизведение тона
        /// </summary>
        public void Stop()
        {
            lock (_lockObject)
            {
                try
                {
                    if (_waveOut != null)
                    {
                        _waveOut.Stop();
                        _waveOut.Dispose();
                        _waveOut = null;
                        _isPlaying = false;
                        AppLog.Log("[ToneGenerator] Tone stopped");
                    }

                    _toneProvider = null;
                    _isPlaying = false;
                }
                catch (Exception ex)
                {
                    _isPlaying = false;
                    AppLog.Log($"[ToneGenerator] Error stopping tone: {ex.Message}");
                    // Игнорируем ошибки при остановке
                }
            }
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                _isDisposed = true;
                Stop();
            }
        }
    }

    /// <summary>
    /// Провайдер для генерации синусоидального тона
    /// </summary>
    internal class ToneWaveProvider : WaveProvider32
    {
        private readonly double _frequency;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly bool _interrupted; // Для прерывистого тона (busy tone)
        private readonly bool _ringbackPattern; // Для ringback tone (400ms on, 200ms off, 400ms on, 2000ms off)
        private long _sampleCount = 0;

        public ToneWaveProvider(double frequency, int sampleRate = 8000, int channels = 1, bool interrupted = false, bool ringbackPattern = false)
        {
            _frequency = frequency;
            _sampleRate = sampleRate;
            _channels = channels;
            _interrupted = interrupted;
            _ringbackPattern = ringbackPattern;
            SetWaveFormat(sampleRate, channels);
        }

        public override int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i += _channels)
            {
                float sample = 0;

                if (_ringbackPattern)
                {
                    // Ringback tone: 400ms on, 200ms off, 400ms on, 2000ms off
                    // Полный цикл: 3000ms = 24000 samples при 8000 Hz
                    long cyclePosition = _sampleCount % 24000; // Полный цикл
                    
                    if (cyclePosition < 3200) // 400ms on (400ms * 8 = 3200 samples)
                    {
                        double time = (double)_sampleCount / _sampleRate;
                        sample = (float)(0.3 * Math.Sin(2 * Math.PI * _frequency * time));
                    }
                    else if (cyclePosition < 4800) // 200ms off (200ms * 8 = 1600 samples, 3200 + 1600 = 4800)
                    {
                        // Тишина
                        sample = 0;
                    }
                    else if (cyclePosition < 8000) // 400ms on (400ms * 8 = 3200 samples, 4800 + 3200 = 8000)
                    {
                        double time = (double)_sampleCount / _sampleRate;
                        sample = (float)(0.3 * Math.Sin(2 * Math.PI * _frequency * time));
                    }
                    else // 2000ms off (2000ms * 8 = 16000 samples, 8000 + 16000 = 24000)
                    {
                        // Тишина
                        sample = 0;
                    }
                }
                else if (_interrupted)
                {
                    // Прерывистый тон (busy tone): 500ms on, 500ms off (при 8000 Hz это 4000 samples)
                    long cyclePosition = _sampleCount % 8000; // 1 секунда = 8000 samples
                    if (cyclePosition < 4000) // Первая половина - тон
                    {
                        double time = (double)_sampleCount / _sampleRate;
                        sample = (float)(0.3 * Math.Sin(2 * Math.PI * _frequency * time));
                    }
                    // Вторая половина - тишина
                }
                else
                {
                    // Непрерывный тон
                    double time = (double)_sampleCount / _sampleRate;
                    sample = (float)(0.3 * Math.Sin(2 * Math.PI * _frequency * time));
                }

                for (int channel = 0; channel < _channels; channel++)
                {
                    buffer[offset + i + channel] = sample;
                }

                _sampleCount++;
            }

            return count;
        }
    }
}

