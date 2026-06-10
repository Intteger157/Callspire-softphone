using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;

namespace Softphone
{
    public partial class LogWindow : Window
    {
        private readonly List<string> _allLogs = new List<string>();
        private readonly List<string> _webRtcLogs = new List<string>();
        private readonly List<string> _amoLogs = new List<string>();
        private readonly List<string> _sipLogs = new List<string>();
        private readonly List<string> _callLogs = new List<string>();

        // Путь к сегодняшнему файлу лога для полного экспорта
        private string? _logFilePath;
        
        private const int MaxLogsPerCategory = 2000; // Ограничение для категорий в UI

        public LogWindow()
        {
            InitializeComponent();
            NativeWindowAppearanceManager.Attach(this);
            _logFilePath = FileLogService.Instance.GetCurrentLogFilePath();
            
            // Подписываемся на изменение активной вкладки для обновления отображения
            if (LogTabControl != null)
            {
                LogTabControl.SelectionChanged += LogTabControl_SelectionChanged;
            }
        }
        
        private void LogTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshActiveTab();
        }

        public void AddLog(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;

            string timestampedMessage = $"[{DateTime.Now:HH:mm:ss}] {message}";
            
            // Добавляем во все логи
            AddToCategory(_allLogs, AllLogsTextBox, timestampedMessage);
            
            // Определяем категорию и добавляем в соответствующую
            if (IsWebRtcLog(message))
            {
                AddToCategory(_webRtcLogs, WebRtcLogTextBox, timestampedMessage);
            }
            else if (IsAmoLog(message))
            {
                AddToCategory(_amoLogs, AmoLogTextBox, timestampedMessage);
            }
            else if (IsSipLog(message))
            {
                AddToCategory(_sipLogs, SipLogTextBox, timestampedMessage);
            }
            else if (IsCallLog(message))
            {
                AddToCategory(_callLogs, CallLogTextBox, timestampedMessage);
            }
        }

        private void AddToCategory(List<string> logList, TextBox? textBox, string message)
        {
            if (textBox == null)
                return;

            logList.Add(message);
            
            // Ограничиваем размер
            if (logList.Count > MaxLogsPerCategory)
            {
                logList.RemoveAt(0);
            }
            
            // Обновляем TextBox только если он видим (активная вкладка)
            // Это оптимизация - не обновляем невидимые вкладки в реальном времени
            if (textBox.IsVisible)
            {
                textBox.AppendText(message + "\n");
                textBox.ScrollToEnd();
            }
        }

