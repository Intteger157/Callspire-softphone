using System;
using System.IO;
using Microsoft.Win32;

namespace Softphone
{
    /// <summary>
    /// Регистрирует протокол callspire:// в Windows при первом запуске приложения
    /// </summary>
    public static class ProtocolRegistrar
    {
        private const string ProtocolName = "callspire";
        private const string ProtocolDescription = "URL:Callspire Protocol";

        /// <summary>
        /// Регистрирует протокол callspire:// в реестре Windows (HKEY_CURRENT_USER)
        /// Не требует прав администратора
        /// </summary>
        public static bool RegisterProtocol()
        {
            try
            {
                // Получаем путь к исполняемому файлу
                string exePath = GetExecutablePath();
                if (string.IsNullOrEmpty(exePath))
                {
                    MainWindow.Log("[ProtocolRegistrar] Cannot determine executable path");
                    return false;
                }

                // Проверяем, не зарегистрирован ли уже протокол полностью
                if (IsProtocolRegistered())
                {
                    MainWindow.Log("[ProtocolRegistrar] Protocol already fully registered");
                    return true;
                }

                MainWindow.Log($"[ProtocolRegistrar] Registering/updating protocol {ProtocolName}...");
                MainWindow.Log($"[ProtocolRegistrar] Executable path: {exePath}");

                // Регистрируем или обновляем протокол в HKEY_CURRENT_USER (не требует прав администратора)
                using (var key = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{ProtocolName}", true))
                {
                    if (key == null)
                    {
                        MainWindow.Log("[ProtocolRegistrar] Failed to create registry key");
                        return false;
                    }

                    // Устанавливаем основные значения (обновляем, если они уже есть)
                    key.SetValue("", ProtocolDescription, RegistryValueKind.String);
                    key.SetValue("URL Protocol", "", RegistryValueKind.String);
                    MainWindow.Log("[ProtocolRegistrar] Main protocol values set");
                }

                // Создаем структуру shell\open если её нет
                using (var shellKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{ProtocolName}\\shell", true))
                {
                    if (shellKey == null)
                    {
                        MainWindow.Log("[ProtocolRegistrar] Failed to create shell registry key");
                        return false;
                    }
                }

                using (var openKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{ProtocolName}\\shell\\open", true))
                {
                    if (openKey == null)
                    {
                        MainWindow.Log("[ProtocolRegistrar] Failed to create open registry key");
                        return false;
                    }
                }

                // Регистрируем или обновляем команду открытия
                using (var commandKey = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{ProtocolName}\\shell\\open\\command", true))
                {
                    if (commandKey == null)
                    {
                        MainWindow.Log("[ProtocolRegistrar] Failed to create command registry key");
                        return false;
                    }

                    // Формат: "путь_к_exe" "%1"
                    string command = $"\"{exePath}\" \"%1\"";
                    commandKey.SetValue("", command, RegistryValueKind.String);
                    MainWindow.Log($"[ProtocolRegistrar] Command set: {command}");
                }

                MainWindow.Log($"[ProtocolRegistrar] Protocol {ProtocolName} registered successfully");
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                MainWindow.Log($"[ProtocolRegistrar] Access denied: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[ProtocolRegistrar] Error registering protocol: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Проверяет, зарегистрирован ли протокол полностью (все необходимые ключи и значения)
        /// </summary>
        public static bool IsProtocolRegistered()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey($"Software\\Classes\\{ProtocolName}"))
                {
                    if (key == null)
                        return false;

                    // Проверяем наличие основных значений
                    string? defaultValue = key.GetValue("")?.ToString();
                    string? urlProtocol = key.GetValue("URL Protocol")?.ToString();
                    
                    if (string.IsNullOrEmpty(defaultValue) || string.IsNullOrEmpty(urlProtocol))
                    {
                        MainWindow.Log("[ProtocolRegistrar] Protocol partially registered (missing main values)");
                        return false;
                    }

                    // Проверяем наличие команды
                    using (var commandKey = Registry.CurrentUser.OpenSubKey($"Software\\Classes\\{ProtocolName}\\shell\\open\\command"))
                    {
                        if (commandKey == null)
                        {
                            MainWindow.Log("[ProtocolRegistrar] Protocol partially registered (missing command)");
                            return false;
                        }
                        
                        string? command = commandKey.GetValue("")?.ToString();
                        if (string.IsNullOrEmpty(command))
                        {
                            MainWindow.Log("[ProtocolRegistrar] Protocol partially registered (command is empty)");
                            return false;
                        }
                    }
                    
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Получает путь к исполняемому файлу приложения
        /// </summary>
        private static string GetExecutablePath()
        {
            try
            {
                // В single-file режиме Assembly.Location может быть пустым
                // Используем AppContext.BaseDirectory и ищем exe файл
                string baseDirectory = AppContext.BaseDirectory;
                
                // Ищем exe файл в базовой директории
                string[] exeFiles = Directory.GetFiles(baseDirectory, "*.exe");
                if (exeFiles.Length > 0)
                {
                    // Предпочитаем файл с именем Callspire или похожим
                    foreach (string exeFile in exeFiles)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(exeFile);
                        if (fileName.Contains("Callspire", StringComparison.OrdinalIgnoreCase) ||
                            fileName.Contains("Softphone", StringComparison.OrdinalIgnoreCase))
                        {
                            return Path.GetFullPath(exeFile);
                        }
                    }
                    // Если не нашли по имени, берем первый exe файл
                    return Path.GetFullPath(exeFiles[0]);
                }

                // Если не нашли в базовой директории, пробуем Assembly.Location
                string? assemblyLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(assemblyLocation) && File.Exists(assemblyLocation))
                {
                    return Path.GetFullPath(assemblyLocation);
                }

                // Последняя попытка: используем Process.GetCurrentProcess().MainModule
                var process = System.Diagnostics.Process.GetCurrentProcess();
                if (process.MainModule != null && !string.IsNullOrEmpty(process.MainModule.FileName))
                {
                    return process.MainModule.FileName;
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[ProtocolRegistrar] Error getting executable path: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Удаляет регистрацию протокола (для тестирования или деинсталляции)
        /// </summary>
        public static bool UnregisterProtocol()
        {
            try
            {
                MainWindow.Log($"[ProtocolRegistrar] Unregistering protocol {ProtocolName}...");

                // Удаляем ключ протокола
                string keyPath = $"Software\\Classes\\{ProtocolName}";
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);

                MainWindow.Log($"[ProtocolRegistrar] Protocol {ProtocolName} unregistered successfully");
                return true;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[ProtocolRegistrar] Error unregistering protocol: {ex.Message}");
                return false;
            }
        }
    }
}
