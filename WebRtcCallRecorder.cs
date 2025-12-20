using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Рекордер для записи WebRTC звонков через MediaRecorder API в JavaScript
    /// Получает аудио данные целиком одним файлом после остановки записи
    /// Автоматически конвертирует WebM в WAV для совместимости с MediaElement
    /// </summary>
    public class WebRtcCallRecorder : CallRecorderBase
    {
        private string? _webmFilePath; // Исходный WebM файл (сохраняем для резерва)
        private bool _isDisposed = false;
        
        // Для сборки файла из чанков
        private List<byte[]>? _receivedChunks;
        private int? _expectedTotalSize;
        private int? _expectedTotalChunks;
        private string? _expectedHash;

        public string? WebmFilePath => _webmFilePath; // Исходный WebM файл

        /// <summary>
        /// Начинает запись звонка (подготавливает пути к файлам)
        /// </summary>
        public override void StartRecording(string phoneNumber, DateTime callStartTime, string recordingsDirectory)
        {
            lock (_lockObject)
            {
                if (_isDisposed)
                {
                    return;
                }
            }

            base.StartRecording(phoneNumber, callStartTime, recordingsDirectory);
            
            lock (_lockObject)
            {
                if (!_isRecording) return;

                _receivedChunks = new List<byte[]>();
                _expectedTotalSize = null;
                _expectedTotalChunks = null;
                _expectedHash = null;
            }
        }

        /// <summary>
        /// Генерирует пути к файлам записи
        /// </summary>
        protected override void GenerateRecordingPaths(string baseFileName, string recordingsDirectory)
        {
            string webmBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}.webm");
            string wavBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}.wav");
            
            // Генерируем уникальные пути
            _webmFilePath = GenerateUniqueFilePath(webmBasePath, path => File.Exists(path));
            _recordingFilePath = GenerateUniqueFilePath(wavBasePath, path => File.Exists(path));
            
            MainWindow.Log($"[WebRtcCallRecorder] Recording paths: WebM={_webmFilePath}, WAV={_recordingFilePath}");
        }

        /// <summary>
        /// Возвращает префикс для логов
        /// </summary>
        protected override string GetLogPrefix() => "WebRtcCallRecorder";

        /// <summary>
        /// Обрабатывает начало передачи большого файла (метаданные)
        /// </summary>
        public void HandleRecordingStart(int totalSize, int totalChunks, string hash)
        {
            lock (_lockObject)
            {
                if (!_isRecording || _isDisposed)
                {
                    return;
                }

                _expectedTotalSize = totalSize;
                _expectedTotalChunks = totalChunks;
                _expectedHash = hash;
                _receivedChunks?.Clear();
                
                MainWindow.Log($"[WebRtcCallRecorder] Receiving {totalChunks} chunks ({totalSize / 1024} KB)");
            }
        }

        /// <summary>
        /// Обрабатывает чанк большого файла
        /// </summary>
        public void HandleRecordingChunk(int chunkIndex, byte[] chunkData, int offset)
        {
            lock (_lockObject)
            {
                if (!_isRecording || _isDisposed || _receivedChunks == null)
                {
                    return;
                }

                try
                {
                    _receivedChunks.Add(chunkData);
                    // Логируем только каждый 10-й чанк или последний, чтобы не засорять логи
                    if (_expectedTotalChunks == null || chunkIndex % 10 == 0 || chunkIndex == _expectedTotalChunks - 1)
                    {
                        MainWindow.Log($"[WebRtcCallRecorder] Received chunk {chunkIndex + 1}/{_expectedTotalChunks} ({chunkData.Length} bytes)");
                    }
                }
                catch (Exception ex)
                {
                    MainWindow.Log($"[WebRtcCallRecorder] Error handling chunk: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Сохраняет полный WebM файл и конвертирует в WAV
        /// </summary>
        public async Task SaveCompleteRecordingAsync(byte[] webmData, string? hash = null)
        {
            // Если файл передавался чанками, собираем его
            byte[] finalData = webmData;
            
            lock (_lockObject)
            {
                if (_receivedChunks != null && _receivedChunks.Count > 0)
                {
                    int chunkCount = _receivedChunks.Count;
                    int totalSize = _receivedChunks.Sum(chunk => chunk.Length);
                    finalData = new byte[totalSize];
                    int offset = 0;
                    foreach (var chunk in _receivedChunks)
                    {
                        Buffer.BlockCopy(chunk, 0, finalData, offset, chunk.Length);
                        offset += chunk.Length;
                    }
                    _receivedChunks.Clear();
                    MainWindow.Log($"[WebRtcCallRecorder] Assembled {chunkCount} chunks: {totalSize / 1024} KB");
                }
            }

            if (_isDisposed || string.IsNullOrEmpty(_webmFilePath) || string.IsNullOrEmpty(_recordingFilePath))
            {
                MainWindow.Log($"[WebRtcCallRecorder] Cannot save recording: disposed={_isDisposed}, webmPath={_webmFilePath}, wavPath={_recordingFilePath}");
                return;
            }

            try
            {
                // Проверяем целостность файла через SHA256
                if (!string.IsNullOrEmpty(hash) || !string.IsNullOrEmpty(_expectedHash))
                {
                    string calculatedHash = CalculateSha256(finalData);
                    string expectedHash = hash ?? _expectedHash ?? "";
                    
                    if (!string.IsNullOrEmpty(expectedHash) && !calculatedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        MainWindow.Log($"[WebRtcCallRecorder] WARNING: Hash mismatch!");
                        // Продолжаем, но логируем предупреждение
                    }
                    // Не логируем успешную проверку хеша - это избыточно
                }

                // Сохраняем WebM файл (исходник) асинхронно, чтобы не блокировать UI
                await Task.Run(() =>
                {
                    File.WriteAllBytes(_webmFilePath, finalData);
                });
                MainWindow.Log($"[WebRtcCallRecorder] WebM saved: {Path.GetFileName(_webmFilePath)} ({finalData.Length / 1024} KB)");

                // Конвертируем WebM в WAV в фоновом потоке
                MainWindow.Log($"[WebRtcCallRecorder] Starting conversion to WAV...");
                bool converted = await ConvertWebMToWavAsync(_webmFilePath, _recordingFilePath);
                
                if (converted)
                {
                    MainWindow.Log($"[WebRtcCallRecorder] Conversion complete: {Path.GetFileName(_recordingFilePath)}");
                    // WebM файл оставляем как исходник (не удаляем)
                }
                else
                {
                    // Если конвертация не удалась, используем WebM файл как основной
                    MainWindow.Log($"[WebRtcCallRecorder] Conversion failed, using WebM: {Path.GetFileName(_webmFilePath)}");
                    _recordingFilePath = _webmFilePath;
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[WebRtcCallRecorder] Error saving complete recording: {ex.Message}");
            }
        }

        /// <summary>
        /// Вычисляет SHA256 хеш файла
        /// </summary>
        private string CalculateSha256(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(data);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Останавливает запись звонка (ожидает завершения сохранения файла)
        /// </summary>
        public override Task StopRecordingAsync(DateTime? callEndTime = null)
        {
            lock (_lockObject)
            {
                if (!_isRecording || _isDisposed)
                {
                    return Task.CompletedTask;
                }

                _isRecording = false;
                MainWindow.Log($"[WebRtcCallRecorder] Recording stopped (waiting for complete file)");
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Конвертирует WebM файл в WAV используя ffmpeg
        /// </summary>
        private async Task<bool> ConvertWebMToWavAsync(string webmPath, string wavPath)
        {
            string? ffmpegPath = FfmpegHelper.FindFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                MainWindow.Log($"[WebRtcCallRecorder] ffmpeg not found, skipping conversion. WebM file will be used as-is.");
                return false;
            }

            // PCM 16-bit, 48kHz, mono для максимальной совместимости
            string args = $"-y -loglevel error -i \"{webmPath}\" -ac 1 -ar 48000 -c:a pcm_s16le \"{wavPath}\"";

            return await FfmpegHelper.ConvertAsync(ffmpegPath, args, wavPath, "[WebRtcCallRecorder]");
        }

        public override void Dispose()
        {
            lock (_lockObject)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                StopRecording();
            }
        }
    }
}
