using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.AppHost
{
    /// <summary>
    /// UI-agnostic port of the WebRTC recording pipeline from the WPF <c>CallWindow</c>:
    /// sends <c>startRecording</c>/<c>stopRecording</c> to <c>phone.js</c> and feeds the
    /// <c>recording_start</c> / <c>recording_chunk</c> / <c>recording_complete</c> events into a
    /// <see cref="WebRtcCallRecorder"/>. Events are processed sequentially on a background queue
    /// so byte[] conversion never blocks the UI thread.
    /// </summary>
    public sealed class WebRtcRecordingSession : IDisposable
    {
        private readonly WebRtcService _service;
        private readonly string _logPrefix;
        private readonly SemaphoreSlim _queue = new(1, 1);
        private WebRtcCallRecorder? _recorder;
        private bool _stopRequested;
        private bool _disposed;

        /// <summary>Raised (on a background thread) once the WAV/WebM has been written to disk.</summary>
        public event Action<string>? RecordingFinalized;

        public string? RecordingFilePath => _recorder?.RecordingFilePath;
        public bool IsRecording => _recorder?.IsRecording == true;
        public bool IsActive => _recorder != null;

        public WebRtcRecordingSession(WebRtcService service, string logPrefix = "[WebRTC Recording]")
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logPrefix = logPrefix;
        }

        public void Start(string phoneNumber, DateTime callStartTime)
        {
            if (_disposed) return;
            if (_recorder != null && _recorder.IsRecording) return;

            try
            {
                _recorder?.Dispose();
                _recorder = new WebRtcCallRecorder();
                _stopRequested = false;
                _recorder.StartRecording(phoneNumber, callStartTime, AppDataHelper.GetRecordingsDirectory());
                AppLog.Log($"{_logPrefix} started for {phoneNumber} → {_recorder.RecordingFilePath}");

                _ = Task.Run(async () =>
                {
                    try { await _service.SendCommandAsync(new { cmd = "startRecording" }).ConfigureAwait(false); }
                    catch (Exception ex) { AppLog.Log($"{_logPrefix} startRecording command failed: {ex.Message}"); }
                });
            }
            catch (Exception ex)
            {
                AppLog.Log($"{_logPrefix} start failed: {ex.Message}");
            }
        }

        /// <summary>Ask the JS side to stop and flush; the recorder is finalized when <c>recording_complete</c> arrives.</summary>
        public void Stop()
        {
            if (_stopRequested || _recorder == null) return;
            _stopRequested = true;
            if (!_recorder.IsRecording) return;

            _ = Task.Run(async () =>
            {
                try { await _service.SendCommandAsync(new { cmd = "stopRecording" }).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Log($"{_logPrefix} stopRecording command failed: {ex.Message}"); }
            });
        }

        public static bool IsRecordingEvent(string? type) =>
            type == "recording_start" || type == "recording_chunk" || type == "recording_complete" || type == "recording_noop";

        /// <summary>Enqueue a recording event. Safe to call from any thread.</summary>
        public void HandleEvent(WebRtcEventDto dto)
        {
            if (dto == null || _recorder == null || !IsRecordingEvent(dto.Type)) return;
            _ = Task.Run(async () =>
            {
                await _queue.WaitAsync().ConfigureAwait(false);
                try { await ProcessAsync(dto).ConfigureAwait(false); }
                catch (Exception ex) { AppLog.Log($"{_logPrefix} {dto.Type} error: {ex.Message}"); }
                finally { _queue.Release(); }
            });
        }

        private async Task ProcessAsync(WebRtcEventDto dto)
        {
            var recorder = _recorder;
            if (recorder == null) return;
            if (dto.Data == null || !dto.Data.HasValue) return;
            var data = dto.Data.Value;

            switch (dto.Type)
            {
                case "recording_start":
                    if (!recorder.IsRecording) return;
                    if (data.TryGetProperty("totalSize", out var totalSizeEl) &&
                        data.TryGetProperty("totalChunks", out var totalChunksEl) &&
                        data.TryGetProperty("hash", out var hashEl))
                    {
                        recorder.HandleRecordingStart(totalSizeEl.GetInt32(), totalChunksEl.GetInt32(), hashEl.GetString() ?? "");
                        AppLog.Log($"{_logPrefix} recording_start: {totalSizeEl.GetInt32()} bytes / {totalChunksEl.GetInt32()} chunks");
                    }
                    return;

                case "recording_chunk":
                    if (!recorder.IsAcceptingRecordingChunks) return;
                    if (data.TryGetProperty("chunkIndex", out var idxEl) &&
                        data.TryGetProperty("data", out var dataEl) &&
                        data.TryGetProperty("offset", out var offEl))
                    {
                        var bytes = ReadBytes(dataEl);
                        if (bytes != null && bytes.Length > 0)
                            recorder.HandleRecordingChunk(idxEl.GetInt32(), bytes, offEl.GetInt32());
                    }
                    return;

                case "recording_complete":
                    string? hash = data.TryGetProperty("hash", out var h) ? h.GetString() : null;
                    byte[]? audio = data.TryGetProperty("audioData", out var audioEl) ? ReadBytes(audioEl) : null;

                    if (audio != null && audio.Length > 0)
                    {
                        AppLog.Log($"{_logPrefix} recording_complete: {audio.Length / 1024} KB inline");
                        await recorder.SaveCompleteRecordingAsync(audio, hash).ConfigureAwait(false);
                        recorder.StopRecording();
                    }
                    else if (!string.IsNullOrEmpty(hash))
                    {
                        AppLog.Log($"{_logPrefix} recording_complete: finalizing chunks (hash={hash})");
                        await recorder.SaveCompleteRecordingAsync(Array.Empty<byte>(), hash).ConfigureAwait(false);
                        recorder.StopRecording();
                    }
                    else
                    {
                        AppLog.Log($"{_logPrefix} recording_complete: no audioData and no hash — nothing saved");
                        return;
                    }

                    var path = recorder.RecordingFilePath;
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { RecordingFinalized?.Invoke(path); } catch { }
                    }
                    return;
            }
        }

        private static byte[]? ReadBytes(JsonElement el)
        {
            if (el.ValueKind == JsonValueKind.Array)
            {
                int len = el.GetArrayLength();
                var bytes = new byte[len];
                int i = 0;
                foreach (var item in el.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Number) bytes[i++] = (byte)item.GetInt32();
                if (i != bytes.Length) Array.Resize(ref bytes, i);
                return bytes;
            }
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (string.IsNullOrEmpty(s)) return null;
                try { return Convert.FromBase64String(s); } catch { return null; }
            }
            return null;
        }

        /// <summary>
        /// Dispose after a grace period so a late <c>recording_complete</c> (sent by JS after hangup)
        /// still reaches the recorder. Mirrors the 10 s delay used by the WPF window.
        /// </summary>
        public void DisposeAfter(TimeSpan delay)
        {
            _ = Task.Delay(delay).ContinueWith(_ => Dispose(), TaskScheduler.Default);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                var r = _recorder;
                _recorder = null;
                if (r != null)
                {
                    if (r.IsRecording) r.StopRecording();
                    r.Dispose();
                }
            }
            catch (Exception ex) { AppLog.Log($"{_logPrefix} dispose: {ex.Message}"); }
        }
    }
}
