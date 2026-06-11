using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        /// 1. tools/ffmpeg(.exe) рядом с исполняемым файлом
        /// 2. Подпапки tools (например tools/ffmpeg-*/bin/ffmpeg)
        /// 3. Системный PATH
        /// </summary>
        public static string? FindFfmpegPath()
        {
            string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string toolsDirectory = Path.Combine(exeDirectory, "tools");

            if (Directory.Exists(toolsDirectory))
            {
                // 1. Platform pack: tools/Windows/…/bin/ffmpeg.exe or tools/MacOS/ffmpeg
                foreach (var platformFolder in GetPlatformToolFolderNames())
                {
                    string platformPath = Path.Combine(toolsDirectory, platformFolder);
                    string? inPlatform = TryFindFfmpegInTree(platformPath, maxDepth: 4);
                    if (inPlatform != null)
                        return inPlatform;
                }

                // 2. Flat layout: tools/ffmpeg(.exe)
                string? inToolsRoot = TryFindFfmpegInTree(toolsDirectory, maxDepth: 0);
                if (inToolsRoot != null)
                    return inToolsRoot;

                // 3. Legacy: any first-level subfolder (tools/ffmpeg-*/bin/…)
                try
                {
                    foreach (var subdir in Directory.GetDirectories(toolsDirectory))
                    {
                        string? inSubdir = TryFindFfmpegInTree(subdir, maxDepth: 2);
                        if (inSubdir != null)
                            return inSubdir;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[FfmpegHelper] Error searching ffmpeg in subfolders: {ex.Message}");
                }
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
                            AppLog.Log($"[FfmpegHelper] ffmpeg found in system PATH");
                            return "ffmpeg"; // Возвращаем имя без расширения для использования через PATH
                        }
                    }
                }
            }
            catch
            {
                // ffmpeg не найден в PATH - это нормально
            }

            AppLog.Log($"[FfmpegHelper] ffmpeg not found in {toolsDirectory} and not in system PATH");
            return null;
        }

        private static string[] GetFfmpegBinaryNames()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new[] { "ffmpeg.exe", "ffmpeg" };
            return new[] { "ffmpeg" };
        }

        private static string[] GetPlatformToolFolderNames()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new[] { "Windows" };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new[] { "MacOS", "macOS" };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return new[] { "Linux" };
            return Array.Empty<string>();
        }

        private static string? TryFindFfmpegInTree(string directory, int maxDepth)
        {
            if (!Directory.Exists(directory))
                return null;

            foreach (var binaryName in GetFfmpegBinaryNames())
            {
                string directPath = Path.Combine(directory, binaryName);
                if (File.Exists(directPath))
                {
                    AppLog.Log($"[FfmpegHelper] ffmpeg found at: {directPath}");
                    return directPath;
                }

                string binPath = Path.Combine(directory, "bin", binaryName);
                if (File.Exists(binPath))
                {
                    AppLog.Log($"[FfmpegHelper] ffmpeg found at: {binPath}");
                    return binPath;
                }
            }

            if (maxDepth <= 0)
                return null;

            try
            {
                foreach (var subdir in Directory.GetDirectories(directory))
                {
                    string? nested = TryFindFfmpegInTree(subdir, maxDepth - 1);
                    if (nested != null)
                        return nested;
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[FfmpegHelper] Error searching in {directory}: {ex.Message}");
            }

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
                        AppLog.Log($"{logPrefix} Failed to start ffmpeg process");
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
                            AppLog.Log($"{logPrefix} Calculated timeout: {timeout.TotalMinutes:F1} minutes (file size: {fileSizeBytes / 1024.0 / 1024.0:F2} MB)");
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
                        AppLog.Log($"{logPrefix} WARNING: ffmpeg timed out after {timeout.TotalSeconds:F0}s, killing process");
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
                        AppLog.Log($"{logPrefix} Conversion successful: {fileInfo.Length / 1024} KB ({outputFilePath})");
                        
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            AppLog.Log($"{logPrefix} ffmpeg stderr: {error.Trim()}");
                        }
                        
                        return true;
                    }
                    else
                    {
                        AppLog.Log($"{logPrefix} Conversion failed (exit code: {process.ExitCode})");
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            AppLog.Log($"{logPrefix} ffmpeg stderr: {error.Trim()}");
                        }
                        if (!string.IsNullOrWhiteSpace(output))
                        {
                            AppLog.Log($"{logPrefix} ffmpeg stdout: {output.Trim()}");
                        }
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"{logPrefix} Error during ffmpeg conversion: {ex.Message}");
                return false;
            }
        }
    }
}