        private bool IsWebRtcLog(string message)
        {
            return message.Contains("[WebRtc", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[WebRTC", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[WebRtcService", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[WebRtcEngineHost", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[WebRtcStatusService", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[WebRtcCallRecorder", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsAmoLog(string message)
        {
            // Проверяем различные варианты префиксов AmoCRM
            if (message.Contains("[AmoCrm", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("[AmoCRM", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            
            // Логи из MainWindow с упоминанием AmoCRM
            if (message.Contains("[MainWindow]", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Contains("AmoCRM", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("AmoCrm", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            // Логи из SettingsWindow с упоминанием AmoCRM
            if (message.Contains("[SettingsWindow]", StringComparison.OrdinalIgnoreCase))
            {
                if (message.Contains("AmoCRM", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("AmoCrm", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            
            return false;
        }

        private bool IsSipLog(string message)
        {
            return message.Contains("[Sip", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[SIP", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[SipService", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCallLog(string message)
        {
            return message.Contains("[Call", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[CallWindow", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[CallHistory", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[CallDetails", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[CallRecorder", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("[RtpCallRecorder", StringComparison.OrdinalIgnoreCase) ||
                   (message.Contains("[MainWindow", StringComparison.OrdinalIgnoreCase) && 
                    (message.Contains("call", StringComparison.OrdinalIgnoreCase) || 
                     message.Contains("Call", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("incoming", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("outgoing", StringComparison.OrdinalIgnoreCase)));
        }

        public void SetLogs(string logs)
        {
            // Очищаем все категории
            _allLogs.Clear();
            _webRtcLogs.Clear();
            _amoLogs.Clear();
            _sipLogs.Clear();
            _callLogs.Clear();
            
            // Разбиваем логи на строки и добавляем их
            if (string.IsNullOrEmpty(logs))
            {
                RefreshActiveTab();
                return;
            }

            string[] lines = logs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            
            foreach (string line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // Извлекаем сообщение (убираем timestamp если есть)
                    string message = line.Trim();
                    if (message.StartsWith("[") && message.Length > 10)
                    {
                        int endIndex = message.IndexOf(']', 1);
                        if (endIndex > 0 && endIndex < 10)
                        {
                            message = message.Substring(endIndex + 1).Trim();
                        }
                    }
                    
                    AddLog(message);
                }
            }
            
            // Обновляем отображение активной вкладки
            RefreshActiveTab();
        }
        
        /// <summary>
        /// Обновляет отображение логов для активной вкладки
        /// </summary>
        private void RefreshActiveTab()
        {
            if (LogTabControl == null) return;
            
            var selectedTab = LogTabControl.SelectedItem as TabItem;
            if (selectedTab == null) return;
            
            TextBox? textBox = null;
            List<string>? logList = null;
            
            string? header = selectedTab.Header?.ToString();
            
            if (header == "All Logs")
            {
                textBox = AllLogsTextBox;
                logList = _allLogs;
            }
            else if (header == "WebRTC Log")
            {
                textBox = WebRtcLogTextBox;
                logList = _webRtcLogs;
            }
            else if (header == "Amo Log")
            {
                textBox = AmoLogTextBox;
                logList = _amoLogs;
            }
            else if (header == "SIP Log")
            {
                textBox = SipLogTextBox;
                logList = _sipLogs;
            }
            else if (header == "Call Log")
            {
                textBox = CallLogTextBox;
                logList = _callLogs;
            }
            
            if (textBox != null && logList != null)
            {
                textBox.Text = string.Join("\n", logList);
                textBox.ScrollToEnd();
            }
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

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            string? textToCopy = GetActiveTabText();
            if (!string.IsNullOrEmpty(textToCopy))
            {
                Clipboard.SetText(textToCopy);
                CustomMessageBox.Show("Logs copied to clipboard!", "Success", MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    FileName = $"SoftphoneLogs_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DefaultExt = "txt"
                };

                if (saveDialog.ShowDialog() != true)
                    return;

                // Экспортируем из файла лога — там полный лог за день без ограничений.
                // Если файл лога доступен, копируем его целиком. Иначе fallback на буфер UI.
                bool exportedFromFile = false;
                if (!string.IsNullOrEmpty(_logFilePath) && File.Exists(_logFilePath))
                {
                    try
                    {
                        using var src = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var dst = new FileStream(saveDialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
                        src.CopyTo(dst);
                        exportedFromFile = true;
                    }
                    catch
                    {
                        // File locked or error — fall through to UI buffer
                    }
                }

                if (!exportedFromFile)
                {
                    // Fallback: экспортируем видимый буфер (может быть неполным)
                    string? textToExport = GetActiveTabText();
                    if (string.IsNullOrEmpty(textToExport))
                    {
                        CustomMessageBox.Show("No logs to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information, this);
                        return;
                    }
                    File.WriteAllText(saveDialog.FileName, textToExport);
                }

                CustomMessageBox.Show(
                    $"Logs exported successfully to:\n{saveDialog.FileName}" +
                    (exportedFromFile ? "\n\n(Full log file — complete history)" : "\n\n(UI buffer only — may be incomplete)"),
                    "Export Success",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information,
                    this);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error exporting logs:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        /// <summary>
        /// Получает текст из активной вкладки
        /// </summary>
        private string? GetActiveTabText()
        {
            if (LogTabControl == null || LogTabControl.SelectedItem == null)
                return AllLogsTextBox?.Text;
            
            var selectedTab = LogTabControl.SelectedItem as TabItem;
            if (selectedTab == null)
                return AllLogsTextBox?.Text;
            
            string? header = selectedTab.Header?.ToString();
            
            if (header == "All Logs")
                return AllLogsTextBox?.Text;
            else if (header == "WebRTC Log")
                return WebRtcLogTextBox?.Text;
            else if (header == "Amo Log")
                return AmoLogTextBox?.Text;
            else if (header == "SIP Log")
                return SipLogTextBox?.Text;
            else if (header == "Call Log")
                return CallLogTextBox?.Text;
            
            return AllLogsTextBox?.Text;
        }
    }
}

