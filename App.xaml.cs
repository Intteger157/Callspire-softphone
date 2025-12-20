using System;
using System.Windows;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Softphone
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            
            // Обработка необработанных исключений в UI потоке
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            
            // Обработка необработанных исключений в других потоках
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            
            // Обработка необработанных исключений в Task
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
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

