using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Softphone
{
    public partial class UpdateAvailableWindow : Window
    {
        private GitHubReleaseInfo _releaseInfo;
        private string _currentVersion;
        private string _repositoryUrl;
        private readonly string _repositoryOwner;
        private readonly string _repositoryName;
        private readonly string? _githubToken;

        private bool _isDownloading;
        private bool _readyToInstall;
        private string? _downloadedFilePath;
        private CancellationTokenSource? _downloadCts;
        
        public UpdateAvailableWindow(
            GitHubReleaseInfo releaseInfo,
            string currentVersion,
            string repositoryUrl,
            string repositoryOwner,
            string repositoryName,
            string? githubToken)
        {
            InitializeComponent();
            _releaseInfo = releaseInfo;
            _currentVersion = currentVersion;
            _repositoryUrl = repositoryUrl;
            _repositoryOwner = repositoryOwner;
            _repositoryName = repositoryName;
            _githubToken = githubToken;
            
            LoadReleaseInfo();

            Closing += UpdateAvailableWindow_Closing;
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
            if (_readyToInstall)
            {
                StartInstaller();
                return;
            }

            if (_isDownloading)
            {
                _downloadCts?.Cancel();
                return;
            }

            _ = DownloadAndPrepareUpdateAsync();
        }

        private void UpdateAvailableWindow_Closing(object? sender, CancelEventArgs e)
        {
            // If user closes the dialog mid-download, cancel the download gracefully.
            if (_isDownloading)
            {
                _downloadCts?.Cancel();
            }
        }

        private async Task DownloadAndPrepareUpdateAsync()
        {
            try
            {
                var asset = SelectBestAsset(_releaseInfo.Assets);
                if (asset == null)
                {
                    CustomMessageBox.Show(
                        "No downloadable installer was found in the latest release.\n\n" +
                        "Please attach a .exe or .msi file to the GitHub release assets.",
                        "No Installer Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }

                string targetDir = AppDataHelper.GetUpdatesDirectory();
                string safeName = string.IsNullOrWhiteSpace(asset.Name) ? "update.bin" : asset.Name;
                string targetPath = Path.Combine(targetDir, safeName);

                // If file exists, overwrite (keeps UX simple)
                if (File.Exists(targetPath))
                {
                    try { File.Delete(targetPath); } catch { /* ignore */ }
                }

                _isDownloading = true;
                _downloadCts?.Cancel();
                _downloadCts = new CancellationTokenSource();

                LaterButton.IsEnabled = false;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "Stop downloading";
                DownloadProgressPanel.Visibility = Visibility.Visible;
                DownloadProgressBar.Value = 0;
                DownloadStatusTextBlock.Text = "Downloading update...";

                var progress = new Progress<double>(p =>
                {
                    DownloadProgressBar.Value = Math.Max(0, Math.Min(100, p));
                    DownloadStatusTextBlock.Text = $"Downloading update... {DownloadProgressBar.Value:0}%";
                });

                await DownloadReleaseAssetAsync(asset, targetPath, progress, _downloadCts.Token);

                _downloadedFilePath = targetPath;
                _readyToInstall = true;

                UpdateMessageTextBlock.Text = "Update downloaded. Ready to install.";
                DownloadStatusTextBlock.Text = "Download complete.";
                DownloadProgressBar.Value = 100;

                LaterButton.IsEnabled = true;
                LaterButton.Content = "Close";
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = "Start";
            }
            catch (OperationCanceledException)
            {
                // No UI noise for cancellation; just reset state.
                ResetDownloadUi("Download canceled.");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Download failed: {ex.Message}");
                ResetDownloadUi("Download failed.");
                CustomMessageBox.Show(
                    $"Failed to download the update:\n\n{ex.Message}",
                    "Download Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
            finally
            {
                _isDownloading = false;
            }
        }

        private void ResetDownloadUi(string status)
        {
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            DownloadProgressBar.Value = 0;
            DownloadStatusTextBlock.Text = status;

            LaterButton.IsEnabled = true;
            LaterButton.Content = "Update Later";
            UpdateButton.IsEnabled = true;
            UpdateButton.Content = "Update Now";
        }

        private GitHubAsset? SelectBestAsset(GitHubAsset[] assets)
        {
            if (assets == null || assets.Length == 0) return null;

            // Prefer Windows installers (.exe / .msi). Try to bias toward x64/windows naming.
            int Score(GitHubAsset a)
            {
                string name = (a.Name ?? "").ToLowerInvariant();
                int score = 0;
                if (name.EndsWith(".exe")) score += 100;
                if (name.EndsWith(".msi")) score += 95;
                if (name.EndsWith(".zip")) score += 10; // fallback only
                if (name.Contains("win")) score += 15;
                if (name.Contains("windows")) score += 15;
                if (name.Contains("x64") || name.Contains("amd64")) score += 10;
                if (a.Size > 0) score += 1;
                return score;
            }

            return assets
                .Where(a => a is not null)
                .Select(a => a!)
                .Where(a =>
                {
                    string n = (a.Name ?? "").ToLowerInvariant();
                    return n.EndsWith(".exe") || n.EndsWith(".msi") || n.EndsWith(".zip");
                })
                .OrderByDescending(Score)
                .FirstOrDefault();
        }

        private async Task DownloadReleaseAssetAsync(
            GitHubAsset asset,
            string destinationPath,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            // Prefer authenticated API download if we have an asset id (works for private repos too).
            string apiUrl = !string.IsNullOrWhiteSpace(asset.ApiUrl)
                ? asset.ApiUrl
                : $"https://api.github.com/repos/{_repositoryOwner}/{_repositoryName}/releases/assets/{asset.Id}";

            // Fallback to browser download URL if API url is missing.
            bool hasApi = asset.Id > 0 && !string.IsNullOrWhiteSpace(apiUrl);
            string downloadUrl = hasApi ? apiUrl : asset.BrowserDownloadUrl;

            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("Missing download URL for the selected release asset.");
            }

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Callspire-Softphone", "1.0"));

            if (hasApi)
            {
                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            }
            else
            {
                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            }

            if (!string.IsNullOrWhiteSpace(_githubToken))
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _githubToken);
            }

            using var response = await http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    double pct = (double)totalRead / totalBytes.Value * 100.0;
                    progress.Report(pct);
                }
            }

            // If server didn't send length, just mark as complete.
            if (!totalBytes.HasValue)
            {
                progress.Report(100.0);
            }
        }

        private void StartInstaller()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_downloadedFilePath) || !File.Exists(_downloadedFilePath))
                {
                    CustomMessageBox.Show(
                        "The downloaded update file is missing. Please download again.",
                        "Update File Missing",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    _readyToInstall = false;
                    UpdateButton.Content = "Update Now";
                    return;
                }

                string ext = Path.GetExtension(_downloadedFilePath).ToLowerInvariant();
                if (ext != ".exe" && ext != ".msi")
                {
                    // For now, we only auto-run installers. Zip can be handled later if needed.
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{_downloadedFilePath}\"",
                        UseShellExecute = true
                    });
                    CustomMessageBox.Show(
                        "The update was downloaded. Please install it manually from the opened folder.",
                        "Ready to Install",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information,
                        this);
                    return;
                }

                var psi = new ProcessStartInfo
                {
                    FileName = _downloadedFilePath,
                    UseShellExecute = true,
                    Verb = "runas" // prompt for admin if needed
                };

                Process.Start(psi);

                // Close the app so installer can replace files.
                Application.Current.Shutdown();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User cancelled UAC
                MainWindow.Log("[UpdateAvailableWindow] Installation was cancelled by the user (UAC prompt).");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Failed to start installer: {ex.Message}");
                CustomMessageBox.Show(
                    $"Failed to start installer:\n\n{ex.Message}",
                    "Install Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
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

