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

            // Theme (System/Dark/Light)
            try { ThemeService.Initialize(); } catch { }

            // Flush logs on exit (best effort).
            this.Exit += (_, __) =>
            {
                try { FileLogService.Instance.Shutdown(); } catch { }
                SingleInstanceManager.Cleanup();
            };
            
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
                    if (!isFirstInstance)
                    {
                        // Приложение уже запущено - отправляем сообщение в существующий экземпляр
                        Debug.WriteLine($"[App] Application already running, sending protocol message to existing instance");
                        bool sent = SingleInstanceManager.SendMessageToExistingInstance(protocolArg);
                        if (sent)
                        {
                            Debug.WriteLine("[App] Message sent successfully, shutting down this instance");
                            Shutdown();
                            return;
                        }
                        else
                        {
                            Debug.WriteLine("[App] Failed to send message, continuing with new instance");
                            // Если не удалось отправить сообщение, продолжаем как первый экземпляр
                            isFirstInstance = true;
                            _pendingProtocolCall = protocolArg;
                        }
                    }
                    else
                    {
                        // Это первый экземпляр - сохраняем для обработки после создания MainWindow
                        _pendingProtocolCall = protocolArg;
                    }
                }
            }
            
            // Если это первый экземпляр, запускаем сервер для приема сообщений
            if (isFirstInstance)
            {
                SingleInstanceManager.StartServer();
            }
            else if (protocolArg == null)
            {
                // Приложение уже запущено и нет протокола - закрываем этот экземпляр
                // (но только если это не первый запуск без аргументов)
                Debug.WriteLine("[App] Application already running without protocol args, shutting down this instance");
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
                Debug.WriteLine($"[App] Handling callspire protocol: {protocolUrl}");
                
                // Парсим URL
                if (!Uri.TryCreate(protocolUrl, UriKind.Absolute, out Uri? uri))
                {
                    Debug.WriteLine($"[App] Invalid protocol URL format: {protocolUrl}");
                    return;
                }
                
                // Проверяем схему и хост
                if (uri.Scheme != "callspire" || uri.Host != "call")
                {
                    Debug.WriteLine($"[App] Invalid protocol scheme or host: {uri.Scheme}://{uri.Host}");
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
                        Softphone.MainWindow.Log($"[App] Unhandled UI thread exception: {e.Exception.Message}");
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
                        var mainWin = MainWindow as Softphone.MainWindow;
                        if (mainWin != null)
                        {
                            Softphone.MainWindow.Log($"[App] Unhandled domain exception: {ex.Message}");
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
                    var mainWin = MainWindow as Softphone.MainWindow;
                    if (mainWin != null)
                    {
                        Softphone.MainWindow.Log($"[App] Unobserved task exception: {e.Exception.Message}");
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

