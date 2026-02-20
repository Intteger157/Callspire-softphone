using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SIPSorceryMedia.Abstractions;
using System.Runtime.InteropServices;

namespace Softphone
{
    /// <summary>
    /// Адаптер PortAudio для IAudioSink
    /// Использует PortAudioNet для воспроизведения аудио с низкой задержкой
    /// </summary>
    public class PortAudioSink : IAudioSink, IDisposable
    {
        private readonly int _deviceIndex;
        private readonly int _sampleRate;
        private readonly int _channels;
        
        private IntPtr _stream = IntPtr.Zero;
        private bool _isStarted = false;
        private bool _isPaused = false;
        private bool _isDisposed = false;
        
        private readonly object _lockObject = new object();
        
        private AudioFormat? _currentFormat;
        private readonly List<AudioFormat> _supportedFormats = new List<AudioFormat>();
        
        public event SourceErrorDelegate? OnAudioSinkError;

        public PortAudioSink(int deviceIndex = -1, int sampleRate = 16000, int channels = 1)
        {
            _deviceIndex = deviceIndex;
            _sampleRate = sampleRate;
            _channels = channels;
            
            InitializeSupportedFormats();
        }

        private void InitializeSupportedFormats()
        {
            // Поддерживаемые форматы для VoIP
            try
            {
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.PCMA, _sampleRate, _channels));
                _supportedFormats.Add(new AudioFormat(AudioCodecsEnum.G722, _sampleRate, _channels));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing sink formats: {ex.Message}");
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
        }

        public void RestrictFormats(Func<AudioFormat, bool> filter)
        {
            lock (_lockObject)
            {
                _supportedFormats.RemoveAll(f => !filter(f));
            }
        }

