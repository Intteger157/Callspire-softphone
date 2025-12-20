using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Утилита для работы с ffmpeg - поиск и выполнение конвертации
    /// </summary>
    public static class FfmpegHelper
    {
        /// <summary>
        /// Ищет путь к ffmpeg в стандартных местах:
        /// 1. В подпапке tools рядом с исполняемым файлом
        /// 2. В системном PATH
        /// </summary>
        public static string? FindFfmpegPath()
        {
            // 1. Ищем в подпапке tools рядом с .exe
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegPath = Path.Combine(exeDirectory, "tools", "ffmpeg.exe");

            if (File.Exists(ffmpegPath))
            {
                MainWindow.Log($"[FfmpegHelper] ffmpeg found at: {ffmpegPath}");
                return ffmpegPath;
            }

            // 2. Ищем в системном PATH
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }))
                {
                    if (process != null)
                    {
                        process.WaitForExit(1000); // Ждем максимум 1 секунду
                        if (process.ExitCode == 0 || process.HasExited)
                        {
                            MainWindow.Log($"[FfmpegHelper] ffmpeg found in system PATH");
                            return "ffmpeg"; // Возвращаем имя без расширения для использования через PATH
                        }
                    }
                }
            }
            catch
            {
                // ffmpeg не найден в PATH - это нормально
            }

            MainWindow.Log($"[FfmpegHelper] ffmpeg not found at: {ffmpegPath} and not in system PATH");
            return null;
        }

        /// <summary>
        /// Выполняет конвертацию через ffmpeg асинхронно
        /// </summary>
        /// <param name="ffmpegPath">Путь к ffmpeg</param>
        /// <param name="arguments">Аргументы командной строки</param>
        /// <param name="outputFilePath">Путь к выходному файлу (для проверки успешности)</param>
        /// <param name="logPrefix">Префикс для логов (например, "[RtpCallRecorder]" или "[WebRtcCallRecorder]")</param>
        /// <returns>true если конвертация успешна, false в противном случае</returns>
        public static async Task<bool> ConvertAsync(string ffmpegPath, string arguments, string outputFilePath, string logPrefix)
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(processStartInfo))
                {
                    if (process == null)
                    {
                        MainWindow.Log($"{logPrefix} Failed to start ffmpeg process");
                        return false;
                    }

                    // Читаем вывод асинхронно
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    
                    await process.WaitForExitAsync();
                    
                    string output = await outputTask;
                    string error = await errorTask;

                    if (process.ExitCode == 0 && File.Exists(outputFilePath))
                    {
                        var fileInfo = new FileInfo(outputFilePath);
                        MainWindow.Log($"{logPrefix} Conversion successful: {fileInfo.Length / 1024} KB ({outputFilePath})");
                        
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            MainWindow.Log($"{logPrefix} ffmpeg stderr: {error.Trim()}");
                        }
                        
                        return true;
                    }
                    else
                    {
                        MainWindow.Log($"{logPrefix} Conversion failed (exit code: {process.ExitCode})");
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            MainWindow.Log($"{logPrefix} ffmpeg stderr: {error.Trim()}");
                        }
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            MainWindow.Log($"{logPrefix} ffmpeg stdout: {output.Trim()}");
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"{logPrefix} Error during ffmpeg conversion: {ex.Message}");
                return false;
            }
        }
    }
}
