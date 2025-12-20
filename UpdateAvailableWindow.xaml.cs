using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace Softphone
{
    public partial class UpdateAvailableWindow : Window
    {
        private GitHubReleaseInfo _releaseInfo;
        private string _currentVersion;
        private string _repositoryUrl;
        
        public UpdateAvailableWindow(GitHubReleaseInfo releaseInfo, string currentVersion, string repositoryUrl)
        {
            InitializeComponent();
            _releaseInfo = releaseInfo;
            _currentVersion = currentVersion;
            _repositoryUrl = repositoryUrl;
            
            LoadReleaseInfo();
        }
        
        private void LoadReleaseInfo()
        {
            try
            {
                CurrentVersionTextBlock.Text = $"Current version: {_currentVersion}";
                
                string latestVersion = _releaseInfo.TagName.TrimStart('v', 'V');
                LatestVersionTextBlock.Text = $"Latest version: {latestVersion}";
                
                if (!string.IsNullOrEmpty(_releaseInfo.Body))
                {
                    ReleaseNotesContentTextBlock.Text = _releaseInfo.Body;
                }
                else
                {
                    ReleaseNotesContentTextBlock.Text = "No release notes available.";
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Error loading release info: {ex.Message}");
            }
        }
        
        private void UpdateButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Открываем страницу релиза в браузере
                string url = !string.IsNullOrEmpty(_releaseInfo.HtmlUrl) 
                    ? _releaseInfo.HtmlUrl 
                    : _repositoryUrl + "/releases/latest";
                
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                
                MainWindow.Log($"[UpdateAvailableWindow] Opened release page: {url}");
                
                Close();
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Error opening release page: {ex.Message}");
                CustomMessageBox.Show($"Error opening release page:\n\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
        
        private void LaterButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

