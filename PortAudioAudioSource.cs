using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using System.Runtime.InteropServices;

namespace Softphone
{
    /// <summary>
    /// Адаптер PortAudio для IAudioSource
    /// Использует PortAudioNet для захвата аудио с микрофона с низкой задержкой
    /// </summary>
    public class PortAudioAudioSource : IAudioSource, IDisposable
    {
        private readonly int _deviceIndex;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly float _gainMultiplier;
        
        private IntPtr _stream = IntPtr.Zero;
        private bool _isInitialized = false;
        private bool _isStarted = false;
        private bool _isPaused = false;
        private bool _isDisposed = false;
        
        private readonly object _lockObject = new object();
        private CancellationTokenSource? _cancellationTokenSource;
        private Task? _audioCaptureTask;
        
        private AudioFormat? _currentFormat;
        private readonly List<AudioFormat> _supportedFormats = new List<AudioFormat>();
        
        public event EncodedSampleDelegate? OnAudioSourceEncodedSample;
        public event RawAudioSampleDelegate? OnAudioSourceRawSample;
        public event SourceErrorDelegate? OnAudioSourceError;

        public PortAudioAudioSource(int deviceIndex = -1, int sampleRate = 8000, int channels = 1, float gainMultiplier = 1.0f)
        {
            _deviceIndex = deviceIndex;
            _sampleRate = sampleRate; // По умолчанию 8000 Hz для G.711
            _channels = channels;
            _gainMultiplier = Math.Max(1.0f, Math.Min(5.0f, gainMultiplier));
            
            InitializeSupportedFormats();
        }

