#if WINDOWS
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
        private UpdateInfo _updateInfo;
        private string _currentVersion;
        private readonly bool _isMandatory;

        private bool _isDownloading;
        private bool _readyToInstall;
        private string? _downloadedFilePath;
        private CancellationTokenSource? _downloadCts;
        
        // Конструктор для собственного сервера обновлений
        public UpdateAvailableWindow(
            UpdateInfo updateInfo,
            string currentVersion)
        {
            // Убеждаемся, что resources темы загружены ДО InitializeComponent()
            // (DynamicResource/Resource lookup может произойти во время загрузки XAML).
            EnsureResourcesLoaded();
            InitializeComponent();
            
            NativeWindowAppearanceManager.Attach(this);
            _updateInfo = updateInfo;
            _currentVersion = currentVersion;
            _isMandatory = updateInfo.Mandatory;
            
            LoadUpdateInfo();

            Closing += UpdateAvailableWindow_Closing;
            
            // Если обновление обязательное, скрываем кнопку "Update Later"
            if (_isMandatory)
            {
                LaterButton.Visibility = Visibility.Collapsed;
            }
        }
        
        /// <summary>
        /// Убеждается, что ресурсы темы загружены в окне
        /// </summary>
        private void EnsureResourcesLoaded()
        {
            try
            {
                // Сначала пытаемся найти ключи через иерархию ресурсов (window -> application -> merged dictionaries).
                if (this.TryFindResource("TextSecondaryBrush") is not null)
                {
                    return;
                }
                if (Application.Current?.TryFindResource("TextSecondaryBrush") is not null)
                {
                    return;
                }
                
                // Если ресурсы не найдены, загружаем их локально в окно.
                var resources = this.Resources ?? new ResourceDictionary();
                this.Resources = resources;
                
                // Проверяем, есть ли уже MergedDictionaries и загруженные темы
                // MergedDictionaries всегда инициализируется автоматически в ResourceDictionary
                var mergedDictionaries = resources.MergedDictionaries;

                // На практике Count может быть > 0, но при этом нужного ключа может не быть
                // (например, только win11-override словарь без базовых brushes).
                if (mergedDictionaries == null)
                {
                    // Best-effort: не будет работать только если ResourceDictionary поврежден/не доступен.
                    return;
                }

                // Загружаем минимум базовые ресурсы, чтобы исключение "Resource ... not found" не появлялось.
                EnsureMergedDictionary(mergedDictionaries, "Themes/FallbackTheme.xaml", logOnFail: true);
                EnsureMergedDictionary(mergedDictionaries, "Themes/DarkTheme.xaml", logOnFail: false);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Error ensuring resources loaded: {ex.Message}");
            }
        }

        private void EnsureMergedDictionary(
            System.Collections.Generic.IList<ResourceDictionary> merged,
            string xamlRelativeSource,
            bool logOnFail)
        {
            try
            {
                bool alreadyLoaded = merged.Any(d =>
                    (d.Source?.ToString() ?? "").EndsWith(xamlRelativeSource, StringComparison.OrdinalIgnoreCase));
                if (alreadyLoaded) return;

                merged.Add(new ResourceDictionary { Source = new Uri(xamlRelativeSource, UriKind.Relative) });
            }
            catch (Exception ex)
            {
                if (logOnFail)
                    MainWindow.Log($"[UpdateAvailableWindow] Failed to load {xamlRelativeSource}: {ex.Message}");
            }
        }
        
        private void LoadUpdateInfo()
        {
            try
            {
                if (_updateInfo == null) return;
                
                CurrentVersionTextBlock.Text = $"Current version: {_currentVersion}";
                
                string latestVersion = _updateInfo.Version.TrimStart('v', 'V');
                LatestVersionTextBlock.Text = $"Latest version: {latestVersion}";
                
                if (!string.IsNullOrEmpty(_updateInfo.Notes))
                {
                    ReleaseNotesContentTextBlock.Text = _updateInfo.Notes;
                }
                else
                {
                    ReleaseNotesContentTextBlock.Text = "No release notes available.";
                }
                
                if (_isMandatory)
                {
                    UpdateMessageTextBlock.Text = "A mandatory update is available!";
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Error loading update info: {ex.Message}");
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
            // Если обновление обязательное, не позволяем закрыть окно
            if (_isMandatory && !_readyToInstall)
            {
                e.Cancel = true;
                return;
            }
            
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
                // Проверяем наличие URL для скачивания
                if (string.IsNullOrWhiteSpace(_updateInfo.Url))
                {
                    CustomMessageBox.Show(
                        "Download URL is missing in update information.",
                        "Invalid Update Info",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning,
                        this);
                    return;
                }
                
                string downloadUrl = _updateInfo.Url;
                string? expectedSha256 = _updateInfo.Sha256;
                string fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = $"Callspire_{_updateInfo.Version}.exe";
                }

                string targetDir = AppDataHelper.GetUpdatesDirectory();
                string targetPath = Path.Combine(targetDir, fileName);

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

                // Загружаем файл
                await DownloadFileAsync(downloadUrl, targetPath, progress, _downloadCts.Token);

                // Проверяем SHA256, если указан
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    DownloadStatusTextBlock.Text = "Verifying file integrity...";
                    bool isValid = UpdateService.VerifyFileHash(targetPath, expectedSha256);
                    
                    if (!isValid)
                    {
                        File.Delete(targetPath);
                        throw new Exception("File integrity check failed. The downloaded file may be corrupted or tampered with.");
                    }
                }

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
        
        private async Task DownloadFileAsync(
            string downloadUrl,
            string destinationPath,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                throw new InvalidOperationException("Download URL is missing.");
            }

            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Callspire-Softphone", "1.0"));
            http.Timeout = TimeSpan.FromMinutes(10); // Таймаут для больших файлов

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
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{_downloadedFilePath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[UpdateAvailableWindow] Failed to open explorer: {ex.Message}");
                    }
                    
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

                Process? process = null;
                try
                {
                    process = Process.Start(psi);
                    if (process == null)
                    {
                        throw new InvalidOperationException("Failed to start the installer process.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (System.ComponentModel.Win32Exception winEx) when (winEx.NativeErrorCode == 1223)
                {
                    // User cancelled UAC - это нормально, не показываем ошибку
                    MainWindow.Log("[UpdateAvailableWindow] Installation was cancelled by the user (UAC prompt).");
                    return;
                }

                // Close the app so installer can replace files.
                Application.Current.Shutdown();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // User cancelled UAC
                MainWindow.Log("[UpdateAvailableWindow] Installation was cancelled by the user (UAC prompt).");
            }
            catch (InvalidCastException castEx)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Invalid cast error: {castEx.Message}");
                MainWindow.Log($"[UpdateAvailableWindow] Stack trace: {castEx.StackTrace}");
                CustomMessageBox.Show(
                    $"Failed to start installer due to a type conversion error.\n\nPlease try running the installer manually:\n{_downloadedFilePath}",
                    "Install Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error,
                    this);
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[UpdateAvailableWindow] Failed to start installer: {ex.Message}");
                MainWindow.Log($"[UpdateAvailableWindow] Exception type: {ex.GetType().Name}");
                MainWindow.Log($"[UpdateAvailableWindow] Stack trace: {ex.StackTrace}");
                CustomMessageBox.Show(
                    $"Failed to start installer:\n\n{ex.Message}\n\nPlease try running the installer manually:\n{_downloadedFilePath}",
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


#endif // WINDOWS
