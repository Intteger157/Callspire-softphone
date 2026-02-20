using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Diagnostics;

namespace Softphone
{
    /// <summary>
    /// Управляет single-instance поведением приложения
    /// Если приложение уже запущено, передает параметры в существующий экземпляр
    /// </summary>
    public static class SingleInstanceManager
    {
        private const string MutexName = "CallspireSoftphone_SingleInstance_Mutex";
        private const string PipeName = "CallspireSoftphone_SingleInstance_Pipe";
        private static Mutex? _mutex;
        private static bool _isFirstInstance = false;

        /// <summary>
        /// Проверяет, является ли этот экземпляр первым (единственным)
        /// </summary>
        public static bool IsFirstInstance()
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out _isFirstInstance);
                return _isFirstInstance;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Запускает сервер для приема сообщений от других экземпляров
        /// </summary>
        public static void StartServer()
        {
            if (!_isFirstInstance)
                return;

            Task.Run(() =>
            {
                try
                {
                    while (_isFirstInstance)
                    {
                        try
                        {
                            using (var pipeServer = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                            {
                                pipeServer.WaitForConnection();

                                using (var reader = new StreamReader(pipeServer))
                                {
                                    string message = reader.ReadToEnd();
                                    if (!string.IsNullOrEmpty(message))
                                    {
                                        // Передаем сообщение в UI поток
                                        Application.Current?.Dispatcher.InvokeAsync(() =>
                                        {
                                            HandleIncomingMessage(message);
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // Игнорируем ошибки при закрытии сервера
                            if (_isFirstInstance)
                            {
                                Debug.WriteLine($"[SingleInstanceManager] Error in pipe server: {ex.Message}");
                            }
                        }
                    }
                }
                catch
                {
                    // Игнорируем ошибки
                }
            });
        }

        /// <summary>
        /// Отправляет сообщение в существующий экземпляр приложения
        /// </summary>
        public static bool SendMessageToExistingInstance(string message)
        {
            try
            {
                using (var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    pipeClient.Connect(1000); // Таймаут 1 секунда
                    
                    using (var writer = new StreamWriter(pipeClient))
                    {
                        writer.Write(message);
                        writer.Flush();
                    }
                    
                    return true;
                }
            }
            catch (TimeoutException)
            {
                // Сервер не отвечает - возможно, приложение закрылось
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstanceManager] Error sending message: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Обрабатывает входящее сообщение от другого экземпляра
        /// </summary>
        private static void HandleIncomingMessage(string message)
        {
            try
            {
                Debug.WriteLine($"[SingleInstanceManager] Received message: {message}");

                // Активируем главное окно
                var mainWindow = Application.Current?.MainWindow as MainWindow;
                if (mainWindow != null)
                {
                    // Восстанавливаем окно если оно свернуто
                    if (mainWindow.WindowState == WindowState.Minimized)
                    {
                        mainWindow.WindowState = WindowState.Normal;
                    }
                    
                    // Активируем окно
                    mainWindow.Activate();
                    mainWindow.BringIntoView();
                    mainWindow.Focus();

                    // Обрабатываем протокол callspire://
                    if (message.StartsWith("callspire://", StringComparison.OrdinalIgnoreCase))
                    {
                        mainWindow.HandleProtocolMessage(message);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstanceManager] Error handling message: {ex.Message}");
            }
        }

        /// <summary>
        /// Освобождает ресурсы при закрытии приложения
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                _isFirstInstance = false;
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch
            {
                // Игнорируем ошибки при очистке
            }
        }
    }
}
