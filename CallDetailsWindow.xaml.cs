using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;
using NAudio.Wave;
using Newtonsoft.Json;

namespace Softphone
{
    public partial class CallDetailsWindow : Window
    {
        private CallHistoryItem _callItem;
        private DispatcherTimer? _positionTimer;
        private bool _isDragging = false;
        
        // NAudio компоненты для воспроизведения
        private WaveOutEvent? _waveOut;
        private WaveFileReader? _waveReader;
        private WaveStream? _waveStream;
        private TimeSpan _totalDuration = TimeSpan.Zero;

        public CallDetailsWindow(CallHistoryItem callItem)
        {
            InitializeComponent();
            _callItem = callItem;
            
            // Логируем информацию о звонке для диагностики
            MainWindow.Log($"[CallDetailsWindow] Opening for call: PhoneNumber={callItem.PhoneNumber}, CallTime={callItem.CallTime:HH:mm:ss.fff}, " +
                $"RecordingFilePath={(string.IsNullOrEmpty(callItem.RecordingFilePath) ? "null" : callItem.RecordingFilePath)}, " +
                $"FileExists={(string.IsNullOrEmpty(callItem.RecordingFilePath) ? "N/A" : File.Exists(callItem.RecordingFilePath).ToString())}");
            
            LoadCallDetails();
            
            // Инициализируем таймер для обновления позиции
            _positionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _positionTimer.Tick += PositionTimer_Tick;
        }

        private void LoadCallDetails()
        {
            // Проверяем, включена ли запись звонков в настройках
            bool isCallRecordingEnabled = false;
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    isCallRecordingEnabled = settings?.EnableCallRecording ?? false;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error reading call recording setting: {ex.Message}");
            }
            
            // Скрываем раздел "Call Recording", если запись отключена в настройках
            if (!isCallRecordingEnabled)
            {
                CallRecordingHeader.Visibility = Visibility.Collapsed;
                RecordingBorder.Visibility = Visibility.Collapsed;
                MainWindow.Log("[CallDetailsWindow] Call recording is disabled in settings - hiding recording section");
            }
            else
            {
                CallRecordingHeader.Visibility = Visibility.Visible;
            }
            
            // Call Information
            PhoneNumberTextBlock.Text = _callItem.PhoneNumber;
            CallTimeTextBlock.Text = _callItem.CallTime.ToString("yyyy-MM-dd HH:mm:ss");
            DirectionTextBlock.Text = _callItem.IsIncoming ? "Incoming" : "Outgoing";
            StatusTextBlock.Text = GetStatusText(_callItem.Status);
            StatusTextBlock.Foreground = GetStatusColor(_callItem.Status);
            
            if (_callItem.Duration.HasValue)
            {
                var duration = _callItem.Duration.Value;
                DurationTextBlock.Text = $"{duration.Hours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
            }
            else
            {
                DurationTextBlock.Text = "N/A";
            }

