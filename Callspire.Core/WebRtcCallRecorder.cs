using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Рекордер для записи WebRTC звонков через MediaRecorder API в JavaScript
    /// Получает аудио данные целиком одним файлом после остановки записи
    /// Автоматически конвертирует WebM в WAV через ffmpeg.
    /// ОПТИМИЗИРОВАНО: Параллельная конвертация до 3 файлов одновременно для избежания очереди при стресс-тестах.
    /// </summary>
    public class WebRtcCallRecorder : CallRecorderBase
    {
        /// <summary>Параллельная конвертация до 3 файлов одновременно для избежания очереди при множественных звонках.</summary>
        private static readonly SemaphoreSlim s_conversionLock = new SemaphoreSlim(3, 3);

        private string? _webmFilePath; // Исходный WebM файл (сохраняем для резерва)
        private bool _isDisposed = false;
        
        // Для сборки файла из чанков (ВАЖНО: собирать строго по chunkIndex, иначе WebM может повредиться → "щелчки")
        private Dictionary<int, byte[]>? _receivedChunksByIndex;
        private int? _expectedTotalSize;
        private int? _expectedTotalChunks;
        private string? _expectedHash;
        /// <summary>JS stopRecording was requested; keep accepting chunks until recording_complete / SaveComplete.</summary>
        private bool _stopRequested;

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

                _stopRequested = false;
            }

            base.StartRecording(phoneNumber, callStartTime, recordingsDirectory);
            
            lock (_lockObject)
            {
                if (!_isRecording) return;

                _receivedChunksByIndex = new Dictionary<int, byte[]>();
                _expectedTotalSize = null;
                _expectedTotalChunks = null;
                _expectedHash = null;
            }
        }

        /// <summary>True while the JS recorder may still send chunk data (including after stopRecording until finalize).</summary>
        public bool IsAcceptingRecordingChunks
        {
            get
            {
                lock (_lockObject)
                {
                    if (_isDisposed || _receivedChunksByIndex == null)
                        return false;
                    return _isRecording || _stopRequested;
                }
            }
        }

        private void EndRecordingSessionLocked()
        {
            _isRecording = false;
            _stopRequested = false;
        }

        /// <summary>
        /// Генерирует пути к файлам записи
        /// </summary>
        protected override void GenerateRecordingPaths(string baseFileName, string recordingsDirectory)
        {
            string webmBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}.webm");
            // КРИТИЧНО: Используем WAV вместо MP3 для WebRTC звонков (более универсальный формат, лучше поддерживается AmoCRM)
            string wavBasePath = Path.Combine(recordingsDirectory, $"{baseFileName}.wav");
            
            // Генерируем уникальные пути
            _webmFilePath = GenerateUniqueFilePath(webmBasePath, path => File.Exists(path));
            _recordingFilePath = GenerateUniqueFilePath(wavBasePath, path => File.Exists(path));
            
            AppLog.Log($"[WebRtcCallRecorder] Recording paths: WebM={_webmFilePath}, WAV={_recordingFilePath}");
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

                if (_stopRequested)
                {
                    AppLog.Log("[WebRtcCallRecorder] Ignoring recording_start from JS (stop already requested — avoids wiping chunk buffer)");
                    return;
                }

                _expectedTotalSize = totalSize;
                _expectedTotalChunks = totalChunks;
                _expectedHash = hash;
                _receivedChunksByIndex?.Clear();
                
                AppLog.Log($"[WebRtcCallRecorder] Receiving {totalChunks} chunks ({totalSize / 1024} KB)");
            }
        }

        /// <summary>
        /// Обрабатывает чанк большого файла
        /// </summary>
        public void HandleRecordingChunk(int chunkIndex, byte[] chunkData, int offset)
        {
            lock (_lockObject)
            {
                if (_isDisposed || _receivedChunksByIndex == null)
                {
                    return;
                }

                // After JS stopRecording(), chunks may still arrive; do not drop them (was causing 0-byte WebM + failed Amo upload).
                if (!_isRecording && !_stopRequested)
                {
                    return;
                }

                try
                {
                    if (_receivedChunksByIndex.ContainsKey(chunkIndex))
                    {
                        // Дубликат чанка — игнорируем
                        return;
                    }

                    _receivedChunksByIndex[chunkIndex] = chunkData;
                    // Логируем только каждый 10-й чанк или последний, чтобы не засорять логи
                    if (_expectedTotalChunks == null || chunkIndex % 10 == 0 || chunkIndex == _expectedTotalChunks - 1)
                    {
                        AppLog.Log($"[WebRtcCallRecorder] Received chunk {chunkIndex + 1}/{_expectedTotalChunks} ({chunkData.Length} bytes, offset={offset})");
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[WebRtcCallRecorder] Error handling chunk: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Сохраняет полный WebM файл и конвертирует в WAV
        /// </summary>
        public async Task SaveCompleteRecordingAsync(byte[] webmData, string? hash = null)
        {
            try
            {
                // Если файл передавался чанками, собираем его
                byte[] finalData = webmData;

                lock (_lockObject)
                {
                    if (_receivedChunksByIndex != null && _receivedChunksByIndex.Count > 0)
                    {
                        int chunkCount = _receivedChunksByIndex.Count;
                        int? expectedChunks = _expectedTotalChunks;

                        // Если знаем ожидаемое число чанков — проверяем, что все пришли
                        if (expectedChunks.HasValue)
                        {
                            for (int i = 0; i < expectedChunks.Value; i++)
                            {
                                if (!_receivedChunksByIndex.ContainsKey(i))
                                {
                                    AppLog.Log($"[WebRtcCallRecorder] ❌ Missing chunk {i + 1}/{expectedChunks.Value}. Cannot assemble recording safely.");
                                    // Сбрасываем буфер, чтобы не использовать поврежденные данные
                                    _receivedChunksByIndex.Clear();
                                    return;
                                }
                            }
                        }

                        // Собираем строго по индексу (0..N-1). Если expectedChunks неизвестен — по возрастанию ключей.
                        var orderedKeys = expectedChunks.HasValue
                            ? Enumerable.Range(0, expectedChunks.Value)
                            : _receivedChunksByIndex.Keys.OrderBy(k => k).ToArray();

                        int totalSize = 0;
                        foreach (var k in orderedKeys)
                        {
                            totalSize += _receivedChunksByIndex[k].Length;
                        }

                        finalData = new byte[totalSize];
                        int writeOffset = 0;
                        foreach (var k in orderedKeys)
                        {
                            var chunk = _receivedChunksByIndex[k];
                            Buffer.BlockCopy(chunk, 0, finalData, writeOffset, chunk.Length);
                            writeOffset += chunk.Length;
                        }

                        _receivedChunksByIndex.Clear();
                        AppLog.Log($"[WebRtcCallRecorder] Assembled {chunkCount} chunks (ordered): {totalSize / 1024} KB");
                    }
                }

                if (_isDisposed || string.IsNullOrEmpty(_webmFilePath) || string.IsNullOrEmpty(_recordingFilePath))
                {
                    AppLog.Log($"[WebRtcCallRecorder] Cannot save recording: disposed={_isDisposed}, webmPath={_webmFilePath}, wavPath={_recordingFilePath}");
                    return;
                }

                // Проверяем целостность файла через SHA256
                if (!string.IsNullOrEmpty(hash) || !string.IsNullOrEmpty(_expectedHash))
                {
                    string calculatedHash = CalculateSha256(finalData);
                    string expectedHash = hash ?? _expectedHash ?? "";
                    
                    if (!string.IsNullOrEmpty(expectedHash) && !calculatedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        AppLog.Log($"[WebRtcCallRecorder] WARNING: Hash mismatch!");
                        // Продолжаем, но логируем предупреждение
                    }
                    // Не логируем успешную проверку хеша - это избыточно
                }

                // Сохраняем WebM файл (исходник) асинхронно, чтобы не блокировать UI
                AppLog.Log($"[WebRtcCallRecorder] Saving WebM file: {_webmFilePath}");
                await Task.Run(() =>
                {
                    File.WriteAllBytes(_webmFilePath, finalData);
                });
                AppLog.Log($"[WebRtcCallRecorder] WebM saved: {Path.GetFileName(_webmFilePath)} ({finalData.Length / 1024} KB), FileExists={File.Exists(_webmFilePath)}");

                // Конвертируем WebM в WAV параллельно (до 3 файлов одновременно через SemaphoreSlim)
                // Это позволяет обрабатывать несколько звонков подряд без очереди, ускоряя общую обработку
                AppLog.Log($"[WebRtcCallRecorder] Starting conversion to WAV: {_webmFilePath} -> {_recordingFilePath}");
                await s_conversionLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    bool converted = await ConvertWebMToWavAsync(_webmFilePath, _recordingFilePath).ConfigureAwait(false);
                
                if (converted && File.Exists(_recordingFilePath))
                {
                    var fileInfo = new FileInfo(_recordingFilePath);
                    AppLog.Log($"[WebRtcCallRecorder] ✅ WAV conversion complete: {Path.GetFileName(_recordingFilePath)} ({fileInfo.Length / 1024} KB)");
                    
                    // ОПТИМИЗАЦИЯ: Удаляем WebM файл после успешной конвертации в WAV для экономии места
                    try
                    {
                        if (File.Exists(_webmFilePath))
                        {
                            File.Delete(_webmFilePath);
                            AppLog.Log($"[WebRtcCallRecorder] ✅ WebM file deleted after successful WAV conversion: {Path.GetFileName(_webmFilePath)}");
                        }
                    }
                    catch (Exception deleteEx)
                    {
                        // Не критично, если не удалось удалить - просто логируем
                        AppLog.Log($"[WebRtcCallRecorder] ⚠️ Failed to delete WebM file after conversion: {deleteEx.Message}");
                    }
                }
                else
                {
                    // Если WAV не получился, пробуем конвертировать в MP3 как fallback
                    AppLog.Log($"[WebRtcCallRecorder] ⚠️ WAV conversion failed, trying MP3 conversion as fallback...");
                    string mp3Path = Path.ChangeExtension(_recordingFilePath, ".mp3");
                    bool mp3Converted = await ConvertWebMToMp3Async(_webmFilePath, mp3Path);
                    
                    if (mp3Converted && File.Exists(mp3Path))
                    {
                        _recordingFilePath = mp3Path;
                        var fileInfo = new FileInfo(mp3Path);
                        AppLog.Log($"[WebRtcCallRecorder] ✅ MP3 conversion complete (fallback): {Path.GetFileName(mp3Path)} ({fileInfo.Length / 1024} KB)");
                        
                        // ОПТИМИЗАЦИЯ: Удаляем WebM файл после успешной конвертации в MP3
                        try
                        {
                            if (File.Exists(_webmFilePath))
                            {
                                File.Delete(_webmFilePath);
                                AppLog.Log($"[WebRtcCallRecorder] ✅ WebM file deleted after successful MP3 conversion: {Path.GetFileName(_webmFilePath)}");
                            }
                        }
                        catch (Exception deleteEx)
                        {
                            AppLog.Log($"[WebRtcCallRecorder] ⚠️ Failed to delete WebM file after MP3 conversion: {deleteEx.Message}");
                        }
                    }
                    else
                    {
                        // Если и MP3 не получился, используем WebM файл как основной
                        // AmoCRM может поддерживать WebM для call_in/call_out примечаний
                        // Если не поддерживает - файл будет прикреплен, но кнопки "Прослушать"/"Скачать" могут не работать
                        AppLog.Log($"[WebRtcCallRecorder] ⚠️ MP3 conversion also failed, using WebM directly: {Path.GetFileName(_webmFilePath)}");
                        AppLog.Log($"[WebRtcCallRecorder] Note: WebM may not be supported by AmoCRM for call_in/call_out notes, but file will be attached");
                        _recordingFilePath = _webmFilePath;
                        // WebM файл НЕ удаляем, так как он используется как основной файл записи
                    }
                }
                }
                finally
                {
                    s_conversionLock.Release();
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcCallRecorder] Error saving complete recording: {ex.Message}");
            }
            finally
            {
                lock (_lockObject)
                {
                    EndRecordingSessionLocked();
                }
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

                if (_stopRequested)
                {
                    return Task.CompletedTask;
                }

                _stopRequested = true;
                AppLog.Log($"[WebRtcCallRecorder] Recording stop requested (still accepting chunks until recording_complete)");
            }
            
            return Task.CompletedTask;
        }

        /// <summary>
        /// Конвертирует WebM файл в MP3 используя ffmpeg
        /// </summary>
        private async Task<bool> ConvertWebMToMp3Async(string webmPath, string mp3Path)
        {
            AppLog.Log($"[WebRtcCallRecorder] ConvertWebMToMp3Async: webmPath={webmPath}, mp3Path={mp3Path}");
            AppLog.Log($"[WebRtcCallRecorder] WebM file exists: {File.Exists(webmPath)}");
            
            string? ffmpegPath = FfmpegHelper.FindFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AppLog.Log($"[WebRtcCallRecorder] ⚠️ ffmpeg not found, skipping MP3 conversion.");
                return false;
            }

            AppLog.Log($"[WebRtcCallRecorder] Using ffmpeg: {ffmpegPath}");

            // Конвертируем WebM в MP3 с битрейтом 128k
            string args = $"-y -loglevel error -i \"{webmPath}\" -c:a libmp3lame -b:a 128k \"{mp3Path}\"";
            AppLog.Log($"[WebRtcCallRecorder] ffmpeg args: {args}");

            bool result = await FfmpegHelper.ConvertAsync(ffmpegPath, args, mp3Path, "[WebRtcCallRecorder]", webmPath);
            
            AppLog.Log($"[WebRtcCallRecorder] ConvertWebMToMp3Async result: {result}, MP3 file exists: {File.Exists(mp3Path)}");
            
            return result;
        }

        /// <summary>
        /// Конвертирует WebM файл в WAV используя ffmpeg
        /// WAV - более универсальный формат, поддерживается большинством систем
        /// </summary>
        private async Task<bool> ConvertWebMToWavAsync(string webmPath, string wavPath)
        {
            AppLog.Log($"[WebRtcCallRecorder] ConvertWebMToWavAsync: webmPath={webmPath}, wavPath={wavPath}");
            AppLog.Log($"[WebRtcCallRecorder] WebM file exists: {File.Exists(webmPath)}");
            
            string? ffmpegPath = FfmpegHelper.FindFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                AppLog.Log($"[WebRtcCallRecorder] ⚠️ ffmpeg not found, skipping WAV conversion.");
                return false;
            }

            AppLog.Log($"[WebRtcCallRecorder] Using ffmpeg for WAV: {ffmpegPath}");

            // Конвертируем WebM в WAV (PCM, 16-bit, 48kHz, моно) БЕЗ обрезки тишины,
            // чтобы не терять ни начало, ни окончание разговора.
            // При необходимости фильтрацию тишины лучше делать уже на стороне плеера/аналитики.
            // aresample=async=1 помогает сгладить разрывы/дрожание таймстемпов, которые могут давать "щелчки".
            string args = $"-y -loglevel error -i \"{webmPath}\" -vn -map 0:a:0 -acodec pcm_s16le -ar 48000 -ac 1 -af aresample=async=1:first_pts=0 \"{wavPath}\"";
            AppLog.Log($"[WebRtcCallRecorder] ffmpeg WAV args: {args}");

            bool result = await FfmpegHelper.ConvertAsync(ffmpegPath, args, wavPath, "[WebRtcCallRecorder]", webmPath);
            
            AppLog.Log($"[WebRtcCallRecorder] ConvertWebMToWavAsync result: {result}, WAV file exists: {File.Exists(wavPath)}");
            
            return result;
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
                EndRecordingSessionLocked();
            }
        }
    }
}
