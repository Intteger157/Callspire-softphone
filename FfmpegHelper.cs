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
        // Таймаут зависит от размера файла: минимум 2 минуты, для больших файлов до 10 минут
        // Для звонка 10 минут (примерно 5-10 МБ WAV) конвертация может занять 1-3 минуты
        private static readonly TimeSpan DefaultFfmpegTimeout = TimeSpan.FromMinutes(10);
        /// <summary>
        /// Ищет путь к ffmpeg в стандартных местах:
        /// 1. В подпапке tools рядом с исполняемым файлом (tools\ffmpeg.exe)
        /// 2. В подпапках tools (например, tools\ffmpeg-*\ffmpeg.exe)
        /// 3. В системном PATH
        /// </summary>
        public static string? FindFfmpegPath()
        {
            // 1. Ищем в подпапке tools рядом с .exe (tools\ffmpeg.exe)
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string ffmpegPath = Path.Combine(exeDirectory, "tools", "ffmpeg.exe");

            if (File.Exists(ffmpegPath))
            {
                MainWindow.Log($"[FfmpegHelper] ffmpeg found at: {ffmpegPath}");
                return ffmpegPath;
            }

            // 2. Ищем в подпапках tools (например, tools\ffmpeg-*\ffmpeg.exe или tools\ffmpeg-*\bin\ffmpeg.exe)
            try
            {
                string toolsDirectory = Path.Combine(exeDirectory, "tools");
                if (Directory.Exists(toolsDirectory))
                {
                    var subdirs = Directory.GetDirectories(toolsDirectory);
                    foreach (var subdir in subdirs)
                    {
                        // Сначала проверяем прямо в подпапке
                        string subdirFfmpegPath = Path.Combine(subdir, "ffmpeg.exe");
                        if (File.Exists(subdirFfmpegPath))
                        {
                            MainWindow.Log($"[FfmpegHelper] ffmpeg found in subfolder: {subdirFfmpegPath}");
                            return subdirFfmpegPath;
                        }
                        
                        // Затем проверяем в подпапке bin (например, tools\ffmpeg-*\bin\ffmpeg.exe)
                        string binFfmpegPath = Path.Combine(subdir, "bin", "ffmpeg.exe");
                        if (File.Exists(binFfmpegPath))
                        {
                            MainWindow.Log($"[FfmpegHelper] ffmpeg found in subfolder bin: {binFfmpegPath}");
                            return binFfmpegPath;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[FfmpegHelper] Error searching ffmpeg in subfolders: {ex.Message}");
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
        /// <param name="inputFilePath">Путь к входному файлу (для расчета таймаута на основе размера)</param>
        /// <returns>true если конвертация успешна, false в противном случае</returns>
        public static async Task<bool> ConvertAsync(string ffmpegPath, string arguments, string outputFilePath, string logPrefix, string? inputFilePath = null)
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

                    // Вычисляем таймаут на основе размера входного файла (если указан)
                    TimeSpan timeout = DefaultFfmpegTimeout;
                    if (!string.IsNullOrEmpty(inputFilePath) && File.Exists(inputFilePath))
                    {
                        try
                        {
                            long fileSizeBytes = new FileInfo(inputFilePath).Length;
                            // Для файлов больше 5 МБ увеличиваем таймаут: примерно 1 минута на МБ
                            // Минимум 2 минуты, максимум 10 минут
                            double estimatedMinutes = Math.Max(2.0, Math.Min(10.0, fileSizeBytes / (1024.0 * 1024.0)));
                            timeout = TimeSpan.FromMinutes(estimatedMinutes);
                            MainWindow.Log($"{logPrefix} Calculated timeout: {timeout.TotalMinutes:F1} minutes (file size: {fileSizeBytes / 1024.0 / 1024.0:F2} MB)");
                        }
                        catch
                        {
                            // Используем дефолтный таймаут при ошибке
                        }
                    }
                    
                    var exitTask = process.WaitForExitAsync();
                    var completed = await Task.WhenAny(exitTask, Task.Delay(timeout));
                    if (completed != exitTask)
                    {
                        MainWindow.Log($"{logPrefix} WARNING: ffmpeg timed out after {timeout.TotalSeconds:F0}s, killing process");
                        try
                        {
                            process.Kill(entireProcessTree: true);
                        }
                        catch { }
                        return false;
                    }

                    await exitTask;
                    
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