            WasAnsweredTextBlock.Text = _callItem.WasAnswered ? "Yes" : "No";
            WasAnsweredTextBlock.Foreground = _callItem.WasAnswered 
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94)) // Green
                : new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red

            // Call Ended By
            EndedByTextBlock.Text = GetEndedByText(_callItem.EndedBy);
            EndedByTextBlock.Foreground = GetEndedByColor(_callItem.EndedBy);

            // Timing Information
            if (_callItem.RingbackStartTime.HasValue && _callItem.RingbackEndTime.HasValue)
            {
                var ringbackDuration = _callItem.RingbackEndTime.Value - _callItem.RingbackStartTime.Value;
                RingbackDurationTextBlock.Text = $"{ringbackDuration.TotalSeconds:F1} seconds";
                RingbackStartTextBlock.Text = _callItem.RingbackStartTime.Value.ToString("HH:mm:ss.fff");
            }
            else if (_callItem.RingbackStartTime.HasValue)
            {
                RingbackDurationTextBlock.Text = "In progress...";
                RingbackStartTextBlock.Text = _callItem.RingbackStartTime.Value.ToString("HH:mm:ss.fff");
            }
            else
            {
                RingbackDurationTextBlock.Text = "N/A";
                RingbackStartTextBlock.Text = "N/A";
            }

            if (_callItem.AnswerTime.HasValue)
            {
                AnswerTimeTextBlock.Text = _callItem.AnswerTime.Value.ToString("HH:mm:ss.fff");
            }
            else
            {
                AnswerTimeTextBlock.Text = "N/A";
            }

            // Call Recording
            // Показываем раздел записи только если запись включена в настройках
            if (isCallRecordingEnabled)
            {
                // ВАЖНО: Проверяем путь к записи, даже если файл еще не создан (конвертация через ffmpeg может быть асинхронной)
                if (!string.IsNullOrEmpty(_callItem.RecordingFilePath))
            {
                // Проверяем, существует ли файл
                bool fileExists = System.IO.File.Exists(_callItem.RecordingFilePath);
                
                if (fileExists)
                {
                    RecordingBorder.Visibility = Visibility.Visible;
                    MainWindow.Log($"[CallDetailsWindow] Recording file found: {_callItem.RecordingFilePath}");
                }
                else
                {
                    // Файл еще не создан (конвертация через ffmpeg может быть в процессе)
                    // Показываем секцию записи, но с сообщением о том, что запись обрабатывается
                    RecordingBorder.Visibility = Visibility.Visible;
                    MainWindow.Log($"[CallDetailsWindow] Recording file path set but file not yet created (may be converting): {_callItem.RecordingFilePath}");
                    
                    // Отключаем элементы воспроизведения, пока файл не готов
                    PlayButton.IsEnabled = false;
                    PositionSlider.IsEnabled = false;
                    TimeTextBlock.Text = "Recording is being processed...";
                    
                    // НЕ выходим - продолжаем загрузку остальных деталей
                    // Файл может появиться позже, и пользователь сможет обновить окно
                }
                
                // Проверяем формат файла - NAudio не поддерживает WebM напрямую
                // Но WebRTC записи должны быть автоматически сконвертированы в WAV через ffmpeg
                bool isWebM = _callItem.RecordingFilePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
                if (isWebM)
                {
                    // Если файл WebM, значит конвертация не удалась (ffmpeg не найден или ошибка)
                    MainWindow.Log($"[CallDetailsWindow] WARNING: Recording is WebM format - NAudio cannot play WebM. " +
                        $"WebRTC conversion to WAV may have failed (check ffmpeg availability).");
                    // Показываем предупреждение пользователю только один раз
                    CustomMessageBox.Show(
                        "This recording is in WebM format. NAudio cannot play WebM files.\n\n" +
                        "WebRTC recordings are normally converted to WAV automatically.\n" +
                        "The conversion may have failed (ffmpeg not found or error occurred).\n\n" +
                        "Please use an external media player (e.g., Windows Media Player) to play this file.",
                        "WebM Format Not Supported",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    RecordingBorder.Visibility = Visibility.Collapsed;
                    StopPlayback();
                    return;
                }
                
                // Инициализируем NAudio для воспроизведения WAV файлов
                // (SIP записи всегда WAV, WebRTC записи должны быть сконвертированы в WAV)
                try
                {
                    // Закрываем предыдущий reader, если есть
                    StopPlayback();
                    
                    // Диагностика: проверяем файл напрямую перед открытием через NAudio
                    try
                    {
                        long fileSize = new FileInfo(_callItem.RecordingFilePath).Length;
                        MainWindow.Log($"[CallDetailsWindow] File size: {fileSize} bytes");
                        
                        // Читаем заголовок и начало данных
                        byte[] fileHeader = new byte[Math.Min(100, (int)fileSize)];
                        using (var fs = new FileStream(_callItem.RecordingFilePath, FileMode.Open, FileAccess.Read))
                        {
                            int headerBytesRead = fs.Read(fileHeader, 0, fileHeader.Length);
                            if (headerBytesRead > 0)
                            {
                                string headerHex = string.Join(" ", fileHeader.Take(headerBytesRead).Select(b => b.ToString("X2")));
                                MainWindow.Log($"[CallDetailsWindow] File header (first {headerBytesRead} bytes, hex): {headerHex}");
                                
                                // Проверяем, что это WAV файл (должен начинаться с "RIFF")
                                if (headerBytesRead >= 4)
                                {
                                    string riffHeader = System.Text.Encoding.ASCII.GetString(fileHeader, 0, 4);
                                    MainWindow.Log($"[CallDetailsWindow] File header signature: '{riffHeader}' (should be 'RIFF' for WAV)");
                                }
                            }
                            
                            // Ищем начало данных (после "data" chunk)
                            // WAV структура: RIFF header (12 bytes) + fmt chunk + data chunk
                            // Ищем "data" (64 61 74 61) и читаем данные после него
                            fs.Position = 0;
                            byte[] searchBuffer = new byte[Math.Min(1000, (int)fileSize)];
                            int searchBytesRead = fs.Read(searchBuffer, 0, searchBuffer.Length);
                            
                            // Ищем "data" chunk
                            int dataChunkOffset = -1;
                            for (int i = 0; i <= searchBytesRead - 4; i++)
                            {
                                if (searchBuffer[i] == 0x64 && searchBuffer[i+1] == 0x61 && 
                                    searchBuffer[i+2] == 0x74 && searchBuffer[i+3] == 0x61)
                                {
                                    dataChunkOffset = i + 8; // Пропускаем "data" (4 bytes) + размер (4 bytes)
                                    break;
                                }
                            }
                            
                            if (dataChunkOffset > 0 && dataChunkOffset < fileSize)
                            {
                                fs.Position = dataChunkOffset;
                                byte[] dataStart = new byte[Math.Min(100, (int)(fileSize - dataChunkOffset))];
                                int dataBytesRead = fs.Read(dataStart, 0, dataStart.Length);
                                
                                if (dataBytesRead > 0)
                                {
                                    string dataHex = string.Join(" ", dataStart.Take(dataBytesRead).Select(b => b.ToString("X2")));
                                    MainWindow.Log($"[CallDetailsWindow] PCM data start (offset {dataChunkOffset}, first {dataBytesRead} bytes, hex): {dataHex}");
                                    
                                    // Проверяем, есть ли ненулевые байты
                                    int nonZeroCount = dataStart.Count(b => b != 0);
                                    MainWindow.Log($"[CallDetailsWindow] PCM data analysis: {nonZeroCount}/{dataBytesRead} bytes are non-zero");
                                    
                                    if (nonZeroCount == 0)
                                    {
                                        MainWindow.Log($"[CallDetailsWindow] CRITICAL: All PCM data bytes are zero! File is silent.");
                                    }
                                }
                            }
                            else
                            {
                                MainWindow.Log($"[CallDetailsWindow] WARNING: Could not find 'data' chunk in WAV file");
                            }
                        }
                    }
                    catch (Exception headerEx)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Error reading file header: {headerEx.Message}, StackTrace: {headerEx.StackTrace}");
                    }
                    
                    // Открываем WAV файл через NAudio
                    _waveReader = new WaveFileReader(_callItem.RecordingFilePath);
                    _waveStream = _waveReader;
                    
                    // Получаем длительность
                    if (_waveReader != null)
                    {
                        _totalDuration = _waveReader.TotalTime;
                        PositionSlider.Minimum = 0;
                        PositionSlider.Maximum = _totalDuration.TotalSeconds;
                        PositionSlider.SmallChange = 1.0;
                        PositionSlider.LargeChange = 5.0;
                        PositionSlider.Value = 0;
                        UpdateTimeDisplay(TimeSpan.Zero, _totalDuration);
                        
                        // Диагностика: проверяем уровень PCM в файле (читаем из разных мест)
                        try
                        {
                            long originalPosition = _waveReader.Position;
                            
                            // Проверяем несколько позиций в файле (начало, середина, конец)
                            int[] checkPositions = { 0, (int)(_waveReader.Length / 2), (int)(_waveReader.Length - 10000) };
                            checkPositions[2] = Math.Max(0, checkPositions[2]); // Убеждаемся, что не отрицательное
                            
                            foreach (int checkPos in checkPositions)
                            {
                                if (checkPos < 0 || checkPos >= _waveReader.Length) continue;
                                
                                _waveReader.Position = checkPos;
                                
                                // Читаем 0.5 секунды для анализа
                                int samplesToRead = (int)(_waveReader.WaveFormat.SampleRate * 0.5 * _waveReader.WaveFormat.Channels);
                                int bytesToRead = samplesToRead * (_waveReader.WaveFormat.BitsPerSample / 8);
                                bytesToRead = Math.Min(bytesToRead, (int)(_waveReader.Length - checkPos));
                                
                                if (bytesToRead <= 0) continue;
                                
                                byte[] sampleBuffer = new byte[bytesToRead];
                                int bytesRead = _waveReader.Read(sampleBuffer, 0, bytesToRead);
                                
                                // Анализируем PCM уровни
                                if (bytesRead > 0 && _waveReader.WaveFormat.BitsPerSample == 16)
                                {
                                    short[] samples = new short[bytesRead / 2];
                                    Buffer.BlockCopy(sampleBuffer, 0, samples, 0, bytesRead);
                                    
                                    int peak = 0;
                                    long sumSquares = 0;
                                    int nonZeroCount = 0;
                                    int maxAbs = 0;
                                    
                                    foreach (short sample in samples)
                                    {
                                        int abs = Math.Abs(sample);
                                        if (abs > peak) peak = abs;
                                        if (abs > maxAbs) maxAbs = abs;
                                        sumSquares += (long)sample * sample;
                                        if (abs > 10) nonZeroCount++;
                                    }
                                    
                                    int rms = samples.Length > 0 ? (int)Math.Sqrt(sumSquares / samples.Length) : 0;
                                    double nonZeroPercent = samples.Length > 0 ? (nonZeroCount * 100.0) / samples.Length : 0;
                                    
                                    double positionSeconds = checkPos / (double)_waveReader.WaveFormat.AverageBytesPerSecond;
                                    MainWindow.Log($"[CallDetailsWindow] PCM Analysis at {positionSeconds:F2}s (pos={checkPos}): " +
                                        $"Peak={peak}, RMS={rms}, NonZero={nonZeroPercent:F1}%, Samples={samples.Length}, " +
                                        $"FirstSample={(samples.Length > 0 ? samples[0] : 0)}, LastSample={(samples.Length > 0 ? samples[samples.Length-1] : 0)}");
                                    
                                    // Также проверяем первые байты в hex для диагностики
                                    if (checkPos == 0 && sampleBuffer.Length >= 20)
                                    {
                                        string hexPreview = string.Join(" ", sampleBuffer.Take(20).Select(b => b.ToString("X2")));
                                        MainWindow.Log($"[CallDetailsWindow] First 20 bytes (hex): {hexPreview}");
                                    }
                                }
                            }
                            
                            // Восстанавливаем позицию
                            _waveReader.Position = originalPosition;
                        }
                        catch (Exception pcmEx)
                        {
                            MainWindow.Log($"[CallDetailsWindow] Error analyzing PCM levels: {pcmEx.Message}, StackTrace: {pcmEx.StackTrace}");
                        }
                        
                        MainWindow.Log($"[CallDetailsWindow] NAudio: File loaded, duration: {_totalDuration.TotalSeconds:F2} seconds, " +
                            $"format: {_waveReader.WaveFormat.SampleRate}Hz, {_waveReader.WaveFormat.Channels}ch, {_waveReader.WaveFormat.BitsPerSample}bit, " +
                            $"Length={_waveReader.Length} bytes");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error loading recording with NAudio: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"Error loading recording: {ex.Message}");
                    CustomMessageBox.Show(
                        $"Error loading recording file:\n{ex.Message}\n\n" +
                        "The file may be corrupted or in an unsupported format.",
                        "Error Loading Recording",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                    RecordingBorder.Visibility = Visibility.Collapsed;
                    StopPlayback();
                }
            }
                else
                {
                    RecordingBorder.Visibility = Visibility.Collapsed;
                    StopPlayback();
                }
            }
            else
            {
                // Запись отключена в настройках - скрываем раздел
                RecordingBorder.Visibility = Visibility.Collapsed;
                StopPlayback();
            }

            // Technical Details
            if (_callItem.TechnicalDetails != null && _callItem.TechnicalDetails.Count > 0)
            {
                TechnicalDetailsTextBlock.Text = string.Join("\n", _callItem.TechnicalDetails);
            }
            else
            {
                TechnicalDetailsTextBlock.Text = "No technical details available.";
            }

            if (!string.IsNullOrEmpty(_callItem.ErrorMessage))
            {
                TechnicalDetailsTextBlock.Text += $"\n\nError: {_callItem.ErrorMessage}";
            }
        }
        
        private void PlayRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_waveReader == null)
                {
                    MainWindow.Log($"[CallDetailsWindow] Play clicked but waveReader is null");
                    return;
                }
                
                if (PlayButtonContent.Text == "▶")
                {
                    MainWindow.Log($"[CallDetailsWindow] Play clicked: File={_callItem.RecordingFilePath}, Duration={_totalDuration.TotalSeconds:F2}s");
                    
                    // Получаем устройство вывода из настроек или используем устройство по умолчанию (0)
                    int outputDeviceNumber = 0;
                    try
                    {
                        string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                        if (File.Exists(settingsFilePath))
                        {
                            string json = File.ReadAllText(settingsFilePath);
                            var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                            if (settings?.SpeakerDeviceNumber.HasValue == true)
                            {
                                outputDeviceNumber = settings.SpeakerDeviceNumber.Value;
                                MainWindow.Log($"[CallDetailsWindow] Using speaker device from settings: {outputDeviceNumber}");
                            }
                            else
                            {
                                MainWindow.Log($"[CallDetailsWindow] No speaker device in settings, using default: {outputDeviceNumber}");
                            }
                        }
                        else
                        {
                            MainWindow.Log($"[CallDetailsWindow] Settings file not found, using default output device: {outputDeviceNumber}");
                        }
                        
                        // Проверяем, что устройство существует
                        if (outputDeviceNumber < 0 || outputDeviceNumber >= WaveOut.DeviceCount)
                        {
                            MainWindow.Log($"[CallDetailsWindow] WARNING: Invalid device number {outputDeviceNumber}, using default (0)");
                            outputDeviceNumber = 0;
                        }
                        
                        // Логируем информацию об устройстве
                        if (WaveOut.DeviceCount > 0 && outputDeviceNumber < WaveOut.DeviceCount)
                        {
                            var deviceCapabilities = WaveOut.GetCapabilities(outputDeviceNumber);
                            MainWindow.Log($"[CallDetailsWindow] Output device: {deviceCapabilities.ProductName} (Device #{outputDeviceNumber})");
                        }
                    }
                    catch (Exception deviceEx)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Error getting output device: {deviceEx.Message}, using default");
                        outputDeviceNumber = 0;
                    }
                    
                    // Создаем WaveOutEvent с явным указанием устройства вывода
                    if (_waveOut == null)
                    {
                        _waveOut = new WaveOutEvent
                        {
                            DeviceNumber = outputDeviceNumber
                        };
                        _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                        MainWindow.Log($"[CallDetailsWindow] WaveOutEvent created with device #{outputDeviceNumber}");
                    }
                    else
                    {
                        // Если устройство изменилось, пересоздаем WaveOutEvent
                        if (_waveOut.DeviceNumber != outputDeviceNumber)
                        {
                            _waveOut.Stop();
                            _waveOut.Dispose();
                            _waveOut = new WaveOutEvent
                            {
                                DeviceNumber = outputDeviceNumber
                            };
                            _waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
                            MainWindow.Log($"[CallDetailsWindow] WaveOutEvent recreated with device #{outputDeviceNumber}");
                        }
                    }
                    
                    // Если reader был закрыт или позиция изменилась, переоткрываем файл
                    if (_waveReader == null || _waveReader.Position >= _waveReader.Length)
                    {
                        _waveReader?.Dispose();
                        _waveReader = new WaveFileReader(_callItem.RecordingFilePath);
                        _waveStream = _waveReader;
                        MainWindow.Log($"[CallDetailsWindow] WaveFileReader reopened: Length={_waveReader.Length} bytes, " +
                            $"Format={_waveReader.WaveFormat.SampleRate}Hz/{_waveReader.WaveFormat.Channels}ch/{_waveReader.WaveFormat.BitsPerSample}bit");
                    }
                    
                    // Устанавливаем позицию, если нужно (из слайдера)
                    if (PositionSlider.Value > 0)
                    {
                        TimeSpan seekPosition = TimeSpan.FromSeconds(PositionSlider.Value);
                        long seekPositionBytes = (long)(seekPosition.TotalSeconds * _waveReader.WaveFormat.AverageBytesPerSecond);
                        seekPositionBytes = seekPositionBytes - (seekPositionBytes % _waveReader.WaveFormat.BlockAlign);
                        _waveReader.Position = Math.Min(seekPositionBytes, _waveReader.Length);
                        MainWindow.Log($"[CallDetailsWindow] Seeking to position: {seekPosition.TotalSeconds:F2}s ({seekPositionBytes} bytes)");
                    }
                    
                    // Инициализируем и запускаем воспроизведение
                    _waveOut.Init(_waveReader);
                    _waveOut.Volume = 1.0f; // Максимальная громкость
                    
                    MainWindow.Log($"[CallDetailsWindow] Before Play: DeviceNumber={_waveOut.DeviceNumber}, " +
                        $"Volume={_waveOut.Volume}, PlaybackState={_waveOut.PlaybackState}, " +
                        $"ReaderPosition={_waveReader.Position}/{_waveReader.Length}, " +
                        $"Format={_waveReader.WaveFormat}");
                    
                    _waveOut.Play();
                    
                    PlayButtonContent.Text = "⏸";
                    _positionTimer?.Start();
                    
                    // Проверяем состояние после запуска
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MainWindow.Log($"[CallDetailsWindow] After Play: PlaybackState={_waveOut?.PlaybackState}, " +
                            $"Volume={_waveOut?.Volume}, DeviceNumber={_waveOut?.DeviceNumber}");
                    }), System.Windows.Threading.DispatcherPriority.Loaded, null);
                }
                else
                {
                    // Пауза или остановка
                    if (_waveOut != null)
                    {
                        if (_waveOut.PlaybackState == PlaybackState.Playing)
                        {
                            _waveOut.Pause();
                            PlayButtonContent.Text = "▶";
                            _positionTimer?.Stop();
                            MainWindow.Log($"[CallDetailsWindow] Playback paused");
                        }
                        else if (_waveOut.PlaybackState == PlaybackState.Paused)
                        {
                            // Возобновляем с текущей позиции (в NAudio нужно просто вызвать Play())
                            _waveOut.Play();
                            PlayButtonContent.Text = "⏸";
                            _positionTimer?.Start();
                            MainWindow.Log($"[CallDetailsWindow] Playback resumed");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error playing recording: {ex.Message}, StackTrace: {ex.StackTrace}");
                CustomMessageBox.Show($"Error playing recording: {ex.Message}", "Error", 
                    MessageBoxButton.OK, MessageBoxImage.Warning, this);
                PlayButtonContent.Text = "▶";
                _positionTimer?.Stop();
                StopPlayback();
            }
        }
        
        private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                PlayButtonContent.Text = "▶";
                _positionTimer?.Stop();
                
                if (e.Exception != null)
                {
                    MainWindow.Log($"[CallDetailsWindow] Playback stopped with error: {e.Exception.Message}");
                }
                else
                {
                    // Воспроизведение завершено нормально
                    PositionSlider.Value = PositionSlider.Maximum;
                    UpdateTimeDisplay(_totalDuration, _totalDuration);
                    MainWindow.Log($"[CallDetailsWindow] Playback completed");
                }
            }));
        }
        
        
        private void PositionTimer_Tick(object? sender, EventArgs e)
        {
            // Обновляем позицию слайдера только если не перетаскиваем (чтобы избежать конфликтов)
            if (!_isDragging && _waveReader != null && _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing)
            {
                try
                {
                    // Получаем текущую позицию из WaveReader
                    long currentBytes = _waveReader.Position;
                    double currentSeconds = currentBytes / (double)_waveReader.WaveFormat.AverageBytesPerSecond;
                    
                    // Проверяем, что позиция изменилась (чтобы избежать лишних обновлений)
                    if (Math.Abs(PositionSlider.Value - currentSeconds) > 0.1)
                    {
                        PositionSlider.Value = Math.Max(0, Math.Min(currentSeconds, PositionSlider.Maximum));
                        UpdateTimeDisplay(TimeSpan.FromSeconds(currentSeconds), _totalDuration);
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error updating position in timer: {ex.Message}");
                }
            }
        }
        
        private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
        }
        
        private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                TimeSpan newPosition = TimeSpan.FromSeconds(e.NewValue);
                
                // Обновляем отображение времени
                UpdateTimeDisplay(newPosition, _totalDuration);
                
                // Устанавливаем позицию сразу при изменении слайдера (если перетаскиваем)
                if (_isDragging && _waveReader != null)
                {
                    try
                    {
                        // Вычисляем позицию в байтах
                        long seekPositionBytes = (long)(newPosition.TotalSeconds * _waveReader.WaveFormat.AverageBytesPerSecond);
                        seekPositionBytes = seekPositionBytes - (seekPositionBytes % _waveReader.WaveFormat.BlockAlign);
                        seekPositionBytes = Math.Max(0, Math.Min(seekPositionBytes, _waveReader.Length));
                        
                        // Если воспроизведение активно, останавливаем, меняем позицию и возобновляем
                        bool wasPlaying = _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;
                        if (wasPlaying)
                        {
                            _waveOut?.Stop();
                        }
                        
                        _waveReader.Position = seekPositionBytes;
                        
                        if (wasPlaying && _waveOut != null)
                        {
                            _waveOut.Init(_waveReader);
                            _waveOut.Play();
                        }
                        
                        MainWindow.Log($"[CallDetailsWindow] Position set to: {newPosition.TotalSeconds:F2}s ({seekPositionBytes} bytes)");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Error setting position: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error in ValueChanged: {ex.Message}");
            }
        }
        
        private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            if (_waveReader != null)
            {
                try
                {
                    TimeSpan newPosition = TimeSpan.FromSeconds(PositionSlider.Value);
                    
                    // Вычисляем позицию в байтах
                    long seekPositionBytes = (long)(newPosition.TotalSeconds * _waveReader.WaveFormat.AverageBytesPerSecond);
                    seekPositionBytes = seekPositionBytes - (seekPositionBytes % _waveReader.WaveFormat.BlockAlign);
                    seekPositionBytes = Math.Max(0, Math.Min(seekPositionBytes, _waveReader.Length));
                    
                    // Если воспроизведение активно, останавливаем, меняем позицию и возобновляем
                    bool wasPlaying = _waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing;
                    if (wasPlaying)
                    {
                        _waveOut?.Stop();
                    }
                    
                    _waveReader.Position = seekPositionBytes;
                    
                    if (wasPlaying && _waveOut != null)
                    {
                        _waveOut.Init(_waveReader);
                        _waveOut.Play();
                    }
                    
                    UpdateTimeDisplay(newPosition, _totalDuration);
                    MainWindow.Log($"[CallDetailsWindow] Position set on mouse up: {newPosition.TotalSeconds:F2}s");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error setting position on mouse up: {ex.Message}");
                }
            }
        }
        
        private void StopPlayback()
        {
            try
            {
                _waveOut?.Stop();
                _waveOut?.Dispose();
                _waveOut = null;
                
                _waveReader?.Dispose();
                _waveReader = null;
                _waveStream = null;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error stopping playback: {ex.Message}");
            }
        }
        
        private void UpdateTimeDisplay(TimeSpan current, TimeSpan total)
        {
            try
            {
                if (total.TotalSeconds > 0)
                {
                    TimeTextBlock.Text = $"{current.Minutes:D2}:{current.Seconds:D2} / {total.Minutes:D2}:{total.Seconds:D2}";
                }
                else
                {
                    // Если продолжительность неизвестна, показываем только текущее время
                    TimeTextBlock.Text = $"{current.Minutes:D2}:{current.Seconds:D2} / --:--";
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error updating time display: {ex.Message}");
            }
        }
        
        protected override void OnClosed(EventArgs e)
        {
            // Останавливаем таймер
            _positionTimer?.Stop();
            _positionTimer = null;
            
            // Останавливаем и очищаем NAudio компоненты
            StopPlayback();
            
            base.OnClosed(e);
        }

        private string GetStatusText(CallStatus status)
        {
            return status switch
            {
                CallStatus.Calling => "Calling...",
                CallStatus.Connected => "Connected",
                CallStatus.Ended => "Ended",
                CallStatus.Failed => "Failed",
                CallStatus.Cancelled => "Cancelled",
                CallStatus.Missed => "Missed",
                _ => "Unknown"
            };
        }

        private Brush GetStatusColor(CallStatus status)
        {
            return status switch
            {
                CallStatus.Connected => new SolidColorBrush(Color.FromRgb(34, 197, 94)), // Green
                CallStatus.Ended => new SolidColorBrush(Color.FromRgb(156, 163, 175)), // Gray
                CallStatus.Failed => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Cancelled => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Missed => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                CallStatus.Calling => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                _ => new SolidColorBrush(Color.FromRgb(156, 163, 175)) // Gray
            };
        }

        private string GetEndedByText(CallEndedBy endedBy)
        {
            return endedBy switch
            {
                CallEndedBy.LocalUser => "Local User (You)",
                CallEndedBy.RemoteParty => "Remote Party",
                _ => "Unknown"
            };
        }

        private Brush GetEndedByColor(CallEndedBy endedBy)
        {
            return endedBy switch
            {
                CallEndedBy.LocalUser => new SolidColorBrush(Color.FromRgb(59, 130, 246)), // Blue
                CallEndedBy.RemoteParty => new SolidColorBrush(Color.FromRgb(239, 68, 68)), // Red
                _ => new SolidColorBrush(Color.FromRgb(156, 163, 175)) // Gray
            };
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void CopyTechnicalDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string textToCopy = TechnicalDetailsTextBlock.Text;
                if (!string.IsNullOrEmpty(textToCopy) && textToCopy != "No technical details available.")
                {
                    Clipboard.SetText(textToCopy);
                    // Можно показать уведомление, но для простоты просто копируем
                }
            }
            catch (Exception ex)
            {
                // Игнорируем ошибки копирования
                System.Diagnostics.Debug.WriteLine($"Error copying technical details: {ex.Message}");
            }
        }
    }
}

