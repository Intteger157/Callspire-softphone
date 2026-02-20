using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Non-blocking file logger that writes to %LOCALAPPDATA%\Callspire\Logs.
    /// Designed to keep working even when UI is frozen (as long as background threads run).
    /// </summary>
    public sealed class FileLogService : IDisposable
    {
        public static FileLogService Instance { get; } = new FileLogService();

        private readonly ConcurrentQueue<string> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        private readonly object _fileLock = new();
        private DateTime _currentDay = DateTime.MinValue;
        private string? _currentFilePath;
        private long _currentFileSizeBytes = 0;

        // Keep logs bounded; if app is completely stuck, don't OOM.
        private const int MaxQueuedLines = 50_000;
        private int _droppedLines = 0;

        // Rollover threshold
        private const long MaxFileSizeBytes = 10L * 1024L * 1024L; // 10MB

        private FileLogService()
        {
            _worker = Task.Run(WorkerLoopAsync);
        }

        public void Enqueue(string message)
        {
            try
            {
                if (_cts.IsCancellationRequested) return;

                // Format: 2025-12-22 12:34:56.789 [T12] message
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] {message}";

                if (_queue.Count > MaxQueuedLines)
                {
                    Interlocked.Increment(ref _droppedLines);
                    return;
                }

                _queue.Enqueue(line);
                _signal.Release();
            }
            catch
            {
                // Never throw from logging
            }
        }

        private async Task WorkerLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_cts.Token);
                    DrainOnce();
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileLogService] WorkerLoopAsync crashed: {ex.Message}");
            }
            finally
            {
                // Best-effort flush
                try { DrainAll(); } catch { }
            }
        }

        private void DrainOnce()
        {
            // Batch write for performance
            var sb = new StringBuilder(8 * 1024);
            int taken = 0;
            while (taken < 500 && _queue.TryDequeue(out var line))
            {
                taken++;
                sb.AppendLine(line);
            }

            if (taken == 0) return;

            var dropped = Interlocked.Exchange(ref _droppedLines, 0);
            if (dropped > 0)
            {
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [T{Environment.CurrentManagedThreadId}] [FileLogService] WARNING: dropped {dropped} log line(s) due to queue overflow");
            }

            WriteBatch(sb.ToString());
        }

        private void DrainAll()
        {
            var sb = new StringBuilder(64 * 1024);
            while (_queue.TryDequeue(out var line))
            {
                sb.AppendLine(line);
                if (sb.Length > 256 * 1024)
                {
                    WriteBatch(sb.ToString());
                    sb.Clear();
                }
            }

            if (sb.Length > 0)
            {
                WriteBatch(sb.ToString());
            }
        }

        private void EnsureFileReady()
        {
            var today = DateTime.Now.Date;
            if (_currentFilePath != null && _currentDay == today && _currentFileSizeBytes < MaxFileSizeBytes)
            {
                return;
            }

            var logsDir = GetLogsDirectoryNoLog();
            Directory.CreateDirectory(logsDir);

            _currentDay = today;

            var baseName = $"softphone_{today:yyyyMMdd}.log";
            var path = Path.Combine(logsDir, baseName);

            // If file is too large, roll it
            if (File.Exists(path))
            {
                try
                {
                    var len = new FileInfo(path).Length;
                    if (len >= MaxFileSizeBytes)
                    {
                        var rolled = Path.Combine(logsDir, $"softphone_{today:yyyyMMdd}_{DateTime.Now:HHmmss}.log");
                        path = rolled;
                        len = 0;
                    }
                    _currentFileSizeBytes = len;
                }
                catch
                {
                    _currentFileSizeBytes = 0;
                }
            }
            else
            {
                _currentFileSizeBytes = 0;
            }

            _currentFilePath = path;
        }

        private void WriteBatch(string text)
        {
            try
            {
                lock (_fileLock)
                {
                    EnsureFileReady();

                    if (string.IsNullOrEmpty(_currentFilePath))
                    {
                        return;
                    }

                    File.AppendAllText(_currentFilePath, text, Encoding.UTF8);
                    _currentFileSizeBytes += Encoding.UTF8.GetByteCount(text);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileLogService] WriteBatch failed: {ex.Message}");
            }
        }

        private static string GetLogsDirectoryNoLog()
        {
            // IMPORTANT: don't call AppDataHelper here (it logs via MainWindow.Log and would recurse).
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Callspire", "Logs");
        }

        public void Shutdown()
        {
            try
            {
                _cts.Cancel();
                // Release worker if it's waiting
                _signal.Release();
                _worker.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
            finally
            {
                try { DrainAll(); } catch { }
            }
        }

        public void Dispose()
        {
            Shutdown();
            _signal.Dispose();
            _cts.Dispose();
        }
    }
}


