using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace Softphone
{
    public partial class CallDetailsWindow : Window
    {
        private CallHistoryItem _callItem;
        private bool _isWebMFile = false; // Флаг для WebM файлов (для логирования)
        private List<string> _allLogs = new List<string>();
        private List<string> _amoCrmLogs = new List<string>();
        private List<string> _webRtcLogs = new List<string>();
        private List<string> _sipLogs = new List<string>();
        private DispatcherTimer? _amoCrmRefreshTimer;
        private int _amoCrmRefreshCount = 0;
        private const int MaxAmoCrmRefreshTicks = 20; // 20 ticks × 2s = 40 seconds max
        private long? _amoCrmContactId = null; // ID контакта в AmoCRM для кнопки "Open Contact"

        public CallDetailsWindow(CallHistoryItem callItem)
        {
            InitializeComponent();
            NativeWindowAppearanceManager.Attach(this);
            _callItem = callItem;

            // Refresh from persisted call history to avoid showing stale/empty details.
            // The UI list items are not INotifyPropertyChanged, so they may not reflect the latest UpdateCallDetails writes.
            try
            {
                var history = new CallHistoryService();
                var latest = history.GetCall(callItem.PhoneNumber, callItem.CallTime);
                if (latest != null)
                {
                    _callItem = latest;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] WARNING: Failed to refresh call item from history: {ex.Message}");
            }
            
            // Логируем информацию о звонке для диагностики
            MainWindow.Log($"[CallDetailsWindow] Opening for call: PhoneNumber={callItem.PhoneNumber}, CallTime={callItem.CallTime:HH:mm:ss.fff}, " +
                $"RecordingFilePath={(string.IsNullOrEmpty(callItem.RecordingFilePath) ? "null" : callItem.RecordingFilePath)}, " +
                $"FileExists={(string.IsNullOrEmpty(callItem.RecordingFilePath) ? "N/A" : File.Exists(callItem.RecordingFilePath).ToString())}");
            
            LoadCallDetails();
            
            // Если AmoCRM статус ещё не финальный — запускаем таймер,
            // чтобы обновить поля когда фоновая обработка завершится.
            if (_callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.NotUploaded && !_callItem.AmoCrmLeadId.HasValue)
            {
                StartAmoCrmRefreshTimer();
            }
            
            this.Closed += (s, e) => StopAmoCrmRefreshTimer();
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
                RecordingBorder.Visibility = Visibility.Collapsed;
                MainWindow.Log("[CallDetailsWindow] Call recording is disabled in settings - hiding recording section");
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

            // Record added to (lead ID или Contact)
            if (_callItem.AmoCrmLeadId.HasValue)
            {
                // Запись загружена в лид
                AmoCrmLeadTextBlock.Text = $"Lead #{_callItem.AmoCrmLeadId.Value}";
                OpenAmoCrmLeadButton.Visibility = Visibility.Visible;
                OpenAmoCrmContactButton.Visibility = Visibility.Collapsed;
            }
            else if (_callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded)
            {
                // Запись загружена в контакт (по алгоритму CallGear: нет открытых лидов)
                AmoCrmLeadTextBlock.Text = "Contact";
                OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                OpenAmoCrmContactButton.Visibility = Visibility.Visible;
            }
            else
            {
                // Запись не загружена
                AmoCrmLeadTextBlock.Text = "Not added to any lead";
                OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                // Показываем кнопку контакта, если контакт найден в AmoCRM
                OpenAmoCrmContactButton.Visibility = Visibility.Collapsed; // Будет показана после загрузки contact ID
            }
            
            // Загружаем contact ID для кнопки "Open Contact"
            _ = LoadAmoCrmContactIdAsync();

            // AmoCRM Contact Name - загружаем асинхронно
            AmoCrmContactNameTextBlock.Text = "Loading...";
            _ = LoadAmoCrmContactNameAsync();
            
            // Загружаем contact ID для кнопки "Open Contact" (если запись прикреплена к контакту или нет лида)
            if (!_callItem.AmoCrmLeadId.HasValue)
            {
                _ = LoadAmoCrmContactIdAsync();
            }

            // AmoCRM Upload Status
            UpdateAmoCrmUploadStatusDisplay();

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
                RecordingBorder.Visibility = Visibility.Visible;
                
                // ВАЖНО: Проверяем путь к записи, даже если файл еще не создан (конвертация через ffmpeg может быть асинхронной)
                if (!string.IsNullOrEmpty(_callItem.RecordingFilePath))
                {
                    // Проверяем, существует ли файл
                    bool fileExists = System.IO.File.Exists(_callItem.RecordingFilePath);
                    
                    if (fileExists)
                    {
                        RecordingFilePathTextBlock.Text = Path.GetFileName(_callItem.RecordingFilePath);
                        PlayRecordingButton.IsEnabled = true;
                        OpenFolderButton.IsEnabled = true;
                        // Показываем кнопку загрузки всегда (пользователь может захотеть перезагрузить запись)
                        UploadRecordingButton.Visibility = Visibility.Visible;
                        MainWindow.Log($"[CallDetailsWindow] Recording file found: {_callItem.RecordingFilePath}");
                    }
                    else
                    {
                        // Файл еще не создан (конвертация через ffmpeg может быть в процессе)
                        RecordingFilePathTextBlock.Text = "Recording is being processed...";
                        PlayRecordingButton.IsEnabled = false;
                        // Разрешаем открыть папку даже если файл еще не создан
                        OpenFolderButton.IsEnabled = true;
                        // Показываем кнопку загрузки
                        UploadRecordingButton.Visibility = Visibility.Visible;
                        MainWindow.Log($"[CallDetailsWindow] Recording file path set but file not yet created (may be converting): {_callItem.RecordingFilePath}");
                    }
                    
                    // Проверяем формат файла для логирования
                    _isWebMFile = _callItem.RecordingFilePath.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
                    if (_isWebMFile)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Recording is WebM format - will open in default application when play button is clicked.");
                    }
                }
                else
                {
                    // Записи нет - показываем раздел с кнопкой загрузки
                    RecordingFilePathTextBlock.Text = "No recording file found";
                    PlayRecordingButton.IsEnabled = false;
                    OpenFolderButton.IsEnabled = false;
                    UploadRecordingButton.Visibility = Visibility.Visible;
                }

            }
            else
            {
                // Запись отключена в настройках - скрываем раздел
                RecordingBorder.Visibility = Visibility.Collapsed;
            }
            
            // Кнопка недозвона актуальна только для неотвеченных вызовов с определенными статусами
            // Показываем её для: Cancelled, Missed, Failed (только если не был отвечен)
            // НЕ показываем для: Calling (звонок еще идет), Ended (звонок был отвечен)
            bool shouldShowMissedCallButton = !_callItem.WasAnswered && 
                (_callItem.Status == CallStatus.Cancelled || 
                 _callItem.Status == CallStatus.Missed || 
                 _callItem.Status == CallStatus.Failed);
            
            UploadMissedCallButton.Visibility = shouldShowMissedCallButton ? Visibility.Visible : Visibility.Collapsed;
            UploadMissedCallTextBlock.Visibility = shouldShowMissedCallButton ? Visibility.Visible : Visibility.Collapsed;

            // Load full logs from file
            LoadFullLogs();
            
            // Set initial tab content
            RefreshActiveTab();
        }
        
        private void LoadFullLogs()
        {
            try
            {
                // Get logs directory
                string logsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Callspire", "Logs");
                
                if (!Directory.Exists(logsDir))
                {
                    // Fallback to technical details if logs directory not found
                    if (_callItem.TechnicalDetails != null && _callItem.TechnicalDetails.Count > 0)
                    {
                        _allLogs = _callItem.TechnicalDetails.ToList();
                    }
                    else
                    {
                        _allLogs = new List<string> { "Logs directory not found. Using technical details from call history." };
                    }
                    FilterLogs();
                    return;
                }
                
                // Find log file for the call date
                string logFileName = $"softphone_{_callItem.CallTime:yyyyMMdd}.log";
                string logFilePath = Path.Combine(logsDir, logFileName);
                
                // Also check for rolled files
                var logFiles = Directory.GetFiles(logsDir, $"softphone_{_callItem.CallTime:yyyyMMdd}*.log")
                    .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                    .ToList();
                
                if (logFiles.Count == 0)
                {
                    // Fallback to technical details if no log file found
                    if (_callItem.TechnicalDetails != null && _callItem.TechnicalDetails.Count > 0)
                    {
                        _allLogs = _callItem.TechnicalDetails.ToList();
                    }
                    else
                    {
                        _allLogs = new List<string> { "No log file found for this call date." };
                    }
                    FilterLogs();
                    return;
                }
                
                // Read logs from file(s) - check files around call time
                var callTime = _callItem.CallTime;
                var timeWindowStart = callTime.AddMinutes(-5); // 5 minutes before call
                var timeWindowEnd = callTime.AddMinutes(30); // 30 minutes after call (to catch recording events)
                
                var relevantLogs = new List<string>();
                
                foreach (var logFile in logFiles)
                {
                    try
                    {
                        var lines = File.ReadAllLines(logFile, Encoding.UTF8);
                        foreach (var line in lines)
                        {
                            // Parse timestamp from log line: "2025-12-22 12:34:56.789 [T12] message"
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            
                            var parts = line.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                // Try to parse date and time
                                if (DateTime.TryParse($"{parts[0]} {parts[1]}", out DateTime logTime))
                                {
                                    if (logTime >= timeWindowStart && logTime <= timeWindowEnd)
                                    {
                                        // Check if log is related to this call
                                        if (IsLogRelatedToCall(line))
                                        {
                                            relevantLogs.Add(line);
                                        }
                                    }
                                }
                                else
                                {
                                    // If timestamp parsing fails, check if line contains call-related info
                                    if (IsLogRelatedToCall(line))
                                    {
                                        relevantLogs.Add(line);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Error reading log file {logFile}: {ex.Message}");
                    }
                }
                
                // If no relevant logs found, use technical details as fallback
                if (relevantLogs.Count == 0)
                {
                    if (_callItem.TechnicalDetails != null && _callItem.TechnicalDetails.Count > 0)
                    {
                        relevantLogs = _callItem.TechnicalDetails.ToList();
                    }
                    else
                    {
                        relevantLogs = new List<string> { "No logs found for this call." };
                    }
                }
                
                _allLogs = relevantLogs;
                FilterLogs();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error loading full logs: {ex.Message}");
                TechnicalDetailsTextBlock.Text = $"Error loading logs: {ex.Message}";
            }
        }
        
        private bool IsLogRelatedToCall(string logLine)
        {
            if (string.IsNullOrEmpty(logLine)) return false;
            
            string phoneNumber = _callItem.PhoneNumber;
            string sipCallId = _callItem.SipCallId ?? string.Empty;
            string webRtcSessionId = _callItem.WebRtcSessionId ?? string.Empty;
            
            // Check for phone number
            if (!string.IsNullOrEmpty(phoneNumber) && logLine.Contains(phoneNumber, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // Check for SIP Call-ID
            if (!string.IsNullOrEmpty(sipCallId) && logLine.Contains(sipCallId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // Check for WebRTC Session ID
            if (!string.IsNullOrEmpty(webRtcSessionId) && logLine.Contains(webRtcSessionId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // Check for call-related keywords around call time
            var callTimeStr = _callItem.CallTime.ToString("HH:mm:ss");
            if (logLine.Contains(callTimeStr.Substring(0, 8))) // Match HH:mm:ss part
            {
                // Check for call-related keywords
                var callKeywords = new[] { "Call", "call", "WebRTC", "SIP", "AmoCRM", "AmoCrm", "recording", "Recording" };
                if (callKeywords.Any(keyword => logLine.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            
            return false;
        }
        
        private void FilterLogs()
        {
            _amoCrmLogs = _allLogs.Where(log => 
                log.Contains("[AmoCrm", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("[AmoCRM", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("AmoCRM", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("AmoCrm", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            _webRtcLogs = _allLogs.Where(log => 
                log.Contains("[WebRtc", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("[WebRTC", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("WebRTC", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("WebRtc", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("webrtc", StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            _sipLogs = _allLogs.Where(log => 
                log.Contains("[Sip", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("[SIP", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("[SipService", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("SIP", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        
        private void RefreshActiveTab()
        {
            if (LogsTabControl.SelectedItem == null)
            {
                LogsTabControl.SelectedIndex = 0;
            }
            
            var selectedTab = LogsTabControl.SelectedItem as TabItem;
            if (selectedTab == null) return;
            
            List<string> logsToShow;
            
            if (selectedTab == AllLogsTab)
            {
                logsToShow = _allLogs;
            }
            else if (selectedTab == AmoCrmLogsTab)
            {
                logsToShow = _amoCrmLogs;
            }
            else if (selectedTab == WebRtcLogsTab)
            {
                logsToShow = _webRtcLogs;
            }
            else if (selectedTab == SipLogsTab)
            {
                logsToShow = _sipLogs;
            }
            else
            {
                logsToShow = _allLogs;
            }
            
            if (logsToShow.Count > 0)
            {
                TechnicalDetailsTextBlock.Text = string.Join("\n", logsToShow);
            }
            else
            {
                TechnicalDetailsTextBlock.Text = "No logs found for this category.";
            }
            
            // Add error message if present
            if (!string.IsNullOrEmpty(_callItem.ErrorMessage))
            {
                TechnicalDetailsTextBlock.Text += $"\n\nError: {_callItem.ErrorMessage}";
            }
        }
        
        private void LogsTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshActiveTab();
        }
        
        private void PlayRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_callItem.RecordingFilePath))
                {
                    MainWindow.Log($"[CallDetailsWindow] Play clicked but RecordingFilePath is empty");
                    return;
                }
                
                if (!File.Exists(_callItem.RecordingFilePath))
                {
                    CustomMessageBox.Show(
                        "Recording file not found.\n\nThe file may still be processing.",
                        "File Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
                
                // Открываем файл в стандартном приложении Windows
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = _callItem.RecordingFilePath,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                MainWindow.Log($"[CallDetailsWindow] Recording file opened in default application: {_callItem.RecordingFilePath}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error opening recording file: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not open recording file:\n{ex.Message}\n\n" +
                    "Please use an external media player to play this file.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }
        
        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_callItem.RecordingFilePath))
                {
                    MainWindow.Log($"[CallDetailsWindow] Open folder clicked but RecordingFilePath is empty");
                    return;
                }
                
                string? folderPath = Path.GetDirectoryName(_callItem.RecordingFilePath);
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                {
                    CustomMessageBox.Show(
                        "Recording folder not found.",
                        "Folder Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
                
                // Open folder in Windows Explorer and select the file
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{_callItem.RecordingFilePath}\"",
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                MainWindow.Log($"[CallDetailsWindow] Recording folder opened: {folderPath}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error opening recording folder: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not open recording folder:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }

        private async void UploadRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Используем файл записи, связанный с этим звонком
                string? recordingFile = _callItem.RecordingFilePath;
                
                if (string.IsNullOrEmpty(recordingFile))
                {
                    CustomMessageBox.Show(
                        "No recording file found for this call.\n\nCannot upload recording.",
                        "No Recording File",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
                
                if (!File.Exists(recordingFile))
                {
                    CustomMessageBox.Show(
                        $"Recording file not found:\n{Path.GetFileName(recordingFile)}\n\nThe file may still be processing.",
                        "File Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                // Get AmoCrmService from MainWindow
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null)
                {
                    CustomMessageBox.Show(
                        "Cannot access AmoCRM service.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                    return;
                }

                var amoCrmService = mainWindow.GetAmoCrmService();
                if (amoCrmService == null || !amoCrmService.IsInitialized)
                {
                    CustomMessageBox.Show(
                        "AmoCRM service is not initialized.\n\nPlease check your AmoCRM settings.",
                        "AmoCRM Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                // Disable button during upload
                UploadRecordingButton.IsEnabled = false;
                UploadRecordingButton.Content = "Uploading...";

                // Get leads by phone number (show all open leads whose contacts contain this phone)
                var leads = await amoCrmService.GetLeadsByPhoneAsync(_callItem.PhoneNumber);
                if (leads == null || leads.Count == 0)
                {
                    CustomMessageBox.Show(
                        $"No leads found for this phone number.\n\nCannot upload recording.",
                        "No Leads Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    UploadRecordingButton.IsEnabled = true;
                    UploadRecordingButton.Content = "Upload Recording";
                    return;
                }

                long? selectedLeadId = null;

                // Всегда показываем окно выбора лида, даже если лид один —
                // пользователь явно выбирает, куда загрузить запись.
                string? subdomain = null;
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        subdomain = settings?.AmoCrmSubdomain;
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error loading subdomain: {ex.Message}");
                }

                var selectionWindow = new LeadSelectionWindow(leads, subdomain)
                {
                    Owner = this
                };
                
                bool? selectionResult = selectionWindow.ShowDialog();
                if (selectionResult == true && selectionWindow.SelectedLeadId.HasValue)
                {
                    selectedLeadId = selectionWindow.SelectedLeadId.Value;
                    MainWindow.Log($"[CallDetailsWindow] User selected lead ID: {selectedLeadId} for manual upload");
                }
                else
                {
                    MainWindow.Log($"[CallDetailsWindow] User cancelled lead selection for manual upload");
                    
                    // Update upload status - cancelled by user
                    _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Cancelled;
                    _callItem.AmoCrmUploadReason = "User cancelled lead selection";
                    var callHistoryService = new CallHistoryService();
                    callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Cancelled, "User cancelled lead selection");
                    UpdateAmoCrmUploadStatusDisplay();
                    
                    UploadRecordingButton.IsEnabled = true;
                    UploadRecordingButton.Content = "Upload Recording";
                    return;
                }

                if (!selectedLeadId.HasValue)
                {
                    CustomMessageBox.Show(
                        "No lead selected.\n\nCannot upload recording.",
                        "No Lead Selected",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    UploadRecordingButton.IsEnabled = true;
                    UploadRecordingButton.Content = "Upload Recording";
                    return;
                }

                // Получаем информацию о звонке для создания примечания
                bool isIncoming = _callItem.IsIncoming;

                // Используем длительность из истории звонков, а если её нет (0 секунд),
                // оцениваем по размеру WAV-файла (PCM 16bit, 44.1 kHz, mono).
                int durationSeconds;
                if (_callItem.Duration.HasValue && _callItem.Duration.Value.TotalSeconds > 0.5)
                {
                    durationSeconds = (int)Math.Round(_callItem.Duration.Value.TotalSeconds);
                }
                else
                {
                    try
                    {
                        var fi = new FileInfo(recordingFile);
                        // bytesPerSecond = sampleRate (44100) * channels (1) * bytesPerSample (2)
                        double bytesPerSecond = 44100.0 * 1 * 2;
                        durationSeconds = (int)Math.Max(1, Math.Round(fi.Length / bytesPerSecond));
                        MainWindow.Log($"[CallDetailsWindow] Estimated call duration from file size: {durationSeconds} s (length={fi.Length} bytes)");
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Error estimating duration from file: {ex.Message}");
                        durationSeconds = 0;
                    }
                }

                bool wasAnswered = _callItem.WasAnswered;
                
                // Upload file to selected lead (используем файл записи этого звонка)
                bool success = await amoCrmService.ManuallyUploadRecordingToLeadAsync(
                    selectedLeadId.Value, 
                    recordingFile, 
                    _callItem.PhoneNumber, 
                    isIncoming, 
                    durationSeconds, 
                    wasAnswered);

                if (success)
                {
                    CustomMessageBox.Show(
                        $"Recording file successfully uploaded to AmoCRM lead {selectedLeadId.Value}.",
                        "Upload Successful",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this);
                    
                    // Update UI - show that file was uploaded
                    PlayRecordingButton.IsEnabled = File.Exists(recordingFile);
                    OpenFolderButton.IsEnabled = true;
                    // Не скрываем кнопку - пользователь может захотеть перезагрузить
                    // UploadRecordingButton.Visibility = Visibility.Collapsed;
                    
                    // Update AmoCrmLeadId in call item
                    _callItem.AmoCrmLeadId = selectedLeadId;
                    AmoCrmLeadTextBlock.Text = $"Lead #{selectedLeadId.Value}";
                    OpenAmoCrmLeadButton.Visibility = Visibility.Visible;
                    
                    // Update upload status
                    _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Uploaded;
                    _callItem.AmoCrmUploadReason = null;
                    var callHistoryService = new CallHistoryService();
                    callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Uploaded, null);
                    UpdateAmoCrmUploadStatusDisplay();
                }
                else
                {
                    CustomMessageBox.Show(
                        "Failed to upload recording file.\n\nPlease check the logs for details.",
                        "Upload Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                    
                    // Update upload status
                    _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Failed;
                    _callItem.AmoCrmUploadReason = "Manual upload failed";
                    var callHistoryService = new CallHistoryService();
                    callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Failed, "Manual upload failed");
                    UpdateAmoCrmUploadStatusDisplay();
                }

                UploadRecordingButton.IsEnabled = true;
                UploadRecordingButton.Content = "Upload Recording";
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error uploading recording: {ex.Message}");
                CustomMessageBox.Show(
                    $"Error uploading recording:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
                UploadRecordingButton.IsEnabled = true;
                UploadRecordingButton.Content = "Upload Recording";
            }
        }

        private async void UploadMissedCallButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Missed call card имеет смысл только для неотвеченных вызовов
                if (_callItem.WasAnswered)
                {
                    CustomMessageBox.Show(
                        "This call was answered.\n\nMissed call card is only available for not answered calls.",
                        "Not a missed call",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this);
                    return;
                }

                // Get AmoCrmService from MainWindow
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null)
                {
                    CustomMessageBox.Show(
                        "Cannot access AmoCRM service.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                    return;
                }

                var amoCrmService = mainWindow.GetAmoCrmService();
                if (amoCrmService == null || !amoCrmService.IsInitialized)
                {
                    CustomMessageBox.Show(
                        "AmoCRM service is not initialized.\n\nPlease check your AmoCRM settings.",
                        "AmoCRM Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                UploadMissedCallButton.IsEnabled = false;
                UploadMissedCallButton.Content = "Uploading...";

                // Ищем лиды по номеру (та же логика, что и для записи)
                var leads = await amoCrmService.GetLeadsByPhoneAsync(_callItem.PhoneNumber);
                if (leads == null || leads.Count == 0)
                {
                    CustomMessageBox.Show(
                        $"No leads found for this phone number.\n\nCannot create missed call card.",
                        "No Leads Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    UploadMissedCallButton.IsEnabled = true;
                    UploadMissedCallButton.Content = "Upload missed call";
                    return;
                }

                long? selectedLeadId = null;

                string? subdomain = null;
                try
                {
                    string settingsPath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsPath))
                    {
                        string json = File.ReadAllText(settingsPath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        subdomain = settings?.AmoCrmSubdomain;
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error loading subdomain for missed call: {ex.Message}");
                }

                var selectionWindow = new LeadSelectionWindow(leads, subdomain)
                {
                    Owner = this
                };

                bool? selectionResult = selectionWindow.ShowDialog();
                if (selectionResult == true && selectionWindow.SelectedLeadId.HasValue)
                {
                    selectedLeadId = selectionWindow.SelectedLeadId.Value;
                    MainWindow.Log($"[CallDetailsWindow] User selected lead ID: {selectedLeadId} for missed call");
                }
                else
                {
                    MainWindow.Log("[CallDetailsWindow] User cancelled lead selection for missed call");
                    UploadMissedCallButton.IsEnabled = true;
                    UploadMissedCallButton.Content = "Upload missed call";
                    return;
                }

                if (!selectedLeadId.HasValue)
                {
                    CustomMessageBox.Show(
                        "No lead selected.\n\nCannot create missed call card.",
                        "No Lead Selected",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    UploadMissedCallButton.IsEnabled = true;
                    UploadMissedCallButton.Content = "Upload missed call";
                    return;
                }

                bool isIncoming = _callItem.IsIncoming;

                bool success = await amoCrmService.ManuallyUploadMissedCallToLeadAsync(
                    selectedLeadId.Value,
                    _callItem.PhoneNumber,
                    isIncoming);

                if (success)
                {
                    CustomMessageBox.Show(
                        $"Missed call card successfully created in AmoCRM lead {selectedLeadId.Value}.",
                        "Upload Successful",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this);

                    // Обновляем статус, что недозвон загружен (без записи)
                    _callItem.AmoCrmLeadId = selectedLeadId;
                    AmoCrmLeadTextBlock.Text = $"Lead ID: {selectedLeadId.Value}";
                    OpenAmoCrmLeadButton.Visibility = Visibility.Visible;

                    _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Uploaded;
                    _callItem.AmoCrmUploadReason = null;
                    var callHistoryService = new CallHistoryService();
                    callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Uploaded, null);
                    UpdateAmoCrmUploadStatusDisplay();
                }
                else
                {
                    CustomMessageBox.Show(
                        "Failed to create missed call card.\n\nPlease check the logs for details.",
                        "Upload Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                }

                UploadMissedCallButton.IsEnabled = true;
                UploadMissedCallButton.Content = "Upload missed call";
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error uploading missed call: {ex.Message}");
                CustomMessageBox.Show(
                    $"Error uploading missed call:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
                UploadMissedCallButton.IsEnabled = true;
                UploadMissedCallButton.Content = "Upload missed call";
            }
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
            // Используем SystemCommands для стандартных анимаций Windows 11
            SystemCommands.MinimizeWindow(this);
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

        private void CopyPhoneNumberButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string phoneNumber = PhoneNumberTextBlock.Text;
                if (!string.IsNullOrEmpty(phoneNumber))
                {
                    Clipboard.SetText(phoneNumber);
                    MainWindow.Log($"[CallDetailsWindow] Phone number copied to clipboard: {phoneNumber}");
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error copying phone number: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not copy phone number:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }

        private void OpenAmoCrmLeadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_callItem.AmoCrmLeadId.HasValue)
                {
                    return;
                }

                // Получаем URL AmoCRM из настроек
                string? amoCrmUrl = null;
                try
                {
                    string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        if (settings != null && !string.IsNullOrEmpty(settings.AmoCrmSubdomain))
                        {
                            string subdomain = settings.AmoCrmSubdomain.Trim();
                            // Обрабатываем разные форматы поддомена
                            if (subdomain.Contains("."))
                            {
                                // Полный домен (например, mdkb.amocrm.com)
                                if (subdomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                    subdomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                                {
                                    amoCrmUrl = subdomain;
                                }
                                else
                                {
                                    amoCrmUrl = $"https://{subdomain}";
                                }
                            }
                            else
                            {
                                // Только поддомен (например, mdkb) — используем amocrm.com
                                amoCrmUrl = $"https://{subdomain}.amocrm.com";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error reading AmoCRM settings: {ex.Message}");
                }

                if (string.IsNullOrEmpty(amoCrmUrl))
                {
                    CustomMessageBox.Show(
                        "AmoCRM URL not configured.\n\nPlease configure AmoCRM integration in Settings.",
                        "AmoCRM Not Configured",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                // Формируем ссылку на лид в AmoCRM
                string leadUrl = $"{amoCrmUrl.TrimEnd('/')}/leads/detail/{_callItem.AmoCrmLeadId.Value}";
                
                // Открываем в браузере
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = leadUrl,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                
                MainWindow.Log($"[CallDetailsWindow] Opening AmoCRM lead in browser: {leadUrl}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error opening AmoCRM lead: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not open AmoCRM lead:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }

        /// <summary>
        /// Обработчик клика по кнопке "Open Contact" - открывает страницу контакта в AmoCRM
        /// </summary>
        private void OpenAmoCrmContactButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_amoCrmContactId.HasValue)
                {
                    CustomMessageBox.Show(
                        "Contact ID not available.\n\nContact may not be found in AmoCRM.",
                        "Contact Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                // Получаем URL AmoCRM из настроек (используем тот же код, что и для лида)
                string? amoCrmUrl = null;
                try
                {
                    string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                    if (File.Exists(settingsFilePath))
                    {
                        string json = File.ReadAllText(settingsFilePath);
                        var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                        if (settings != null && !string.IsNullOrEmpty(settings.AmoCrmSubdomain))
                        {
                            string subdomain = settings.AmoCrmSubdomain.Trim();
                            // Обрабатываем разные форматы поддомена
                            if (subdomain.Contains("."))
                            {
                                // Полный домен (например, mdkb.amocrm.com)
                                if (subdomain.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                    subdomain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                                {
                                    amoCrmUrl = subdomain;
                                }
                                else
                                {
                                    amoCrmUrl = $"https://{subdomain}";
                                }
                            }
                            else
                            {
                                // Только поддомен (например, mdkb) — используем amocrm.com
                                amoCrmUrl = $"https://{subdomain}.amocrm.com";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[CallDetailsWindow] Error reading AmoCRM settings: {ex.Message}");
                }

                if (string.IsNullOrEmpty(amoCrmUrl))
                {
                    CustomMessageBox.Show(
                        "AmoCRM URL not configured.\n\nPlease configure AmoCRM integration in Settings.",
                        "AmoCRM Not Configured",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                // Формируем ссылку на контакт в AmoCRM
                string contactUrl = $"{amoCrmUrl.TrimEnd('/')}/contacts/detail/{_amoCrmContactId.Value}";
                
                // Открываем в браузере
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = contactUrl,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                
                MainWindow.Log($"[CallDetailsWindow] Opening AmoCRM contact in browser: {contactUrl}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error opening AmoCRM contact: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not open AmoCRM contact:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }

        /// <summary>
        /// Загружает ID контакта из AmoCRM по номеру телефона для кнопки "Open Contact"
        /// </summary>
        private async Task LoadAmoCrmContactIdAsync()
        {
            try
            {
                // Проверяем, включена ли интеграция AmoCRM
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null || !mainWindow.IsAmoCrmServiceInitialized())
                {
                    return;
                }

                var amoCrmService = mainWindow.GetAmoCrmService();
                if (amoCrmService == null)
                {
                    return;
                }

                // Получаем ID контакта из AmoCRM
                long? contactId = await amoCrmService.FindContactByPhoneAsync(_callItem.PhoneNumber);

                // Обновляем UI в главном потоке
                Dispatcher.Invoke(() =>
                {
                    if (contactId.HasValue)
                    {
                        _amoCrmContactId = contactId.Value;
                        // Показываем кнопку "Open Contact", если запись не прикреплена к лиду
                        if (!_callItem.AmoCrmLeadId.HasValue)
                        {
                            OpenAmoCrmContactButton.Visibility = Visibility.Visible;
                        }
                        MainWindow.Log($"[CallDetailsWindow] Loaded AmoCRM contact ID: {contactId.Value} for {_callItem.PhoneNumber}");
                    }
                    else
                    {
                        _amoCrmContactId = null;
                        // Если нет contact ID и нет lead ID, скрываем кнопку
                        if (!_callItem.AmoCrmLeadId.HasValue)
                        {
                            OpenAmoCrmContactButton.Visibility = Visibility.Collapsed;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error loading AmoCRM contact ID: {ex.Message}");
            }
        }

        /// <summary>
        /// Загружает имя контакта из AmoCRM по номеру телефона
        /// </summary>
        private async Task LoadAmoCrmContactNameAsync()
        {
            try
            {
                // Проверяем, включена ли интеграция AmoCRM
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow == null || !mainWindow.IsAmoCrmServiceInitialized())
                {
                    Dispatcher.Invoke(() =>
                    {
                        AmoCrmContactNameTextBlock.Text = "AmoCRM integration not enabled";
                    });
                    return;
                }

                var amoCrmService = mainWindow.GetAmoCrmService();
                if (amoCrmService == null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AmoCrmContactNameTextBlock.Text = "AmoCRM service not available";
                    });
                    return;
                }

                // Получаем имя контакта из AmoCRM
                string? contactName = await amoCrmService.GetContactNameByPhoneAsync(_callItem.PhoneNumber);

                // Обновляем UI в главном потоке
                Dispatcher.Invoke(() =>
                {
                    if (!string.IsNullOrEmpty(contactName))
                    {
                        AmoCrmContactNameTextBlock.Text = contactName;
                        MainWindow.Log($"[CallDetailsWindow] Loaded AmoCRM contact name: {contactName} for {_callItem.PhoneNumber}");
                    }
                    else
                    {
                        AmoCrmContactNameTextBlock.Text = "Contact not found in AmoCRM";
                    }
                });
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error loading AmoCRM contact name: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    AmoCrmContactNameTextBlock.Text = "Error loading contact name";
                });
            }
        }

        /// <summary>
        /// Запускает таймер для периодической проверки статуса AmoCRM (обработка идёт в фоне).
        /// </summary>
        private void StartAmoCrmRefreshTimer()
        {
            _amoCrmRefreshCount = 0;
            _amoCrmRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _amoCrmRefreshTimer.Tick += AmoCrmRefreshTimer_Tick;
            _amoCrmRefreshTimer.Start();
            MainWindow.Log("[CallDetailsWindow] AmoCRM refresh timer started (polling every 2s)");
        }

        private void StopAmoCrmRefreshTimer()
        {
            if (_amoCrmRefreshTimer != null)
            {
                _amoCrmRefreshTimer.Stop();
                _amoCrmRefreshTimer.Tick -= AmoCrmRefreshTimer_Tick;
                _amoCrmRefreshTimer = null;
                MainWindow.Log("[CallDetailsWindow] AmoCRM refresh timer stopped");
            }
        }

        private void AmoCrmRefreshTimer_Tick(object? sender, EventArgs e)
        {
            _amoCrmRefreshCount++;
            
            try
            {
                var history = new CallHistoryService();
                var latest = history.GetCall(_callItem.PhoneNumber, _callItem.CallTime);
                if (latest == null) return;

                // Проверяем, обновился ли LeadId или статус загрузки
                bool needsUpdate = false;
                if (latest.AmoCrmLeadId.HasValue != _callItem.AmoCrmLeadId.HasValue)
                {
                    _callItem.AmoCrmLeadId = latest.AmoCrmLeadId;
                    needsUpdate = true;
                }
                
                if (latest.AmoCrmUploadStatus != _callItem.AmoCrmUploadStatus)
                {
                    _callItem.AmoCrmUploadStatus = latest.AmoCrmUploadStatus;
                    _callItem.AmoCrmUploadReason = latest.AmoCrmUploadReason;
                    needsUpdate = true;
                }
                
                if (needsUpdate)
                {
                    // Обновляем отображение "Record added to"
                    if (_callItem.AmoCrmLeadId.HasValue)
                    {
                        AmoCrmLeadTextBlock.Text = $"Lead #{_callItem.AmoCrmLeadId.Value}";
                        OpenAmoCrmLeadButton.Visibility = Visibility.Visible;
                        MainWindow.Log($"[CallDetailsWindow] AmoCRM Lead ID updated via refresh: {_callItem.AmoCrmLeadId.Value}");
                    }
                    else if (_callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded)
                    {
                        AmoCrmLeadTextBlock.Text = "Contact";
                        OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                        OpenAmoCrmContactButton.Visibility = _amoCrmContactId.HasValue ? Visibility.Visible : Visibility.Collapsed;
                        MainWindow.Log($"[CallDetailsWindow] AmoCRM upload status updated: record added to Contact");
                    }
                    else
                    {
                        AmoCrmLeadTextBlock.Text = "Not added to any lead";
                        OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                        OpenAmoCrmContactButton.Visibility = _amoCrmContactId.HasValue ? Visibility.Visible : Visibility.Collapsed;
                    }
                    
                    UpdateAmoCrmUploadStatusDisplay();
                    MainWindow.Log($"[CallDetailsWindow] AmoCRM upload status updated via refresh: {_callItem.AmoCrmUploadStatus}");
                }

                // Проверяем файл записи
                if (!string.IsNullOrEmpty(latest.RecordingFilePath) && string.IsNullOrEmpty(_callItem.RecordingFilePath))
                {
                    _callItem.RecordingFilePath = latest.RecordingFilePath;
                }

                // Останавливаем таймер, если обработка завершена
                if (latest.AmoCrmUploadStatus != AmoCrmUploadStatus.NotUploaded || _amoCrmRefreshCount >= MaxAmoCrmRefreshTicks)
                {
                    if (_amoCrmRefreshCount >= MaxAmoCrmRefreshTicks)
                    {
                        MainWindow.Log("[CallDetailsWindow] AmoCRM refresh timer: max ticks reached, stopping");
                    }
                    else
                    {
                        MainWindow.Log("[CallDetailsWindow] AmoCRM refresh timer: final status reached, stopping");
                    }
                    StopAmoCrmRefreshTimer();
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] AmoCRM refresh timer error: {ex.Message}");
                // Не останавливаем таймер при ошибке — пусть попробует снова
            }
        }

        /// <summary>
        /// Обновляет отображение статуса загрузки записи в AmoCRM
        /// </summary>
        private void UpdateAmoCrmUploadStatusDisplay()
        {
            string statusText;
            Brush statusColor;

            switch (_callItem.AmoCrmUploadStatus)
            {
                case AmoCrmUploadStatus.Uploaded:
                    statusText = "✅ Uploaded successfully";
                    statusColor = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
                    break;
                    
                case AmoCrmUploadStatus.Cancelled:
                    statusText = "❌ Upload cancelled by user";
                    statusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    break;
                    
                case AmoCrmUploadStatus.Failed:
                    statusText = $"❌ Upload failed";
                    if (!string.IsNullOrEmpty(_callItem.AmoCrmUploadReason))
                    {
                        statusText += $": {_callItem.AmoCrmUploadReason}";
                    }
                    statusColor = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                    break;
                    
                case AmoCrmUploadStatus.NotUploaded:
                default:
                    if (!string.IsNullOrEmpty(_callItem.AmoCrmUploadReason))
                    {
                        statusText = $"⚠️ Not uploaded: {_callItem.AmoCrmUploadReason}";
                    }
                    else
                    {
                        statusText = "⚠️ Not uploaded";
                    }
                    statusColor = new SolidColorBrush(Color.FromRgb(234, 179, 8)); // Yellow/Orange
                    break;
            }

            AmoCrmUploadStatusTextBlock.Text = statusText;
            AmoCrmUploadStatusTextBlock.Foreground = statusColor;
        }
    }
}

