#if WINDOWS
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
        private bool _isWebMFile = false;
        private bool _outboundCallerIdLoadStarted = false;
        private List<string> _allLogs = new List<string>();
        private List<string> _amoCrmLogs = new List<string>();
        private List<string> _webRtcLogs = new List<string>();
        private List<string> _sipLogs = new List<string>();
        private DispatcherTimer? _amoCrmRefreshTimer;
        private int _amoCrmRefreshCount = 0;
        private const int MaxAmoCrmRefreshTicks = 20; // 20 ticks × 2s = 40 seconds max
        private long? _amoCrmContactId = null; // ID контакта в AmoCRM для кнопки "Open Contact"
        private const string UploadCallResultButtonDefaultCaption = "Upload local recording";
        private const string UploadCallResultButtonGatewayCaption = "Retry upload via Gateway";
        private bool _isAmoCrmIntegrationEnabled;
        private string? _crmSectionRecordingPath;

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

            var localRecording = CallWindowHelpers.ResolveLocalClientRecordingPath(_callItem);
            if (!string.IsNullOrEmpty(localRecording))
                _callItem.RecordingFilePath = localRecording;
            
            // Логируем информацию о звонке для диагностики
            MainWindow.Log($"[CallDetailsWindow] Opening for call: PhoneNumber={_callItem.PhoneNumber}, CallTime={_callItem.CallTime:HH:mm:ss.fff}, " +
                $"RecordingFilePath={(string.IsNullOrEmpty(_callItem.RecordingFilePath) ? "null" : _callItem.RecordingFilePath)}, " +
                $"FileExists={(string.IsNullOrEmpty(_callItem.RecordingFilePath) ? "N/A" : File.Exists(_callItem.RecordingFilePath).ToString())}");
            
            LoadCallDetails();

            // Догружаем Outbound CallerID из PBX CDR, если он еще не успел появиться на момент открытия окна.
            _ = TryLoadOutboundCallerIdFromPbxAsync();
            
            // Если AmoCRM статус ещё не финальный — запускаем таймер,
            // чтобы обновить поля когда фоновая обработка завершится.
            if (_isAmoCrmIntegrationEnabled
                && _callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.NotUploaded
                && !_callItem.AmoCrmLeadId.HasValue)
            {
                StartAmoCrmRefreshTimer();
            }
            
            this.Closed += (s, e) => StopAmoCrmRefreshTimer();
        }

        private async Task TryLoadOutboundCallerIdFromPbxAsync()
        {
            if (_outboundCallerIdLoadStarted) return;
            if (_callItem.IsIncoming) return;
            if (!string.IsNullOrEmpty(_callItem.OutboundCallerId)) return;

            var mainWindow = Application.Current.MainWindow as MainWindow;
            var cdrService = mainWindow?.GetMikoPbxCdrService();
            if (cdrService == null) return;

            _outboundCallerIdLoadStarted = true;
            try
            {
                string? callerId = null;
                for (int attempt = 1; attempt <= 4; attempt++)
                {
                    int delayMs = attempt == 1 ? 3000 : attempt == 2 ? 5000 : attempt == 3 ? 10000 : 15000;
                    await Task.Delay(delayMs).ConfigureAwait(false);

                    callerId = await cdrService.GetCallCallerIdAsync(_callItem.PhoneNumber, _callItem.CallTime).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(callerId))
                        break;
                }

                if (!string.IsNullOrEmpty(callerId))
                {
                    _callItem.OutboundCallerId = callerId;
                    try
                    {
                        Dispatcher.Invoke(() =>
                        {
                            OutboundCallerIdLabel.Visibility = Visibility.Visible;
                            OutboundCallerIdTextBlock.Visibility = Visibility.Visible;
                            OutboundCallerIdTextBlock.Text = callerId;
                        });
                    }
                    catch
                    {
                        // Окно могли закрыть; в этом случае достаточно хотя бы обновить историю.
                    }

                    var histService = new CallHistoryService();
                    histService.UpdateOutboundCallerId(_callItem.PhoneNumber, _callItem.CallTime, callerId);
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] PBX CallerID load failed: {ex.Message}");
            }
            finally
            {
                _outboundCallerIdLoadStarted = false;
            }
        }

        private string GetTransportText(CallTransport transport)
        {
            return transport == CallTransport.WebRtc ? "WebRTC" : "SIP";
        }

        private Brush GetTransportBrush(CallTransport transport)
        {
            return transport == CallTransport.WebRtc
                ? new SolidColorBrush(Color.FromRgb(37, 99, 235))   // синий для WebRTC
                : new SolidColorBrush(Color.FromRgb(16, 185, 129)); // зелёный для SIP
        }

        private void LoadCallDetails()
        {
            // Проверяем настройки приложения (запись, AmoCRM и т.д.)
            bool isCallRecordingEnabled = false;
            bool isAmoCrmEnabled = false;
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    isCallRecordingEnabled = settings?.EnableCallRecording ?? false;
                    isAmoCrmEnabled = settings?.EnableAmoCrmIntegration ?? false;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error reading call recording setting: {ex.Message}");
            }

            _isAmoCrmIntegrationEnabled = isAmoCrmEnabled;

            if (!isAmoCrmEnabled)
            {
                AmoCrmSectionBorder.Visibility = Visibility.Collapsed;
                AmoCrmLogsTab.Visibility = Visibility.Collapsed;
            }
            else
            {
                AmoCrmSectionBorder.Visibility = Visibility.Visible;
                AmoCrmLogsTab.Visibility = Visibility.Visible;
            }

            // Скрываем раздел "Call Recording", если запись отключена в настройках
            if (!isCallRecordingEnabled)
            {
                RecordingBorder.Visibility = Visibility.Collapsed;
                CallRecordingSectionTitle.Visibility = Visibility.Collapsed;
                MainWindow.Log("[CallDetailsWindow] Call recording is disabled in settings - hiding recording section");
            }
            
            // Call Information
            PhoneNumberTextBlock.Text = _callItem.PhoneNumber;

            // Outbound CallerID — show only if available (populated via PBX CDR API, not from PAI)
            if (!string.IsNullOrEmpty(_callItem.OutboundCallerId))
            {
                OutboundCallerIdLabel.Visibility = Visibility.Visible;
                OutboundCallerIdTextBlock.Visibility = Visibility.Visible;
                OutboundCallerIdTextBlock.Text = _callItem.OutboundCallerId;
            }

            CallTimeTextBlock.Text = _callItem.CallTime.ToString("yyyy-MM-dd HH:mm:ss");
            DirectionTextBlock.Text = _callItem.IsIncoming ? "Incoming" : "Outgoing";

            // Транспортный бейдж (SIP/WebRTC)
            TransportBadgeTextBlock.Text = GetTransportText(_callItem.Transport);
            TransportBadgeBorder.Background = GetTransportBrush(_callItem.Transport);

            // Call Ended By
            EndedByTextBlock.Text = GetEndedByText(_callItem.EndedBy);
            EndedByTextBlock.Foreground = GetEndedByColor(_callItem.EndedBy);

            RefreshCallStatusDisplay();

            // Блок AmoCRM заполняем только если интеграция включена.
            if (isAmoCrmEnabled)
            {
                // Record added to (lead ID или Contact)
                if (_callItem.AmoCrmLeadId.HasValue)
                {
                    // Запись загружена в лид
                    AmoCrmLeadTextBlock.Text = $"Lead #{_callItem.AmoCrmLeadId.Value}";
                    AmoCrmLeadTextBlock.Visibility = Visibility.Visible;
                    OpenAmoCrmLeadButton.Visibility = Visibility.Visible;
                    OpenAmoCrmContactButton.Visibility = Visibility.Collapsed;
                }
                else if (_callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded)
                {
                    // Запись загружена в контакт (по алгоритму CallGear: нет открытых лидов)
                    AmoCrmLeadTextBlock.Text = "Contact";
                    AmoCrmLeadTextBlock.Visibility = Visibility.Visible;
                    OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                    OpenAmoCrmContactButton.Visibility = Visibility.Visible;
                }
                else
                {
                    // Запись не загружена
                    AmoCrmLeadTextBlock.Text = "Not added to any lead";
                    AmoCrmLeadTextBlock.Visibility = Visibility.Visible;
                    OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                    // Показываем кнопку контакта, если контакт найден в AmoCRM
                    OpenAmoCrmContactButton.Visibility = Visibility.Collapsed; // Будет показана после загрузки contact ID
                }

                // Загружаем contact ID для кнопки «Open contact»
                _ = LoadAmoCrmContactIdAsync();

                // AmoCRM Contact Name - загружаем асинхронно
                AmoCrmContactNameTextBlock.Text = "Loading...";
                _ = LoadAmoCrmContactNameAsync();

                // AmoCRM Upload Status
                UpdateAmoCrmUploadStatusDisplay();

                UpdateCrmManualUploadSectionVisibility(isAmoCrmEnabled);
            }

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

            // Call Recording
            // Показываем раздел записи только если запись включена в настройках
            if (isCallRecordingEnabled)
            {
                RecordingBorder.Visibility = Visibility.Visible;
                CallRecordingSectionTitle.Visibility = Visibility.Visible;
                
                // ВАЖНО: Проверяем путь к записи, даже если файл еще не создан (конвертация через ffmpeg может быть асинхронной)
                if (!string.IsNullOrEmpty(_callItem.RecordingFilePath))
                {
                    bool fileExists = System.IO.File.Exists(_callItem.RecordingFilePath);
                    bool fileUsable = IsUsableRecordingFile(_callItem.RecordingFilePath);
                    
                    if (fileUsable)
                    {
                        RecordingFilePathTextBlock.Text = Path.GetFileName(_callItem.RecordingFilePath);
                        PlayRecordingButton.IsEnabled = true;
                        OpenFolderButton.IsEnabled = true;
                        MainWindow.Log($"[CallDetailsWindow] Recording file found: {_callItem.RecordingFilePath}");
                    }
                    else if (fileExists)
                    {
                        RecordingFilePathTextBlock.Text = "Recording is still processing or invalid";
                        PlayRecordingButton.IsEnabled = false;
                        OpenFolderButton.IsEnabled = true;
                        MainWindow.Log($"[CallDetailsWindow] Recording file exists but not ready to play: {_callItem.RecordingFilePath}");
                    }
                    else
                    {
                        // Файл еще не создан (конвертация через ffmpeg может быть в процессе)
                        RecordingFilePathTextBlock.Text = "Recording is being processed...";
                        PlayRecordingButton.IsEnabled = false;
                        // Разрешаем открыть папку даже если файл еще не создан
                        OpenFolderButton.IsEnabled = true;
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
                    // Записи нет
                    RecordingFilePathTextBlock.Text = "No recording file found";
                    PlayRecordingButton.IsEnabled = false;
                    OpenFolderButton.IsEnabled = false;
                }

            }
            else
            {
                // Запись отключена в настройках - скрываем раздел
                RecordingBorder.Visibility = Visibility.Collapsed;
            }
            
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
                
                if (!CallWindowHelpers.IsRecordingWavPlausible(_callItem.RecordingFilePath, out long fileBytes))
                {
                    CustomMessageBox.Show(
                        File.Exists(_callItem.RecordingFilePath)
                            ? "Recording file is still being processed or is invalid.\n\nPlease wait a few seconds and try again."
                            : "Recording file not found.\n\nThe file may still be processing.",
                        "File Not Ready",
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

        private static bool IsUsableRecordingFile(string? path) =>
            CallWindowHelpers.IsRecordingUsableForUpload(path);

        /// <summary>
        /// Local upload mode: wait for client WAV, then download from Miko via PBX Gateway if needed.
        /// </summary>
        private async Task<string?> ResolveRecordingPathForManualUploadAsync(MainWindow? mainWindow)
        {
            string? recordingFile = CallWindowHelpers.ResolveLocalClientRecordingPath(_callItem);
            if (string.IsNullOrEmpty(recordingFile)
                && CallWindowHelpers.IsLocalClientRecordingPath(_callItem.RecordingFilePath))
                recordingFile = _callItem.RecordingFilePath;

            if (!string.IsNullOrEmpty(recordingFile) && !IsUsableRecordingFile(recordingFile))
            {
                for (int i = 0; i < 90; i++)
                {
                    await Task.Delay(1000).ConfigureAwait(true);
                    recordingFile = CallWindowHelpers.ResolveLocalClientRecordingPath(_callItem) ?? recordingFile;
                    if (IsUsableRecordingFile(recordingFile))
                        break;
                }
            }

            if (IsUsableRecordingFile(recordingFile))
                return recordingFile;

            if (mainWindow?.GetMikoPbxCdrService() == null)
                return recordingFile;

            MainWindow.Log("[CallDetailsWindow] Manual upload: no local WAV — downloading recording from Miko via PBX Gateway");
            try
            {
                var (pbxPath, _) = await mainWindow.DownloadPbxRecordingForCallAsync(_callItem).ConfigureAwait(true);
                if (IsUsableRecordingFile(pbxPath))
                {
                    MainWindow.Log($"[CallDetailsWindow] Manual upload: Miko recording ready: {pbxPath}");
                    return pbxPath;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Manual upload: Miko download failed: {ex.Message}");
            }

            return recordingFile;
        }

        /// <summary>
        /// Нет аудиофайла: можно добавить заметку недозвона, если абонент не ответил и звонок уже завершён.
        /// AnswerTime — надёжнее WasAnswered (флаг мог ошибочно выставиться на SIP 200 OK к CANCEL).
        /// Также допускаем недозвон, если AnswerTime выглядит ложным (нет записи, большой разрыв после гудков).
        /// </summary>
        private bool CanAddMissedCallNoteWhenNoRecording()
        {
            bool callFinished = _callItem.Status != CallStatus.Calling
                && _callItem.Status != CallStatus.Connected;
            if (!callFinished)
                return false;

            if (!_callItem.AnswerTime.HasValue)
                return true;

            if (_callItem.InboundRtpPackets == 0)
                return true;

            // Отменённый/неуспешный исходящий — не было разговора
            if (_callItem.Status == CallStatus.Cancelled || _callItem.Status == CallStatus.Failed)
                return true;

            if (LooksLikeFalseAnswerTime())
                return true;

            return false;
        }

        /// <summary>
        /// AnswerTime есть, но записи нет и тайминги не сходятся — типичный ложный «ответ» (SIP 200 OK не к INVITE,
        /// или данные второго звонка попали в строку первого).
        /// </summary>
        private bool LooksLikeFalseAnswerTime()
        {
            if (_callItem.InboundRtpPackets == 0)
                return true;

            if (!_callItem.AnswerTime.HasValue)
                return false;

            DateTime? ringEnd = _callItem.RingbackEndTime ?? _callItem.RingbackStartTime;
            if (ringEnd.HasValue)
            {
                double gapAfterRingSec = (_callItem.AnswerTime.Value - ringEnd.Value).TotalSeconds;
                if (gapAfterRingSec > 15)
                    return true;
            }
            else
            {
                double gapFromDialSec = (_callItem.AnswerTime.Value - _callItem.CallTime).TotalSeconds;
                if (gapFromDialSec > 45)
                    return true;
            }

            return false;
        }

        private void RefreshCallStatusDisplay()
        {
            StatusTextBlock.Text = CallStatusPresentation.FormatSummary(_callItem);
            StatusTextBlock.Foreground = CallStatusPresentation.GetForegroundBrush(_callItem);

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
                ? new SolidColorBrush(Color.FromRgb(34, 197, 94))
                : new SolidColorBrush(Color.FromRgb(239, 68, 68));

            AnswerTimeTextBlock.Text = _callItem.AnswerTime.HasValue
                ? _callItem.AnswerTime.Value.ToString("HH:mm:ss.fff")
                : "N/A";
        }

        private void RefreshCallItemFromHistory()
        {
            try
            {
                var latest = new CallHistoryService().GetCall(_callItem.PhoneNumber, _callItem.CallTime);
                if (latest != null)
                    _callItem = latest;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] WARNING: Failed to refresh call item before upload: {ex.Message}");
            }
        }

        private async void UploadCallResultButton_Click(object sender, RoutedEventArgs e)
        {
            Window? msgOwner = (IsLoaded && IsVisible) ? this : null;
            void ResetUploadButton()
            {
                UploadCallResultButton.IsEnabled = true;
                UploadCallResultButton.Content = IsGatewayKommoRecordingUploadSource()
                    ? UploadCallResultButtonGatewayCaption
                    : UploadCallResultButtonDefaultCaption;
            }

            try
            {
                StopAmoCrmRefreshTimer();
                RefreshCallItemFromHistory();

                var mainWindow = Application.Current.MainWindow as MainWindow;

                bool gatewayUpload = IsGatewayKommoRecordingUploadSource();
                bool clientRecordingEnabled = false;
                try
                {
                    clientRecordingEnabled = AppDataHelper.LoadSettingsOrNew().EnableCallRecording;
                }
                catch { /* ignore */ }

                string? recordingFile;
                if (gatewayUpload)
                {
                    recordingFile = CallWindowHelpers.ResolveLocalClientRecordingPath(_callItem);
                    if (string.IsNullOrEmpty(recordingFile)
                        && CallWindowHelpers.IsLocalClientRecordingPath(_callItem.RecordingFilePath))
                        recordingFile = _callItem.RecordingFilePath;

                    if (!string.IsNullOrEmpty(recordingFile) && !IsUsableRecordingFile(recordingFile))
                    {
                        UploadCallResultButton.IsEnabled = false;
                        UploadCallResultButton.Content = "Waiting for local file...";
                        for (int i = 0; i < 90; i++)
                        {
                            await Task.Delay(1000);
                            recordingFile = CallWindowHelpers.ResolveLocalClientRecordingPath(_callItem) ?? recordingFile;
                            if (IsUsableRecordingFile(recordingFile))
                                break;
                        }
                    }
                }
                else
                {
                    UploadCallResultButton.IsEnabled = false;
                    UploadCallResultButton.Content = "Preparing recording...";
                    recordingFile = await ResolveRecordingPathForManualUploadAsync(mainWindow).ConfigureAwait(true);
                }

                bool hasRecording = IsUsableRecordingFile(recordingFile);
                bool canMissedNote = CanAddMissedCallNoteWhenNoRecording();

                if (!hasRecording && !canMissedNote && !(gatewayUpload && !clientRecordingEnabled))
                {
                    CustomMessageBox.Show(
                        "No local recording file found.\n\nEnsure call recording is enabled in settings and the WAV finished converting.\n\nA missed-call note can only be added when the call was not answered.",
                        "No recording",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        msgOwner);

                    ResetUploadButton();
                    return;
                }

                if (mainWindow == null)
                {
                    CustomMessageBox.Show(
                        "Cannot access AmoCRM service.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        msgOwner);
                    ResetUploadButton();
                    return;
                }

                var amoCrmService = mainWindow.GetAmoCrmService();
                var pbxGateway = mainWindow.GetMikoPbxCdrService();

                if (gatewayUpload)
                {
                    if (pbxGateway == null)
                    {
                        CustomMessageBox.Show(
                            "PBX Gateway is not configured.\n\nConfigure PBX Gateway in settings for gateway Kommo upload.",
                            "PBX Gateway Not Available",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            msgOwner);
                        ResetUploadButton();
                        return;
                    }
                }
                else if (amoCrmService == null || !amoCrmService.IsInitialized)
                {
                    CustomMessageBox.Show(
                        "AmoCRM service is not initialized.\n\nPlease check your AmoCRM settings.",
                        "AmoCRM Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        msgOwner);
                    ResetUploadButton();
                    return;
                }

                if (gatewayUpload && (amoCrmService == null || !amoCrmService.IsInitialized) && !_callItem.AmoCrmLeadId.HasValue)
                {
                    CustomMessageBox.Show(
                        "Kommo is not connected.\n\nInitialize Kommo integration in settings before manual upload.",
                        "Kommo Not Available",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        msgOwner);
                    ResetUploadButton();
                    return;
                }

                UploadCallResultButton.IsEnabled = false;
                UploadCallResultButton.Content = hasRecording ? "Uploading..." : "Adding to AmoCRM...";

                bool attachToContact = false;
                long? selectedLeadId = null;
                long? selectedContactId = null;

                if (_callItem.AmoCrmLeadId.HasValue)
                {
                    selectedLeadId = _callItem.AmoCrmLeadId.Value;
                    MainWindow.Log($"[CallDetailsWindow] Manual Amo: using lead #{selectedLeadId.Value} from call history (browser/previous upload)");
                }
                else
                {
                    // Сначала только открытые сделки; если API по контактам даст пустой ответ,
                    // отдельным шагом ниже включим закрытые — иначе «Upload call» бессилен при одной закрытой сделке.
                    var leads = await amoCrmService.GetLeadsByPhoneAsync(_callItem.PhoneNumber, openLeadsOnly: true, ownLeadsOnly: true);
                    if (leads == null || leads.Count == 0)
                    {
                        leads = await amoCrmService.GetLeadsByPhoneAsync(_callItem.PhoneNumber, openLeadsOnly: false, ownLeadsOnly: true);
                        if (leads.Count > 0)
                        {
                            MainWindow.Log($"[CallDetailsWindow] Manual Amo: no open deals; offering {leads.Count} lead(s) including closed");
                        }
                    }

                    if (leads == null || leads.Count == 0)
                {
                    var choice = CustomMessageBox.Show(
                        "There is no open deal for this contact.\n\nAttach the call result to the contact?",
                        "No open deal",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question,
                        msgOwner,
                        "Yes",
                        "No");

                    if (choice != MessageBoxResult.Yes)
                    {
                        ResetUploadButton();
                        return;
                    }

                    selectedContactId = await amoCrmService.FindContactByPhoneAsync(_callItem.PhoneNumber);
                    if (!selectedContactId.HasValue)
                    {
                        CustomMessageBox.Show(
                            "No contact with this phone number was found in AmoCRM.",
                            "Contact not found",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            msgOwner);
                        ResetUploadButton();
                        return;
                    }

                    attachToContact = true;
                    MainWindow.Log($"[CallDetailsWindow] Manual Amo: attach call result to contact {selectedContactId.Value} (no open leads)");
                }
                else
                {
                    string? subdomain = amoCrmService.KommoSubdomain;
                    if (string.IsNullOrWhiteSpace(subdomain))
                    {
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
                    }

                    var selectionWindow = new LeadSelectionWindow(leads, subdomain);
                    if (IsLoaded && IsVisible)
                        selectionWindow.Owner = this;

                    bool? selectionResult = selectionWindow.ShowDialog();
                    if (selectionResult == true && selectionWindow.SelectedLeadId.HasValue)
                    {
                        selectedLeadId = selectionWindow.SelectedLeadId.Value;
                        MainWindow.Log($"[CallDetailsWindow] User selected lead ID: {selectedLeadId} for manual Amo call result");
                    }
                    else
                    {
                        MainWindow.Log($"[CallDetailsWindow] User cancelled lead selection for manual Amo call result");

                        _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Cancelled;
                        _callItem.AmoCrmUploadReason = "User cancelled lead selection";
                        var callHistoryService = new CallHistoryService();
                        callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Cancelled, "User cancelled lead selection");
                        UpdateAmoCrmUploadStatusDisplay();

                        ResetUploadButton();
                        return;
                    }

                    if (!selectedLeadId.HasValue)
                    {
                        CustomMessageBox.Show(
                            "No lead selected.\n\nCannot send this call to AmoCRM.",
                            "No Lead Selected",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning,
                            msgOwner);
                        ResetUploadButton();
                        return;
                    }
                }
                }

                if (gatewayUpload)
                {
                    bool gatewayOk = await ManualUploadViaGatewayKommoAsync(
                        pbxGateway!,
                        attachToContact ? null : selectedLeadId,
                        msgOwner).ConfigureAwait(true);
                    ResetUploadButton();
                    if (gatewayOk)
                        UpdateAmoCrmUploadStatusDisplay();
                    return;
                }

                bool isIncoming = _callItem.IsIncoming;
                string? callFromLabel = AmoCallFromLabelResolver.Resolve(_callItem);

                if (!hasRecording)
                {
                    bool success;
                    if (attachToContact)
                        success = await amoCrmService.ManuallyUploadMissedCallToContactAsync(
                            selectedContactId!.Value,
                            _callItem.PhoneNumber,
                            isIncoming,
                            _callItem.CallTime,
                            callFromLabel);
                    else
                        success = await amoCrmService.ManuallyUploadMissedCallToLeadAsync(
                            selectedLeadId!.Value,
                            _callItem.PhoneNumber,
                            isIncoming,
                            _callItem.CallTime,
                            callFromLabel);

                    if (success)
                    {
                        var callHistoryService = new CallHistoryService();
                        if (attachToContact)
                        {
                            CustomMessageBox.Show(
                                "Missed-call note was added to the contact in AmoCRM.",
                                "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information,
                                msgOwner);

                            _callItem.AmoCrmLeadId = null;
                            callHistoryService.UpdateAmoCrmLeadId(_callItem.PhoneNumber, _callItem.CallTime, null);
                            AmoCrmLeadTextBlock.Text = "Contact";
                            OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                            _amoCrmContactId = selectedContactId;
                            OpenAmoCrmContactButton.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            CustomMessageBox.Show(
                                $"Missed-call note was added to AmoCRM lead {selectedLeadId!.Value}.",
                                "Success",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information,
                                msgOwner);

                            _callItem.AmoCrmLeadId = selectedLeadId;
                            AmoCrmLeadTextBlock.Text = $"Lead #{selectedLeadId.Value}";
                            OpenAmoCrmLeadButton.Visibility = Visibility.Visible;
                            OpenAmoCrmContactButton.Visibility = Visibility.Collapsed;
                        }

                        _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Uploaded;
                        _callItem.AmoCrmUploadReason = null;
                        callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Uploaded, null);
                        UpdateAmoCrmUploadStatusDisplay();
                    }
                    else
                    {
                        CustomMessageBox.Show(
                            "Failed to add the missed-call note.\n\nPlease check the logs for details.",
                            "Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error,
                            msgOwner);

                        _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Failed;
                        _callItem.AmoCrmUploadReason = "Manual missed-call note failed";
                        var callHistoryService = new CallHistoryService();
                        callHistoryService.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Failed, "Manual missed-call note failed");
                        UpdateAmoCrmUploadStatusDisplay();
                    }

                    ResetUploadButton();
                    return;
                }

                int durationSeconds;
                if (_callItem.Duration.HasValue && _callItem.Duration.Value.TotalSeconds > 0.5)
                {
                    durationSeconds = (int)Math.Round(_callItem.Duration.Value.TotalSeconds);
                }
                else
                {
                    durationSeconds = CallWindowHelpers.EstimateWavDurationSeconds(recordingFile);
                    if (durationSeconds <= 0)
                    {
                        try
                        {
                            var fi = new FileInfo(recordingFile!);
                            double bytesPerSecond = 48000.0 * 2.0;
                            durationSeconds = (int)Math.Max(1, Math.Round(fi.Length / bytesPerSecond));
                            MainWindow.Log($"[CallDetailsWindow] Estimated call duration from local file size: {durationSeconds} s (length={fi.Length} bytes)");
                        }
                        catch (Exception ex)
                        {
                            MainWindow.Log($"[CallDetailsWindow] Error estimating duration from file: {ex.Message}");
                            durationSeconds = 0;
                        }
                    }
                }

                bool wasAnswered = _callItem.WasAnswered;
                var chs = new CallHistoryService();

                bool uploadOk;
                if (attachToContact)
                {
                    uploadOk = await amoCrmService.ManuallyUploadRecordingToContactAsync(
                        selectedContactId!.Value,
                        recordingFile!,
                        _callItem.PhoneNumber,
                        isIncoming,
                        durationSeconds,
                        wasAnswered,
                        callFromLabel);
                }
                else
                {
                    uploadOk = await amoCrmService.ManuallyUploadRecordingToLeadAsync(
                        selectedLeadId!.Value,
                        recordingFile!,
                        _callItem.PhoneNumber,
                        isIncoming,
                        durationSeconds,
                        wasAnswered,
                        callFromLabel);
                }

                if (uploadOk)
                {
                    if (attachToContact)
                    {
                        CustomMessageBox.Show(
                            "Recording was uploaded and attached to the contact in AmoCRM.",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            msgOwner);

                        _callItem.AmoCrmLeadId = null;
                        chs.UpdateAmoCrmLeadId(_callItem.PhoneNumber, _callItem.CallTime, null);
                        AmoCrmLeadTextBlock.Text = "Contact";
                        OpenAmoCrmLeadButton.Visibility = Visibility.Collapsed;
                        _amoCrmContactId = selectedContactId;
                        OpenAmoCrmContactButton.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        CustomMessageBox.Show(
                            $"Recording was uploaded to AmoCRM lead {selectedLeadId!.Value}.",
                            "Success",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            msgOwner);

                        _callItem.AmoCrmLeadId = selectedLeadId;
                        AmoCrmLeadTextBlock.Text = $"Lead #{selectedLeadId.Value}";
                        OpenAmoCrmLeadButton.Visibility = Visibility.Visible;
                        OpenAmoCrmContactButton.Visibility = Visibility.Collapsed;
                    }

                    PlayRecordingButton.IsEnabled = IsUsableRecordingFile(recordingFile);
                    OpenFolderButton.IsEnabled = true;

                    _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Uploaded;
                    _callItem.AmoCrmUploadReason = null;
                    var detectedSource = CallWindowHelpers.DetectRecordingUploadSource(recordingFile);
                    _callItem.AmoCrmUploadedRecordingSource = detectedSource;
                    chs.UpdateAmoCrmUploadStatus(
                        _callItem.PhoneNumber,
                        _callItem.CallTime,
                        AmoCrmUploadStatus.Uploaded,
                        null,
                        detectedSource != AmoCrmUploadedRecordingSource.None ? detectedSource : AmoCrmUploadedRecordingSource.Local);
                    UpdateAmoCrmUploadStatusDisplay();
                }
                else
                {
                    CustomMessageBox.Show(
                        "Failed to upload the recording.\n\nPlease check the logs for details.",
                        "Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        msgOwner);

                    _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Failed;
                    _callItem.AmoCrmUploadReason = "Manual upload failed";
                    chs.UpdateAmoCrmUploadStatus(_callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Failed, "Manual upload failed");
                    UpdateAmoCrmUploadStatusDisplay();
                }

                ResetUploadButton();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error sending call result to AmoCRM: {ex.Message}");
                CustomMessageBox.Show(
                    $"Error:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    msgOwner);
                UploadCallResultButton.IsEnabled = true;
                UploadCallResultButton.Content = IsGatewayKommoRecordingUploadSource()
                    ? UploadCallResultButtonGatewayCaption
                    : UploadCallResultButtonDefaultCaption;
            }
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

        private static string? TryGetAmoCrmWebBaseUrl()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (File.Exists(settingsFilePath))
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (settings != null && !string.IsNullOrEmpty(settings.AmoCrmSubdomain))
                    {
                        string? fromSettings = AmoCrmAccountUrl.TryBuildWebBaseUrl(settings.AmoCrmSubdomain);
                        if (!string.IsNullOrEmpty(fromSettings))
                            return fromSettings;
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error reading AmoCRM settings: {ex.Message}");
            }

            var mainWindow = Application.Current.MainWindow as MainWindow;
            return mainWindow?.GetAmoCrmService()?.GetAccountWebBaseUrl();
        }

        private void OpenAmoCrmLeadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_callItem.AmoCrmLeadId.HasValue)
                {
                    return;
                }

                string? amoCrmUrl = TryGetAmoCrmWebBaseUrl();

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

                string? amoCrmUrl = TryGetAmoCrmWebBaseUrl();

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

                bool callStatusChanged = latest.WasAnswered != _callItem.WasAnswered
                    || latest.Status != _callItem.Status
                    || latest.Duration != _callItem.Duration
                    || latest.AnswerTime != _callItem.AnswerTime;

                // Проверяем, обновился ли LeadId или статус загрузки
                bool needsUpdate = false;
                if (latest.AmoCrmLeadId.HasValue != _callItem.AmoCrmLeadId.HasValue)
                {
                    _callItem.AmoCrmLeadId = latest.AmoCrmLeadId;
                    needsUpdate = true;
                }
                
                // Do not overwrite a newer in-memory status set by manual upload in this window.
                if (latest.AmoCrmUploadStatus != _callItem.AmoCrmUploadStatus &&
                    _callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.NotUploaded)
                {
                    _callItem.AmoCrmUploadStatus = latest.AmoCrmUploadStatus;
                    _callItem.AmoCrmUploadReason = latest.AmoCrmUploadReason;
                    _callItem.AmoCrmUploadedRecordingSource = latest.AmoCrmUploadedRecordingSource;
                    needsUpdate = true;
                }
                else if (latest.AmoCrmUploadedRecordingSource != _callItem.AmoCrmUploadedRecordingSource
                         && latest.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded
                         && _callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded
                         && _callItem.AmoCrmUploadedRecordingSource == AmoCrmUploadedRecordingSource.None
                         && latest.AmoCrmUploadedRecordingSource != AmoCrmUploadedRecordingSource.None)
                {
                    _callItem.AmoCrmUploadedRecordingSource = latest.AmoCrmUploadedRecordingSource;
                    needsUpdate = true;
                }

                if (callStatusChanged)
                {
                    _callItem.WasAnswered = latest.WasAnswered;
                    _callItem.Status = latest.Status;
                    _callItem.Duration = latest.Duration;
                    _callItem.AnswerTime = latest.AnswerTime;
                    RefreshCallStatusDisplay();
                    needsUpdate = true;
                    MainWindow.Log($"[CallDetailsWindow] Call status updated via refresh: Status={_callItem.Status}, WasAnswered={_callItem.WasAnswered}");
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

                // Проверяем файл записи — в UI всегда показываем локальный WAV, не PBX cache
                var localFromHistory = CallWindowHelpers.ResolveLocalClientRecordingPath(latest);
                if (!string.IsNullOrEmpty(localFromHistory))
                    _callItem.RecordingFilePath = localFromHistory;
                else if (!string.IsNullOrEmpty(latest.RecordingFilePath) && string.IsNullOrEmpty(_callItem.RecordingFilePath))
                    _callItem.RecordingFilePath = latest.RecordingFilePath;

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
                    if (!string.IsNullOrEmpty(_callItem.AmoCrmUploadReason)
                        && (_callItem.AmoCrmUploadReason.Contains("recording unavailable", StringComparison.OrdinalIgnoreCase)
                            || _callItem.AmoCrmUploadReason.Contains("no PBX recording", StringComparison.OrdinalIgnoreCase)
                            || _callItem.AmoCrmUploadReason.Contains("no recording", StringComparison.OrdinalIgnoreCase)))
                    {
                        statusText = $"⚠️ Call note only (no recording): {_callItem.AmoCrmUploadReason}";
                        statusColor = new SolidColorBrush(Color.FromRgb(234, 179, 8));
                    }
                    else if (!string.IsNullOrEmpty(_callItem.AmoCrmUploadReason))
                    {
                        statusText = $"✅ Uploaded: {_callItem.AmoCrmUploadReason}";
                        statusColor = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    }
                    else
                    {
                        statusText = "✅ Uploaded successfully";
                        statusColor = new SolidColorBrush(Color.FromRgb(34, 197, 94));
                    }
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
            UpdateAmoCrmUploadedRecordingSourceDisplay();

            if (_isAmoCrmIntegrationEnabled)
                UpdateCrmManualUploadSectionVisibility(true);
        }

        private void UpdateAmoCrmUploadedRecordingSourceDisplay()
        {
            if (AmoCrmUploadedRecordingSourceTextBlock == null
                || AmoCrmUploadedRecordingSourceLabel == null)
                return;

            bool show = _callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Uploaded
                        && _callItem.AmoCrmUploadedRecordingSource != AmoCrmUploadedRecordingSource.None;

            AmoCrmUploadedRecordingSourceLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AmoCrmUploadedRecordingSourceTextBlock.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (!show)
                return;

            AmoCrmUploadedRecordingSourceTextBlock.Text =
                CallWindowHelpers.GetUploadedRecordingSourceLabel(_callItem.AmoCrmUploadedRecordingSource);
        }

        private static bool IsGatewayKommoRecordingUploadSource()
        {
            try
            {
                var source = AppDataHelper.LoadSettingsOrNew().AmoCrmRecordingUploadSource?.Trim().ToLowerInvariant();
                return string.Equals(source, "gateway", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLocalKommoRecordingUploadSource()
        {
            try
            {
                var settings = AppDataHelper.LoadSettingsOrNew();
                if (!settings.EnableAmoCrmRecordingUpload)
                    return false;

                var source = settings.AmoCrmRecordingUploadSource?.Trim().ToLowerInvariant();
                return !string.Equals(source, "gateway", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private void UpdateCrmManualUploadSectionVisibility(bool isAmoCrmEnabled)
        {
            bool gatewayUpload = IsGatewayKommoRecordingUploadSource();
            bool localUpload = IsLocalKommoRecordingUploadSource();
            bool showGatewayRetry = gatewayUpload
                && (_callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Failed
                    || _callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.NotUploaded
                    || _callItem.AmoCrmUploadStatus == AmoCrmUploadStatus.Cancelled);
            bool show = isAmoCrmEnabled && (localUpload || showGatewayRetry);

            if (AmoCrmManualUploadBorder != null)
                AmoCrmManualUploadBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (UploadCallResultButton != null)
            {
                UploadCallResultButton.Content = gatewayUpload && showGatewayRetry
                    ? UploadCallResultButtonGatewayCaption
                    : UploadCallResultButtonDefaultCaption;
            }

            if (show && localUpload)
                _ = LoadCrmSectionRecordingAsync();
            else if (show && gatewayUpload)
                SetCrmRecordingUi(null, "Recording will be fetched from Miko PBX by Gateway", false);
            else
                SetCrmRecordingUi(null, null, false);
        }

        private void SetCrmRecordingUi(string? path, string? label, bool playable)
        {
            _crmSectionRecordingPath = path;
            if (CrmRecordingFileTextBlock != null)
                CrmRecordingFileTextBlock.Text = label ?? "No recording available";
            if (PlayCrmRecordingButton != null)
                PlayCrmRecordingButton.IsEnabled = playable;
        }

        private string? ResolveExistingPbxRecordingPath()
        {
            if (CallWindowHelpers.IsPbxDownloadedRecordingPath(_callItem.RecordingFilePath)
                && CallWindowHelpers.IsRecordingUsableForUpload(_callItem.RecordingFilePath))
            {
                return _callItem.RecordingFilePath;
            }

            try
            {
                string pbxDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Callspire", "Recordings", "PBX");
                if (!Directory.Exists(pbxDir))
                    return null;

                return Directory.GetFiles(pbxDir, "mikopbx_*.*")
                    .Where(CallWindowHelpers.IsRecordingUsableForUpload)
                    .OrderBy(p => Math.Abs((new FileInfo(p).LastWriteTime - _callItem.CallTime).TotalSeconds))
                    .FirstOrDefault(p => Math.Abs((new FileInfo(p).LastWriteTime - _callItem.CallTime).TotalSeconds) < 300);
            }
            catch
            {
                return null;
            }
        }

        private async Task LoadCrmSectionRecordingAsync()
        {
            if (AmoCrmManualUploadBorder?.Visibility != Visibility.Visible)
                return;

            SetCrmRecordingUi(null, "Loading recording...", false);

            string? localPath = CallWindowHelpers.ResolveLocalClientRecordingPath(_callItem);
            if (IsUsableRecordingFile(localPath))
            {
                SetCrmRecordingUi(localPath, $"Local: {Path.GetFileName(localPath)}", true);
                return;
            }

            string? path = ResolveExistingPbxRecordingPath();
            if (string.IsNullOrEmpty(path))
            {
                var mainWindow = Application.Current.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    try
                    {
                        var (downloaded, _) = await mainWindow.DownloadPbxRecordingForCallAsync(_callItem).ConfigureAwait(true);
                        path = downloaded;
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[CallDetailsWindow] Failed to download Miko recording: {ex.Message}");
                    }
                }
            }

            if (string.IsNullOrEmpty(path))
            {
                SetCrmRecordingUi(null, "No recording available", false);
                return;
            }

            bool playable = CallWindowHelpers.IsRecordingUsableForUpload(path);
            string label = CallWindowHelpers.IsPbxDownloadedRecordingPath(path)
                ? $"Miko PBX: {Path.GetFileName(path)}"
                : Path.GetFileName(path)!;
            SetCrmRecordingUi(path, label, playable);
        }

        private void PlayCrmRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_crmSectionRecordingPath)
                    || !CallWindowHelpers.IsRecordingUsableForUpload(_crmSectionRecordingPath))
                {
                    CustomMessageBox.Show(
                        "Recording file is not available yet.\n\nPlease wait a moment and try again.",
                        "File Not Ready",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = _crmSectionRecordingPath,
                    UseShellExecute = true
                });
                MainWindow.Log($"[CallDetailsWindow] CRM section recording opened: {_crmSectionRecordingPath}");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[CallDetailsWindow] Error playing CRM section recording: {ex.Message}");
                CustomMessageBox.Show(
                    $"Could not open recording file:\n{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
        }

        private async Task<bool> ManualUploadViaGatewayKommoAsync(
            MikoPbxCdrService pbxGateway,
            long? selectedLeadId,
            Window? msgOwner)
        {
            bool isIncoming = _callItem.IsIncoming;
            bool wasAnswered = _callItem.WasAnswered || _callItem.AnswerTime.HasValue;
            int durationSeconds = _callItem.Duration.HasValue
                ? (int)Math.Round(_callItem.Duration.Value.TotalSeconds)
                : 0;
            string? callFromLabel = AmoCallFromLabelResolver.Resolve(_callItem);
            var settings = AppDataHelper.LoadSettingsOrNew();
            bool enableRecordingUpload = settings.EnableAmoCrmRecordingUpload;

            string connectionSlot = _callItem.ConnectionSlot == CallConnectionSlot.Secondary
                ? "secondary"
                : "main";

            var request = new KommoProcessCallRetryRequest
            {
                Phone = _callItem.PhoneNumber,
                CallTime = CallWindowHelpers.FormatCallTimeUtcIso(_callItem.CallTime),
                IsIncoming = isIncoming,
                DurationSeconds = durationSeconds,
                WasAnswered = wasAnswered,
                LeadId = selectedLeadId,
                ClientRecordingEnabled = false,
                ConnectionSlot = connectionSlot,
                CallFromLabel = callFromLabel,
                EnableRecordingUpload = enableRecordingUpload,
                AnswerTime = _callItem.AnswerTime?.ToUniversalTime().ToString("o"),
                CallEndTime = CallWindowHelpers.FormatCallEndTimeIso(_callItem),
            };

            MainWindow.Log("[CallDetailsWindow] Gateway Kommo: manual retry/process-call (Miko PBX recording)");
            var (job, error) = await pbxGateway.RetryKommoProcessCallAsync(request).ConfigureAwait(true);
            if (job == null || string.IsNullOrWhiteSpace(job.Id))
            {
                CustomMessageBox.Show(
                    $"Gateway Kommo upload failed.\n\n{error ?? "Unknown error"}",
                    "Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    msgOwner);
                _callItem.AmoCrmUploadStatus = AmoCrmUploadStatus.Failed;
                _callItem.AmoCrmUploadReason = error;
                new CallHistoryService().UpdateAmoCrmUploadStatus(
                    _callItem.PhoneNumber, _callItem.CallTime, AmoCrmUploadStatus.Failed, error);
                return false;
            }

            KommoProcessCallJobStatus? finalStatus = null;
            for (int i = 0; i < 90; i++)
            {
                await Task.Delay(2000).ConfigureAwait(true);
                finalStatus = await pbxGateway.GetKommoProcessCallStatusAsync(job.Id).ConfigureAwait(true);
                if (finalStatus == null)
                    continue;
                string st = finalStatus.Status?.ToLowerInvariant() ?? "";
                if (st is "uploaded" or "failed" or "skipped")
                    break;
            }

            if (finalStatus == null)
            {
                CustomMessageBox.Show(
                    "Gateway Kommo upload timed out waiting for status.",
                    "Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning,
                    msgOwner);
                return false;
            }

            var uploadStatus = (finalStatus.Status ?? "").ToLowerInvariant() switch
            {
                "uploaded" => AmoCrmUploadStatus.Uploaded,
                "failed" => AmoCrmUploadStatus.Failed,
                _ => AmoCrmUploadStatus.NotUploaded,
            };

            AmoCrmUploadedRecordingSource? source = null;
            if (!string.IsNullOrEmpty(finalStatus.UploadSource))
            {
                if (finalStatus.UploadSource.Equals("miko_pbx", StringComparison.OrdinalIgnoreCase))
                    source = AmoCrmUploadedRecordingSource.MikoPbx;
                else if (finalStatus.UploadSource.Equals("local", StringComparison.OrdinalIgnoreCase)
                         || finalStatus.UploadSource.Equals("client", StringComparison.OrdinalIgnoreCase))
                    source = AmoCrmUploadedRecordingSource.Local;
            }

            _callItem.AmoCrmUploadStatus = uploadStatus;
            _callItem.AmoCrmUploadReason = finalStatus.Reason;
            if (source.HasValue)
                _callItem.AmoCrmUploadedRecordingSource = source.Value;
            if (finalStatus.LeadId.HasValue)
                _callItem.AmoCrmLeadId = finalStatus.LeadId;

            var chs = new CallHistoryService();
            chs.UpdateAmoCrmUploadStatus(
                _callItem.PhoneNumber,
                _callItem.CallTime,
                uploadStatus,
                finalStatus.Reason,
                source);
            if (finalStatus.LeadId.HasValue)
                chs.UpdateAmoCrmLeadId(_callItem.PhoneNumber, _callItem.CallTime, finalStatus.LeadId);

            string? sessionId = _callItem.Transport == CallTransport.WebRtc
                ? _callItem.WebRtcSessionId
                : _callItem.SipCallId;
            if (chs.ApplyPbxTruthFromGateway(
                    _callItem.PhoneNumber,
                    _callItem.CallTime,
                    finalStatus.PbxWasAnswered,
                    finalStatus.PbxDurationSeconds,
                    sessionId))
            {
                var refreshed = chs.GetCall(_callItem.PhoneNumber, _callItem.CallTime);
                if (refreshed != null)
                    _callItem = refreshed;
                RefreshCallStatusDisplay();
            }

            if (uploadStatus == AmoCrmUploadStatus.Uploaded)
            {
                CustomMessageBox.Show(
                    "Call was processed via PBX Gateway Kommo.",
                    "Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    msgOwner);
                return true;
            }

            CustomMessageBox.Show(
                $"Gateway Kommo upload failed.\n\n{finalStatus.Reason ?? finalStatus.Status}",
                "Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error,
                msgOwner);
            return false;
        }
    }
}


#endif // WINDOWS