        public void SetAudioSinkFormat(AudioFormat audioFormat)
        {
            lock (_lockObject)
            {
                // Проверяем, что формат имеет допустимый ID (<= 127)
                try
                {
                    var formatIdProperty = audioFormat.GetType().GetProperty("FormatID");
                    if (formatIdProperty != null)
                    {
                        var formatId = Convert.ToInt32(formatIdProperty.GetValue(audioFormat));
                        if (formatId > 127)
                        {
                            _currentFormat = new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels);
                            return;
                        }
                    }
                }
                catch
                {
                    _currentFormat = new AudioFormat(AudioCodecsEnum.PCMU, _sampleRate, _channels);
                    return;
                }
                
                _currentFormat = audioFormat;
            }
        }

        public Task StartAudioSink()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(PortAudioSink));

            lock (_lockObject)
            {
                if (_isStarted)
                    return Task.CompletedTask;

                try
                {
                    // Используем reflection для вызова методов PortAudioNet
                    Type? portAudioType = Type.GetType("PortAudioSharp.PortAudio, PortAudioNet");
                    if (portAudioType == null)
                    {
                        portAudioType = Type.GetType("PortAudio.PortAudio, PortAudioNet");
                    }
                    if (portAudioType == null)
                    {
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
                        OnAudioSinkError?.Invoke("PortAudioNet assembly not found");
                        return Task.CompletedTask;
                    }

                    // Инициализируем PortAudio
                    var initializeMethod = portAudioType.GetMethod("Pa_Initialize");
                    if (initializeMethod == null)
                    {
                        OnAudioSinkError?.Invoke("Pa_Initialize method not found");
                        return Task.CompletedTask;
                    }

                    var initResult = initializeMethod.Invoke(null, null);
                    if (initResult == null || !IsSuccess(initResult))
                    {
                        OnAudioSinkError?.Invoke($"Failed to initialize PortAudio: {initResult}");
                        return Task.CompletedTask;
                    }

                    // Получаем устройство по умолчанию для вывода
                    int outputDevice = _deviceIndex;
                    if (outputDevice < 0)
                    {
                        var getDefaultOutputMethod = portAudioType.GetMethod("Pa_GetDefaultOutputDevice");
                        if (getDefaultOutputMethod != null)
                        {
                            var defaultDevice = getDefaultOutputMethod.Invoke(null, null);
                            if (defaultDevice != null)
                            {
                                outputDevice = Convert.ToInt32(defaultDevice);
                            }
                        }
                    }

                    if (outputDevice < 0)
                    {
                        OnAudioSinkError?.Invoke("No output device available");
                        return Task.CompletedTask;
                    }

                    // Настраиваем параметры потока для вывода
                    var streamParamsType = Type.GetType("PortAudioSharp.PaStreamParameters, PortAudioNet");
                    if (streamParamsType == null)
                    {
                        OnAudioSinkError?.Invoke("PaStreamParameters type not found");
                        return Task.CompletedTask;
                    }

                    var outputParams = Activator.CreateInstance(streamParamsType);
                    if (outputParams != null)
                    {
                        SetProperty(outputParams, "device", outputDevice);
                        SetProperty(outputParams, "channelCount", _channels);
                    }
                    else
                    {
                        OnAudioSinkError?.Invoke("Failed to create stream parameters");
                        return Task.CompletedTask;
                    }
                    
                    var sampleFormatType = Type.GetType("PortAudioSharp.PaSampleFormat, PortAudioNet");
                    if (sampleFormatType != null)
                    {
                        var paInt16 = Enum.Parse(sampleFormatType, "paInt16");
                        SetProperty(outputParams, "sampleFormat", paInt16);
                    }

                    // Открываем поток для вывода
                    var openStreamMethod = portAudioType.GetMethod("Pa_OpenStream", new[] { typeof(IntPtr).MakeByRefType(), typeof(IntPtr), streamParamsType.MakeByRefType(), typeof(double), typeof(int), typeof(int), typeof(IntPtr), typeof(IntPtr) });
                    if (openStreamMethod == null)
                    {
                        OnAudioSinkError?.Invoke("Pa_OpenStream method not found");
                        return Task.CompletedTask;
                    }

                    var openParams = new object[] { IntPtr.Zero, IntPtr.Zero, outputParams, (double)_sampleRate, 0, 0, IntPtr.Zero, IntPtr.Zero };
                    var openResult = openStreamMethod.Invoke(null, openParams);
                    
                    if (openResult == null || !IsSuccess(openResult))
                    {
                        OnAudioSinkError?.Invoke($"Failed to open output stream: {openResult}");
                        return Task.CompletedTask;
                    }

                    _stream = (IntPtr)openParams[0];

                    // Запускаем поток
                    var startStreamMethod = portAudioType.GetMethod("Pa_StartStream");
                    if (startStreamMethod == null)
                    {
                        OnAudioSinkError?.Invoke("Pa_StartStream method not found");
                        return Task.CompletedTask;
                    }

                    var startResult = startStreamMethod.Invoke(null, new object[] { _stream });
                    if (startResult == null || !IsSuccess(startResult))
                    {
                        OnAudioSinkError?.Invoke($"Failed to start output stream: {startResult}");
                        return Task.CompletedTask;
                    }

                    _isStarted = true;
                    _isPaused = false;
                }
                catch (Exception ex)
                {
                    OnAudioSinkError?.Invoke($"Error starting audio sink: {ex.Message}");
                    return Task.CompletedTask;
                }
            }
            
            return Task.CompletedTask;
        }

        public Task PauseAudioSink()
        {
            lock (_lockObject)
            {
                _isPaused = true;
            }
            return Task.CompletedTask;
        }

        public Task ResumeAudioSink()
        {
            lock (_lockObject)
            {
                _isPaused = false;
            }
            return Task.CompletedTask;
        }

        public Task CloseAudioSink()
        {
            lock (_lockObject)
            {
                if (!_isStarted || _stream == IntPtr.Zero)
                    return Task.CompletedTask;

                try
                {
                    Type? portAudioType = Type.GetType("PortAudioSharp.PortAudio, PortAudioNet");
                    if (portAudioType == null)
                    {
                        portAudioType = Type.GetType("PortAudio.PortAudio, PortAudioNet");
                    }
                    if (portAudioType == null)
                    {
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

                    if (portAudioType != null)
                    {
                        var stopStreamMethod = portAudioType.GetMethod("Pa_StopStream");
                        if (stopStreamMethod != null)
                        {
                            stopStreamMethod.Invoke(null, new object[] { _stream });
                        }

                        var closeStreamMethod = portAudioType.GetMethod("Pa_CloseStream");
                        if (closeStreamMethod != null)
                        {
                            closeStreamMethod.Invoke(null, new object[] { _stream });
                        }

                        var terminateMethod = portAudioType.GetMethod("Pa_Terminate");
                        if (terminateMethod != null)
                        {
                            terminateMethod.Invoke(null, null);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error closing audio sink: {ex.Message}");
                }
                finally
                {
                    _stream = IntPtr.Zero;
                    _isStarted = false;
                    _isPaused = false;
                }
            }
            
            return Task.CompletedTask;
        }

        public List<AudioFormat> GetAudioSinkFormats()
        {
            lock (_lockObject)
            {
                // Фильтруем форматы с ID > 127
                return _supportedFormats.Where(f =>
                {
                    try
                    {
                        var formatIdProperty = f.GetType().GetProperty("FormatID");
                        if (formatIdProperty != null)
                        {
                            var formatId = Convert.ToInt32(formatIdProperty.GetValue(f));
                            return formatId <= 127;
                        }
                    }
                    catch
                    {
                        return false;
                    }
                    return true;
                }).ToList();
            }
        }

        public void GotAudioRtp(IPEndPoint remoteEndPoint, uint ssrc, uint seqnum, uint timestamp, int payloadID, bool marker, byte[] payload)
        {
            // Этот метод вызывается для RTP пакетов, но мы используем OnAudioSinkRawSample
            // Обычно VoIPMediaSession обрабатывает RTP и вызывает OnAudioSinkRawSample
        }

        public void GotAudioSample(AudioSamplingRatesEnum samplingRate, uint durationMilliseconds, short[] sample)
        {
            if (_isPaused || _isDisposed || !_isStarted || _stream == IntPtr.Zero)
                return;

            try
            {
                // Записываем данные в поток PortAudio
                Type? portAudioType = Type.GetType("PortAudioSharp.PortAudio, PortAudioNet");
                if (portAudioType == null)
                {
                    portAudioType = Type.GetType("PortAudio.PortAudio, PortAudioNet");
                }
                if (portAudioType == null)
                {
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

                if (portAudioType != null)
                {
                    var writeStreamMethod = portAudioType.GetMethod("Pa_WriteStream");
                    if (writeStreamMethod != null)
                    {
                        writeStreamMethod.Invoke(null, new object[] { _stream, sample, sample.Length });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error writing to PortAudio stream: {ex.Message}");
            }
        }

        private bool IsSuccess(object? result)
        {
            if (result == null) return false;
            
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

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                var t = CloseAudioSink();
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

