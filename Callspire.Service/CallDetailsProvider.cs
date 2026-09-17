using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.Service.Contracts;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// Backs the Swift CallDetailsView: resolves a history row into the full <see cref="CallDetailsDto"/>
    /// (WPF <c>CallDetailsWindow</c> content), prepares recordings for native AVAudioPlayer playback
    /// (ffmpeg transcode of non-WAV/MP3 files, as <c>RecordingPlayer</c> does) and runs Kommo retries.
    /// </summary>
    public sealed class CallDetailsProvider
    {
        private readonly IpcServer _ipc;
        private readonly DesktopAppController _controller;

        public CallDetailsProvider(IpcServer ipc, DesktopAppController controller)
        {
            _ipc = ipc;
            _controller = controller;
        }

        public void RegisterHandlers()
        {
            _ipc.Register("getCallDetails", p => Build(Resolve(p)));
            _ipc.Register("prepareRecording", (p, _) => PrepareRecordingAsync(Resolve(p)));
            _ipc.Register("retryKommo", async (p, _) =>
            {
                var call = Resolve(p);
                if (!_controller.CanRetryKommo) throw new IpcException("Kommo is not available", "kommo_unavailable");
                var (ok, error) = await _controller.RetryKommoForCallAsync(call).ConfigureAwait(false);
                return new { ok, error, details = Build(_controller.FindHistoryItem(call.PhoneNumber, call.CallTime) ?? call) };
            });
        }

        private CallHistoryItem Resolve(System.Text.Json.JsonElement? p)
        {
            var key = p.HasValue ? IpcJson.Deserialize<HistoryKeyParams>(p.Value) : null;
            if (key == null) throw new IpcException("phoneNumber/callTime are required", "bad_request");
            return _controller.FindHistoryItem(key.PhoneNumber, key.CallTime)
                ?? throw new IpcException("Call not found in history", "not_found");
        }

        private CallDetailsDto Build(CallHistoryItem call)
        {
            var vm = _controller.ViewModel;
            string? connectionName = call.ConnectionSlot switch
            {
                CallConnectionSlot.Main => vm.Main.DisplayName,
                CallConnectionSlot.Secondary => vm.Secondary.DisplayName,
                _ => null,
            };
            var duration = call.Duration ?? (call.WasAnswered ? CallStatisticsService.GetEffectiveTalkDuration(call) : TimeSpan.Zero);
            var s = _controller.Settings;
            return new CallDetailsDto
            {
                PhoneNumber = call.PhoneNumber, CallTime = call.CallTime,
                RingbackStart = call.RingbackStartTime, RingbackEnd = call.RingbackEndTime, AnswerTime = call.AnswerTime,
                WasAnswered = call.WasAnswered || call.AnswerTime.HasValue,
                EndedBy = call.EndedBy.ToString(),
                Duration = call.Duration, DurationText = duration > TimeSpan.Zero ? CallStatisticsService.FormatDuration(duration) : "",
                RecordingFilePath = call.RecordingFilePath, HasRecording = CallStatisticsService.HasLocalRecording(call),
                TransportLabel = call.Transport == CallTransport.WebRtc ? "WebRTC" : "SIP",
                OutboundCallerId = call.OutboundCallerId, ConnectionName = connectionName,
                IsIncoming = call.IsIncoming, Status = call.Status.ToString(),
                TechnicalDetails = call.TechnicalDetails?.ToList() ?? new(),
                KommoEnabled = s.EnableAmoCrmIntegration,
                KommoUploadStatus = call.AmoCrmUploadStatus.ToString(),
                KommoUploadReason = call.AmoCrmUploadReason,
                KommoLeadId = call.AmoCrmLeadId,
                KommoSubdomain = s.AmoCrmSubdomain,
                CanRetryKommo = s.EnableAmoCrmIntegration && _controller.CanRetryKommo,
            };
        }

        private static async Task<object?> PrepareRecordingAsync(CallHistoryItem call)
        {
            var path = call.RecordingFilePath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return new RecordingPlaybackDto { Error = "Recording file not found." };

            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".wav" or ".mp3" or ".m4a" or ".aac" or ".caf")
                return new RecordingPlaybackDto { FilePath = path };

            var ffmpeg = FfmpegHelper.FindFfmpegPath();
            if (ffmpeg == null) return new RecordingPlaybackDto { Error = "ffmpeg is required to play this recording format." };

            var temp = Path.Combine(Path.GetTempPath(), $"callspire-play-{Guid.NewGuid():N}.wav");
            bool ok = await FfmpegHelper.ConvertAsync(ffmpeg, $"-y -i \"{path}\" -ac 1 -ar 16000 \"{temp}\"", temp, "[CallDetails]", path).ConfigureAwait(false);
            return ok ? new RecordingPlaybackDto { FilePath = temp } : new RecordingPlaybackDto { Error = "Could not decode the recording." };
        }
    }
}
