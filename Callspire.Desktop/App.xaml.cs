#if WINDOWS
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;

namespace Softphone
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Ensure file logging is initialized early (before MainWindow exists).
            // Do not log via MainWindow here to avoid startup ordering issues.
            _ = FileLogService.Instance;

            // Callspire.Core is platform-agnostic; provide the NAudio-based media duration reader.
            CallStatisticsService.RecordingDurationResolver = path =>
            {
                using var reader = new NAudio.Wave.AudioFileReader(path);
                return reader.TotalTime;
            };

            // UI-thread marshalling hooks for business services (replaces direct WPF Dispatcher usage).
            Softphone.UiThread.Post = a => Current?.Dispatcher.BeginInvoke(a);
            Softphone.UiThread.PostUrgent = a => Current?.Dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Send);

            // Platform audio backend: WASAPI/WinMM on Windows, PortAudio elsewhere.
            Softphone.Audio.AudioDeviceFactory.Default = new DesktopAudioDeviceFactory();

            // Desktop (NAudio-based) implementations of Core audio hooks.
            Softphone.Audio.TonePlayerFactory.Create = () => new ToneGenerator();
            Softphone.Audio.RtpCallRecorderFactory.Create = () => new RtpCallRecorder();
            Softphone.Audio.RtpCallRecorderFactory.TryRecoverWavFromOrphanPcm = path => RtpCallRecorder.TryRecoverWavFromOrphanPcm(path);
            Softphone.Audio.RingtoneControl.StopHook = () => RingtoneService.Instance.Stop();
            Softphone.Audio.RingbackToneControl.StopHook = () => RingbackToneService.Instance.Stop();
            Softphone.Audio.MicrophoneControl.OptimizeForVoIPHook = () => MicrophoneMuteHelper.OptimizeMicrophoneForVoIP();
            Softphone.Audio.MicrophoneControl.SetMuteHook = mute => MicrophoneMuteHelper.SetMicrophoneMute(mute);

            // Theme (System/Dark/Light)
            try { ThemeService.Initialize(); } catch { }

            // Flush logs on exit (best effort).
            this.Exit += (_, __) =>
            {
                // Best-effort: end any active calls on exit so they don't "continue" on PBX.
                try
                {
                    if (this.MainWindow is Softphone.MainWindow mw)
                    {
                        mw.ShutdownTelephonyBestEffort();
                    }
                }
                catch { }
                try { FileLogService.Instance.Shutdown(); } catch { }
                SingleInstanceManager.Cleanup();
            };

            // Also attempt cleanup on OS session end / process exit.
            try
            {
                AppDomain.CurrentDomain.ProcessExit += (_, __) =>
                {
                    try
                    {
                        if (this.MainWindow is Softphone.MainWindow mw)
                        {
                            mw.ShutdownTelephonyBestEffort();
                        }
                    }
                    catch { }
                };
            }
            catch { }

            try
            {
                this.SessionEnding += (_, __) =>
                {
                    try
                    {
                        if (this.MainWindow is Softphone.MainWindow mw)
                        {
                            mw.ShutdownTelephonyBestEffort();
                        }
                    }
                    catch { }
                };
            }
            catch { }
            
            // Обработка необработанных исключений в UI потоке
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            // Обработка необработанных исключений в других потоках
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            
            // Обработка необработанных исключений в Task
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            // Проверяем, является ли этот экземпляр первым
            bool isFirstInstance = SingleInstanceManager.IsFirstInstance();
            
            // Обработка протокола callspire:// для интеграции с браузером AmoCRM
            // Формат: callspire://call?phone=17027325111&leadId=12345
            string? protocolArg = null;
            if (e.Args != null && e.Args.Length > 0)
            {
                protocolArg = e.Args[0];
                if (protocolArg.StartsWith("callspire://", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isFirstInstance || SingleInstanceManager.AnotherInstanceIsRunning())
                    {
                        // Никогда не поднимаем второй UI для click-to-call — только передаём в primary.
                        SingleInstanceManager.WaitAndForwardToExistingInstance(protocolArg);
                        Shutdown();
                        return;
                    }

                    // Primary instance: обработаем после создания MainWindow
                    _pendingProtocolCall = protocolArg;
                }
            }
            
            // Pipe server стартует сразу, чтобы secondary успел подключиться во время cold start.
            if (isFirstInstance)
            {
                SingleInstanceManager.StartServer();
            }
            else if (protocolArg == null)
            {
                Debug.WriteLine("[App] Application already running without protocol args, shutting down this instance");
                Shutdown();
                return;
            }
            else
            {
                // callspire:// но мы не primary — уже обработано выше; на всякий случай выходим.
                Shutdown();
                return;
            }
        }
        
        /// <summary>
        /// Обрабатывает протокол callspire:// для инициации звонка из браузера AmoCRM
        /// Формат: callspire://call?phone=17027325111&leadId=12345
        /// </summary>
        private void HandleCallspireProtocol(string protocolUrl)
        {
            try
            {
                Debug.WriteLine($"[App] Handling callspire protocol: {LogSanitizer.RedactUrlWithToken(protocolUrl)}");
                
                // Парсим URL
                if (!Uri.TryCreate(protocolUrl, UriKind.Absolute, out Uri? uri))
                {
                    Debug.WriteLine($"[App] Invalid protocol URL format: {protocolUrl}");
                    return;
                }
                
                if (uri.Scheme != "callspire")
                {
                    Debug.WriteLine($"[App] Invalid protocol scheme: {uri.Scheme}");
                    return;
                }

                if (uri.Host == "cdr-auth")
                {
                    var cdrParams = ParseQueryString(uri.Query);
                    string? token = cdrParams.ContainsKey("token") ? cdrParams["token"] : null;
                    if (!string.IsNullOrEmpty(token))
                    {
                        Debug.WriteLine("[App] CDR auth token received via protocol");
                        (this.MainWindow as MainWindow)?.HandleCdrAuthToken(token);
                    }
                    return;
                }

                if (uri.Host == "provision")
                {
                    var provParams = ParseQueryString(uri.Query);
                    string? tokenId = provParams.ContainsKey("token") ? provParams["token"] : null;
                    string? proxy = provParams.ContainsKey("proxy") ? provParams["proxy"] : null;
                    if (string.IsNullOrEmpty(tokenId) || string.IsNullOrEmpty(proxy))
                    {
                        Debug.WriteLine("[App] provision URL missing token or proxy parameter");
                        return;
                    }
                    Debug.WriteLine($"[App] Provision token received (proxy={proxy})");
                    var mw = this.MainWindow as MainWindow;
                    if (mw != null)
                    {
                        _ = mw.HandleProvisionTokenAsync(tokenId!, proxy!);
                    }
                    else
                    {
                        _pendingProvision = (tokenId!, proxy!);
                    }
                    return;
                }

                if (uri.Host != "call")
                {
                    Debug.WriteLine($"[App] Unknown protocol host: {uri.Host}");
                    return;
                }
                
                // Парсим query параметры
                var queryParams = ParseQueryString(uri.Query);
                string? phoneNumber = queryParams.ContainsKey("phone") ? queryParams["phone"] : null;
                string? leadIdStr = queryParams.ContainsKey("leadId") ? queryParams["leadId"] : null;
                
                if (string.IsNullOrEmpty(phoneNumber))
                {
                    Debug.WriteLine("[App] Phone number is missing in protocol URL");
                    return;
                }
                
                long? leadId = null;
                if (!string.IsNullOrEmpty(leadIdStr) && long.TryParse(leadIdStr, out long parsedLeadId))
                {
                    leadId = parsedLeadId;
                }
                
                Debug.WriteLine($"[App] Parsed protocol: phone={phoneNumber}, leadId={leadId}");
                
                // Передаем в MainWindow для обработки
                var mainWindow = this.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.InitiateCallFromBrowser(phoneNumber, leadId);
                }
                else
                {
                    // Если MainWindow еще не создан, сохраняем параметры для обработки позже
                    _pendingBrowserCall = (phoneNumber, leadId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[App] Error handling callspire protocol: {ex.Message}");
            }
        }
        
        // Временное хранилище для звонка из браузера, если MainWindow еще не создан
        private (string phoneNumber, long? leadId)? _pendingBrowserCall = null;
        private string? _pendingProtocolCall = null;
        // Pending provisioning request that arrived via ``callspire://provision``
        // before MainWindow existed. Consumed by GetPendingProvision.
        private (string tokenId, string proxy)? _pendingProvision = null;

        /// <summary>
        /// Вызывается MainWindow после создания для обработки отложенного звонка из браузера
        /// </summary>
        public (string phoneNumber, long? leadId)? GetPendingBrowserCall()
        {
            // Обрабатываем протокол, если есть
            if (!string.IsNullOrEmpty(_pendingProtocolCall))
            {
                HandleCallspireProtocol(_pendingProtocolCall);
                _pendingProtocolCall = null;
            }
            
            var call = _pendingBrowserCall;
            _pendingBrowserCall = null; // Очищаем после получения
            return call;
        }

        /// <summary>
        /// Retrieves a pending provisioning request that arrived before MainWindow
        /// was created. MainWindow should call this on startup and run the
        /// returned (token, proxy) pair through <c>HandleProvisionTokenAsync</c>.
        /// </summary>
        public (string tokenId, string proxy)? GetPendingProvision()
        {
            var p = _pendingProvision;
            _pendingProvision = null;
            return p;
        }
        
        /// <summary>
        /// Парсит query string в словарь ключ-значение
        /// </summary>
        private Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;
            
            // Убираем ведущий '?' если есть
            if (query.StartsWith("?"))
                query = query.Substring(1);
            
            string[] pairs = query.Split('&');
            foreach (string pair in pairs)
            {
                if (string.IsNullOrEmpty(pair))
                    continue;
                
                int equalIndex = pair.IndexOf('=');
                if (equalIndex > 0)
                {
                    string key = Uri.UnescapeDataString(pair.Substring(0, equalIndex));
                    string value = Uri.UnescapeDataString(pair.Substring(equalIndex + 1));
                    result[key] = value;
                }
                else
                {
                    result[Uri.UnescapeDataString(pair)] = string.Empty;
                }
            }
            
            return result;
        }
        
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            try
            {
                // Логируем в Debug и пытаемся в MainWindow, если доступен
                Debug.WriteLine($"[App] Unhandled UI thread exception: {e.Exception.Message}");
                Debug.WriteLine($"[App] Exception type: {e.Exception.GetType().Name}");
                if (e.Exception.StackTrace != null)
                {
                    Debug.WriteLine($"[App] Stack trace: {e.Exception.StackTrace}");
                }
                
                // Пытаемся логировать в MainWindow, если он доступен
                try
                {
                    var mainWin = MainWindow as Softphone.MainWindow;
                    if (mainWin != null)
                    {
                        Softphone.MainWindow.Log($"[App] Unhandled UI thread exception: {e.Exception.GetType().Name}: {e.Exception.Message}");
                        if (!string.IsNullOrEmpty(e.Exception.StackTrace))
                            Softphone.MainWindow.Log($"[App] Stack trace: {e.Exception.StackTrace}");
                    }
                }
                catch
                {
                    // Игнорируем ошибки логирования в MainWindow
                }
                
                // Показываем сообщение пользователю
                MessageBox.Show(
                    $"Произошла ошибка в приложении:\n\n{e.Exception.Message}\n\nПриложение продолжит работу, но некоторые функции могут работать некорректно.",
                    "Ошибка приложения",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                
                // Помечаем исключение как обработанное, чтобы приложение не падало
                e.Handled = true;
            }
            catch
            {
                // Если даже логирование не работает, просто помечаем как обработанное
                e.Handled = true;
            }
        }
        
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Debug.WriteLine($"[App] Unhandled domain exception: {ex.Message}");
                    Debug.WriteLine($"[App] Exception type: {ex.GetType().Name}");
                    if (ex.StackTrace != null)
                    {
                        Debug.WriteLine($"[App] Stack trace: {ex.StackTrace}");
                    }
                    Debug.WriteLine($"[App] IsTerminating: {e.IsTerminating}");
                    
                    // Пытаемся логировать в MainWindow, если он доступен
                    try
                    {
                        // IMPORTANT: this can be called from a non-UI thread; marshal to Dispatcher to avoid WPF cross-thread exceptions.
                        var dispatcher = Application.Current?.Dispatcher;
                        if (dispatcher != null)
                        {
                            dispatcher.BeginInvoke(new Action(() =>
                            {
                                try { Softphone.MainWindow.Log($"[App] Unhandled domain exception: {ex.Message}"); } catch { }
                            }));
                        }
                    }
                    catch
                    {
                        // Игнорируем ошибки логирования в MainWindow
                    }
                }
                else
                {
                    Debug.WriteLine($"[App] Unhandled domain exception (non-Exception): {e.ExceptionObject}");
                }
            }
            catch
            {
                // Игнорируем ошибки логирования
            }
        }
        
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Debug.WriteLine($"[App] Unobserved task exception: {e.Exception.Message}");
                Debug.WriteLine($"[App] Exception type: {e.Exception.GetType().Name}");
                if (e.Exception.StackTrace != null)
                {
                    Debug.WriteLine($"[App] Stack trace: {e.Exception.StackTrace}");
                }
                
                // Пытаемся логировать в MainWindow, если он доступен
                try
                {
                    // IMPORTANT: UnobservedTaskException can be raised on the finalizer thread.
                    // Never touch WPF-bound logging directly here; marshal to Dispatcher.
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null)
                    {
                        var msg = e.Exception.Message;
                        dispatcher.BeginInvoke(new Action(() =>
                        {
                            try { Softphone.MainWindow.Log($"[App] Unobserved task exception: {msg}"); } catch { }
                        }));
                    }
                }
                catch
                {
                    // Игнорируем ошибки логирования в MainWindow
                }
                
                // Помечаем исключение как обработанное
                e.SetObserved();
            }
            catch
            {
                // Игнорируем ошибки логирования
                e.SetObserved();
            }
        }
    }
}


#endif // WINDOWS
