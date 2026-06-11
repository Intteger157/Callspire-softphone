#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Softphone
{
    public partial class LeadSelectionWindow : Window
    {
        public long? SelectedLeadId { get; private set; }
        public bool RecordingUploadedInDialog { get; private set; }
        private string? _amoCrmSubdomain;
        private bool _showFirstLeadOnly; // Режим показа только первого лида
        private string? _phoneNumber;
        private string? _audioFilePath;
        private bool _isIncoming;
        private int _durationSeconds;
        private bool _wasAnswered;
        private string? _callLog;
        private DateTime? _callTime;

        public class LeadInfo
        {
            public long Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            // ID ответственного пользователя в AmoCRM (responsible_user_id).
            // Может быть null, если мы его не запрашивали или не передавали.
            public long? ResponsibleUserId { get; set; }
        }

        public LeadSelectionWindow(List<LeadInfo> leads, string? amoCrmSubdomain = null, bool showFirstLeadOnly = false, 
            string? phoneNumber = null, string? audioFilePath = null, bool isIncoming = false, int durationSeconds = 0, 
            bool wasAnswered = false, string? callLog = null, DateTime? callTime = null)
        {
            InitializeComponent();
            NativeWindowAppearanceManager.Attach(this);
            _amoCrmSubdomain = amoCrmSubdomain;
            _showFirstLeadOnly = showFirstLeadOnly;
            _phoneNumber = phoneNumber;
            _audioFilePath = audioFilePath;
            _isIncoming = isIncoming;
            _durationSeconds = durationSeconds;
            _wasAnswered = wasAnswered;
            _callLog = callLog;
            _callTime = callTime;
            
            // Показываем кнопку "Upload record" только если есть информация о звонке
            if (!string.IsNullOrEmpty(_phoneNumber) && !string.IsNullOrEmpty(_audioFilePath))
            {
                UploadRecordButton.Visibility = Visibility.Visible;
            }
            else
            {
                UploadRecordButton.Visibility = Visibility.Collapsed;
            }
            
            if (leads == null || leads.Count == 0)
            {
                MainWindow.Log("[LeadSelectionWindow] No leads provided");
                Close();
                return;
            }

            // В режиме "show first lead only" показываем только первый лид
            if (_showFirstLeadOnly && leads.Count > 0)
            {
                var firstLead = new List<LeadInfo> { leads[0] };
                LeadsListBox.ItemsSource = firstLead;
                
                // Скрываем кнопку "Select" - она не нужна в этом режиме
                SelectButton.Visibility = Visibility.Collapsed;
                
                // Изменяем текст описания для режима показа первого лида
                DescriptionTextBlock.Text = $"A lead was found for this contact:";
            }
            else
            {
                // Обычный режим - показываем все лиды
                LeadsListBox.ItemsSource = leads;
            }
            
            LeadsListBox.SelectionChanged += LeadsListBox_SelectionChanged;
        }

        private void LeadsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            bool hasSelection = LeadsListBox.SelectedItem != null;
            SelectButton.IsEnabled = hasSelection;
            UploadRecordButton.IsEnabled = hasSelection && !string.IsNullOrEmpty(_phoneNumber) && !string.IsNullOrEmpty(_audioFilePath);
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if (LeadsListBox.SelectedItem is LeadInfo selectedLead)
            {
                SelectedLeadId = selectedLead.Id;
                MainWindow.Log($"[LeadSelectionWindow] User selected lead ID: {selectedLead.Id}");
                DialogResult = true;
                Close();
            }
        }

        private async void UploadRecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (LeadsListBox.SelectedItem is LeadInfo selectedLead)
            {
                if (string.IsNullOrEmpty(_phoneNumber) || string.IsNullOrEmpty(_audioFilePath))
                {
                    CustomMessageBox.Show(
                        "Call information is missing.\n\nCannot upload recording.",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                // Disable button during upload
                UploadRecordButton.IsEnabled = false;
                UploadRecordButton.Content = "Uploading...";

                try
                {
                    var mainWindow = Application.Current.MainWindow as MainWindow;
                    if (mainWindow == null)
                    {
                        CustomMessageBox.Show(
                            "Cannot access AmoCRM service.",
                            "Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error,
                            this);
                        UploadRecordButton.IsEnabled = true;
                        UploadRecordButton.Content = "Upload record";
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
                        UploadRecordButton.IsEnabled = true;
                        UploadRecordButton.Content = "Upload record";
                        return;
                    }

                    string? callFromLabel = (!_isIncoming && _callTime.HasValue && !string.IsNullOrEmpty(_phoneNumber))
                        ? AmoCallFromLabelResolver.ResolveFromHistory(_phoneNumber, _callTime.Value)
                        : null;

                    // Upload recording to selected lead
                    bool success = await amoCrmService.ManuallyUploadRecordingToLeadAsync(
                        selectedLead.Id,
                        _audioFilePath,
                        _phoneNumber,
                        _isIncoming,
                        _durationSeconds,
                        _wasAnswered,
                        callFromLabel);

                    if (success)
                    {
                        SelectedLeadId = selectedLead.Id;
                        RecordingUploadedInDialog = true;
                        CustomMessageBox.Show(
                            $"Recording file successfully uploaded to AmoCRM lead {selectedLead.Id}.",
                            "Upload Successful",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information,
                            this);
                        
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        CustomMessageBox.Show(
                            "Failed to upload recording file.\n\nPlease try again.",
                            "Upload Failed",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error,
                            this);
                        UploadRecordButton.IsEnabled = true;
                        UploadRecordButton.Content = "Upload record";
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[LeadSelectionWindow] Error uploading recording: {ex.Message}");
                    CustomMessageBox.Show(
                        $"Error uploading recording:\n{ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error,
                        this);
                    UploadRecordButton.IsEnabled = true;
                    UploadRecordButton.Content = "Upload record";
                }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedLeadId = null;
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void LinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.Tag is long leadId)
            {
                try
                {
                    string subdomain = _amoCrmSubdomain ?? "mdkb"; // Fallback to default
                    string? baseUrl = AmoCrmAccountUrl.TryBuildWebBaseUrl(subdomain);
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        var mainWindow = Application.Current.MainWindow as MainWindow;
                        baseUrl = mainWindow?.GetAmoCrmService()?.GetAccountWebBaseUrl();
                    }
                    if (string.IsNullOrEmpty(baseUrl))
                        return;
                    
                    string url = $"{baseUrl.TrimEnd('/')}/leads/detail/{leadId}";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                    MainWindow.Log($"[LeadSelectionWindow] Opened lead {leadId} in browser: {url}");
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[LeadSelectionWindow] Error opening lead link: {ex.Message}");
                }
            }
        }
    }
}

#endif // WINDOWS