        private void InitializeSupportedFormats()
        {
            // Для G.711 строго 8000 Hz, mono, 16-bit PCM
            // Это формат, совместимый с PCMU/PCMA
            try
            {
                // Убеждаемся, что используем 8000 Hz для G.711
                int sampleRate = _sampleRate;
                if (sampleRate != 8000)
                {
                    sampleRate = 8000; // Принудительно 8000 Hz для G.711
                }
                
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMA, sampleRate, _channels));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing formats: {ex.Message}");
                try
                {
                    _supportedFormats.Clear();
                    _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, 8000, 1));
                }
                catch
                {
                    // Если даже PCMU не работает, оставляем список пустым
                }
            }
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
                throw new ObjectDisposedException(nameof(PortAudioAudioSource));

            lock (_lockObject)
            {
                if (_isStarted)
                    return Task.CompletedTask;

                try
                {
                    // Используем reflection для вызова методов PortAudioNet
                    // Пробуем разные варианты имен пространств имен
                    Type? portAudioType = Type.GetType("PortAudioSharp.PortAudio, PortAudioNet");
                    if (portAudioType == null)
                    {
                        portAudioType = Type.GetType("PortAudio.PortAudio, PortAudioNet");
                    }
                    if (portAudioType == null)
                    {
                        // Пробуем найти тип в загруженных сборках
                        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                        foreach (var assembly in assemblies)
                        {
                            if (assembly.FullName?.Contains("PortAudioNet") == true)
                            {
                                portAudioType = assembly.GetType("PortAudioSharp.PortAudio") 
                                             ?? assembly.GetType("PortAudio.PortAudio");
                                if (portAudioType != null) break;
                            }
                        }
                    }
                    
                    if (portAudioType == null)
                    {
                        OnAudioSourceError?.Invoke("PortAudioNet assembly not found. Please ensure PortAudioNet package is installed.");
                        return Task.CompletedTask;
                    }

                    // Инициализируем PortAudio
                    var initializeMethod = portAudioType.GetMethod("Pa_Initialize");
                    if (initializeMethod == null)
                    {
                        OnAudioSourceError?.Invoke("Pa_Initialize method not found");
                        return Task.CompletedTask;
                    }

                    var initResult = initializeMethod.Invoke(null, null);
                    if (initResult == null || !IsSuccess(initResult))
                    {
                        OnAudioSourceError?.Invoke($"Failed to initialize PortAudio: {initResult}");
                        return Task.CompletedTask;
                    }

                    _isInitialized = true;

                    // Получаем устройство по умолчанию, если не указано
                    int inputDevice = _deviceIndex;
                    if (inputDevice < 0)
                    {
                        var getDefaultInputMethod = portAudioType.GetMethod("Pa_GetDefaultInputDevice");
                        if (getDefaultInputMethod != null)
                        {
                            var defaultDevice = getDefaultInputMethod.Invoke(null, null);
                            if (defaultDevice != null)
                            {
                                inputDevice = Convert.ToInt32(defaultDevice);
                            }
                        }
                    }

                    if (inputDevice < 0)
                    {
                        OnAudioSourceError?.Invoke("No input device available");
                        return Task.CompletedTask;
                    }

                    // Настраиваем параметры потока
                    var streamParamsType = Type.GetType("PortAudioSharp.PaStreamParameters, PortAudioNet");
                    if (streamParamsType == null)
                    {
                        OnAudioSourceError?.Invoke("PaStreamParameters type not found");
                        return Task.CompletedTask;
                    }

                    var inputParams = Activator.CreateInstance(streamParamsType);
                    if (inputParams != null)
                    {
                        SetProperty(inputParams, "device", inputDevice);
                        SetProperty(inputParams, "channelCount", _channels);
                    }
                    else
                    {
                        OnAudioSourceError?.Invoke("Failed to create stream parameters");
                        return Task.CompletedTask;
                    }
                    
                    // Используем paInt16 для 16-bit PCM
                    var sampleFormatType = Type.GetType("PortAudioSharp.PaSampleFormat, PortAudioNet");
                    if (sampleFormatType != null)
                    {
                        var paInt16 = Enum.Parse(sampleFormatType, "paInt16");
                        SetProperty(inputParams, "sampleFormat", paInt16);
                    }

                    // Открываем поток
                    var openStreamMethod = portAudioType.GetMethod("Pa_OpenStream", new[] { typeof(IntPtr).MakeByRefType(), streamParamsType.MakeByRefType(), typeof(IntPtr), typeof(double), typeof(int), typeof(int), typeof(IntPtr), typeof(IntPtr) });
                    if (openStreamMethod == null)
                    {
                        OnAudioSourceError?.Invoke("Pa_OpenStream method not found");
                        return Task.CompletedTask;
                    }

                    // Для G.711 строго 8000 Hz
                    int sampleRate = _sampleRate;
                    if (sampleRate != 8000)
                    {
                        sampleRate = 8000;
                    }
                    
                    var openParams = new object[] { IntPtr.Zero, inputParams, IntPtr.Zero, (double)sampleRate, 0, 0, IntPtr.Zero, IntPtr.Zero };
                    var openResult = openStreamMethod.Invoke(null, openParams);
                    
                    if (openResult == null || !IsSuccess(openResult))
                    {
                        OnAudioSourceError?.Invoke($"Failed to open stream: {openResult}");
                        return Task.CompletedTask;
                    }

                    _stream = (IntPtr)openParams[0];

                    // Запускаем поток
                    var startStreamMethod = portAudioType.GetMethod("Pa_StartStream");
                    if (startStreamMethod == null)
                    {
                        OnAudioSourceError?.Invoke("Pa_StartStream method not found");
                        return Task.CompletedTask;
                    }

                    var startResult = startStreamMethod.Invoke(null, new object[] { _stream });
                    if (startResult == null || !IsSuccess(startResult))
                    {
                        OnAudioSourceError?.Invoke($"Failed to start stream: {startResult}");
                        return Task.CompletedTask;
                    }

                    _isStarted = true;
                    _isPaused = false;

                    // Запускаем задачу для захвата аудио
                    _cancellationTokenSource = new CancellationTokenSource();
                    _audioCaptureTask = Task.Run(() => AudioCaptureLoop(_cancellationTokenSource.Token, portAudioType));
                    
                    // Небольшая задержка для инициализации потока
                    System.Threading.Thread.Sleep(50);
                }
                catch (Exception ex)
                {
                    OnAudioSourceError?.Invoke($"Error starting audio: {ex.Message}");
                    return Task.CompletedTask;
                }
            }
            
            return Task.CompletedTask;
        }

        private void AudioCaptureLoop(CancellationToken cancellationToken, Type portAudioType)
        {
            // Вычисляем размер буфера для нужной частоты дискретизации
            // Для VoIP обычно используется 20ms буфер
            int samplesPerBuffer = (_sampleRate * 20) / 1000; // 20ms при текущей частоте
            if (samplesPerBuffer < 160) samplesPerBuffer = 160; // Минимум для VoIP
            
            short[] buffer = new short[samplesPerBuffer * _channels];
            uint durationMs = 20; // 20ms буфер

            AudioSamplingRatesEnum samplingRateEnum = ConvertSampleRate(_sampleRate);

            try
            {
                var readStreamMethod = portAudioType.GetMethod("Pa_ReadStream");
                if (readStreamMethod == null)
                {
                    OnAudioSourceError?.Invoke("Pa_ReadStream method not found");
                    return;
                }

                // Небольшая задержка для инициализации
                Thread.Sleep(10);

                while (!cancellationToken.IsCancellationRequested && _isStarted && !_isPaused)
                {
                    if (_stream == IntPtr.Zero)
                        break;

                    try
                    {
                        // Читаем данные из потока
                        var readResult = readStreamMethod.Invoke(null, new object[] { _stream, buffer, samplesPerBuffer });
                        
                        if (IsSuccess(readResult))
                        {
                            // Применяем усиление
                            short[] amplifiedBuffer = ApplyGain(buffer);
                            
                            // Конвертируем в моно, если нужно
                            short[] monoBuffer = _channels > 1 ? ConvertToMono(amplifiedBuffer) : amplifiedBuffer;
                            
                            // Отправляем событие только если есть подписчики и данные не пустые
                            if (OnAudioSourceRawSample != null && monoBuffer.Length > 0)
                            {
                                OnAudioSourceRawSample.Invoke(samplingRateEnum, durationMs, monoBuffer);
                            }
                        }
                        else
                        {
                            // Логируем ошибки чтения, но продолжаем работу
                            System.Diagnostics.Debug.WriteLine($"PortAudio read error: {readResult}");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Логируем ошибку, но продолжаем работу
                        System.Diagnostics.Debug.WriteLine($"Error reading from PortAudio stream: {ex.Message}");
                    }

                    // Небольшая задержка для предотвращения перегрузки CPU
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                OnAudioSourceError?.Invoke($"Audio capture error: {ex.Message}");
            }
        }

        private bool IsSuccess(object? result)
        {
            if (result == null) return false;
            
            // Проверяем, является ли результат успешным кодом ошибки PortAudio
            var resultType = result.GetType();
            if (resultType.IsEnum)
            {
                var noError = Enum.Parse(resultType, "paNoError");
                return result.Equals(noError);
            }
            
            return Convert.ToInt32(result) == 0;
        }

        private void SetProperty(object obj, string propertyName, object value)
        {
            var prop = obj.GetType().GetProperty(propertyName);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(obj, value);
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
            // Для G.711 строго 8000 Hz
            int rate = sampleRate;
            if (rate != 8000)
            {
                rate = 8000;
            }
            
            return rate switch
            {
                8000 => AudioSamplingRatesEnum.Rate8KHz,
                16000 => AudioSamplingRatesEnum.Rate16KHz,
                _ => AudioSamplingRatesEnum.Rate8KHz // По умолчанию 8 кГц для G.711
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
            Task? captureTask = null;
            
            lock (_lockObject)
            {
                if (!_isStarted)
                    return Task.CompletedTask;

                _isStarted = false;
                _isPaused = false;

                // Останавливаем захват
                _cancellationTokenSource?.Cancel();
                
                if (_stream != IntPtr.Zero)
                {
                    try
                    {
                        var portAudioType = Type.GetType("PortAudioSharp.PortAudio, PortAudioNet");
                        if (portAudioType != null)
                        {
                            var stopMethod = portAudioType.GetMethod("Pa_StopStream");
                            var closeMethod = portAudioType.GetMethod("Pa_CloseStream");
                            
                            stopMethod?.Invoke(null, new object[] { _stream });
                            closeMethod?.Invoke(null, new object[] { _stream });
                        }
                    }
                    catch { }
                    _stream = IntPtr.Zero;
                }

                // Сохраняем ссылку на задачу для ожидания вне lock
                captureTask = _audioCaptureTask;
                _audioCaptureTask = null;

                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
            
            // Ждем завершения задачи захвата вне lock
            if (captureTask != null)
            {
                try
                {
                    return captureTask.ContinueWith(t => { }, TaskContinuationOptions.OnlyOnFaulted);
                }
                catch { }
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
                
                // Фильтруем только форматы с 8000 Hz и 1 channel для G.711
                safeFormats = safeFormats.Where(f => 
                {
                    try
                    {
                        // Получаем SampleRate через рефлексию
                        var sampleRateProperty = f.GetType().GetProperty("SampleRate");
                        var channelsProperty = f.GetType().GetProperty("Channels");
                        
                        if (sampleRateProperty != null && channelsProperty != null)
                        {
                            var sampleRate = Convert.ToInt32(sampleRateProperty.GetValue(f));
                            var channels = Convert.ToInt32(channelsProperty.GetValue(f));
                            return sampleRate == 8000 && channels == 1;
                        }
                        
                        // Если свойства недоступны, проверяем через другие свойства
                        var clockRateProperty = f.GetType().GetProperty("ClockRate");
                        if (clockRateProperty != null)
                        {
                            var clockRate = Convert.ToInt32(clockRateProperty.GetValue(f));
                            return clockRate == 8000;
                        }
                        
                        // Если ничего не найдено, считаем формат подходящим (форматы уже созданы с правильными параметрами)
                        return true;
                    }
                    catch
                    {
                        // В случае ошибки считаем формат подходящим
                        return true;
                    }
                }).ToList();
                
                // Если нет безопасных форматов, возвращаем PCMU 8000 Hz
                if (safeFormats.Count == 0)
                {
                    try
                    {
                        safeFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, 8000, 1));
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
            return OnAudioSourceEncodedSample != null;
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

            if (_isInitialized)
            {
                try
                {
                    var portAudioType = Type.GetType("PortAudioSharp.PortAudio, PortAudioNet");
                    if (portAudioType != null)
                    {
                        var terminateMethod = portAudioType.GetMethod("Pa_Terminate");
                        terminateMethod?.Invoke(null, null);
                    }
                }
                catch { }
                _isInitialized = false;
            }

            _isDisposed = true;
        }
    }
}

