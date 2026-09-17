// Cross-platform: pure helpers over Callspire.Core types (no WPF/Avalonia dependencies).
// Shared by the WPF CallWindow and the Avalonia CallWindow / DesktopAppController.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Softphone
{
    /// <summary>
    /// Вспомогательные методы для CallWindow - общая логика для SIP и WebRTC звонков
    /// </summary>
    public static class CallWindowHelpers
    {
        /// <summary>
        /// Вычисляет длительность звонка на основе времени начала и текущего времени
        /// Для SIP звонков запись начинается после 200 OK (когда устанавливается answerTime),
        /// поэтому используем answerTime для вычисления Duration, если он установлен
        /// Для WebRTC звонков запись начинается при makeCall_started, поэтому используем startTime
        /// </summary>
        public static TimeSpan? CalculateCallDuration(DateTime startTime, DateTime? answerTime, bool wasAnswered, bool isIncomingCall)
        {
            if (!wasAnswered && !answerTime.HasValue) return null;
            
            var effectiveStartTime = answerTime ?? startTime;
            var duration = DateTime.Now - effectiveStartTime;
            return duration.TotalSeconds >= 0 ? duration : null;
        }

        /// <summary>
        /// Форматирует строку деталей звонка для логирования
        /// </summary>
        public static string FormatCallDetailsString(
            string phoneNumber,
            DateTime callTime,
            DateTime? ringbackStart,
            DateTime? ringbackEnd,
            DateTime? answerTime,
            bool wasAnswered,
            TimeSpan? duration,
            CallEndedBy endedBy,
            int technicalDetailsCount)
        {
            return $"PhoneNumber={phoneNumber}, " +
                $"CallTime={callTime:HH:mm:ss.fff}, " +
                $"RingbackStart={ringbackStart?.ToString("HH:mm:ss.fff") ?? "null"}, " +
                $"RingbackEnd={ringbackEnd?.ToString("HH:mm:ss.fff") ?? "null"}, " +
                $"AnswerTime={answerTime?.ToString("HH:mm:ss.fff") ?? "null"}, " +
                $"WasAnswered={wasAnswered}, " +
                $"Duration={duration?.ToString(@"hh\:mm\:ss") ?? "N/A"}, " +
                $"EndedBy={endedBy}, " +
                $"TechnicalDetailsCount={technicalDetailsCount}";
        }

        /// <summary>
        /// Получает путь к файлу записи из различных источников
        /// </summary>
        public static string? GetRecordingFilePath(
            string? sipRecordingFilePath,
            string? sipServiceRecordingPath,
            string? webRtcRecorderPath,
            string? webRtcRecordingFilePath)
        {
            return sipRecordingFilePath 
                ?? sipServiceRecordingPath 
                ?? webRtcRecorderPath 
                ?? webRtcRecordingFilePath;
        }

        /// <summary>
        /// Обновляет время ответа и флаг wasAnswered
        /// </summary>
        public static void UpdateAnswerTime(ref DateTime? answerTime, ref bool wasAnswered)
        {
            if (!answerTime.HasValue)
            {
                answerTime = DateTime.Now;
                wasAnswered = true;
            }
        }

        /// <summary>
        /// Обновляет время окончания гудков
        /// </summary>
        public static void UpdateRingbackEndTime(DateTime? ringbackStartTime, ref DateTime? ringbackEndTime)
        {
            if (ringbackStartTime.HasValue && !ringbackEndTime.HasValue)
            {
                ringbackEndTime = DateTime.Now;
            }
        }

        /// <summary>Minimum plausible WAV size (16 KB — avoids empty/truncated uploads).</summary>
        public const long MinRecordingWavBytes = RecordingFileLimits.MinWavBytes;

        /// <summary>Sanity cap — corrupt/incomplete ffmpeg output must not be uploaded or played.</summary>
        public const long MaxRecordingWavBytes = RecordingFileLimits.MaxWavBytes;

        /// <summary>
        /// True when the recording file exists and its size is within expected bounds.
        /// </summary>
        public static bool IsRecordingWavPlausible(string? recordingFilePath, out long fileBytes) =>
            RecordingFileLimits.IsPlausibleWav(recordingFilePath, out fileBytes);

        /// <summary>
        /// Tracks consecutive observations of unchanged plausible file size (ffmpeg finished writing).
        /// </summary>
        public static bool ObserveRecordingWavStability(
            string? recordingFilePath,
            ref long lastObservedBytes,
            ref int unchangedObservations,
            int requiredUnchangedObservations = 2) =>
            RecordingFileLimits.ObserveStableWav(
                recordingFilePath,
                ref lastObservedBytes,
                ref unchangedObservations,
                requiredUnchangedObservations);

        /// <summary>
        /// Estimates WAV duration from file size (pcm_s16le, 48 kHz, mono — WebRtcCallRecorder output).
        /// </summary>
        public static int EstimateWavDurationSeconds(string? recordingFilePath)
        {
            if (!IsRecordingWavPlausible(recordingFilePath, out long fileLength))
                return 0;

            try
            {
                const double bytesPerSecond = 48000.0 * 2.0;
                int estimated = (int)Math.Round(fileLength / bytesPerSecond);
                return Math.Max(estimated, 0);
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsLocalClientRecordingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (IsPbxDownloadedRecordingPath(path)) return false;
            return path.Contains($"{Path.DirectorySeparatorChar}Callspire{Path.DirectorySeparatorChar}Recordings{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Callspire\Recordings\", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPbxDownloadedRecordingPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.Contains($"{Path.DirectorySeparatorChar}Recordings{Path.DirectorySeparatorChar}PBX{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                   || path.Contains(@"\Recordings\PBX\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>WAV or PBX webm/mp3 — usable for AmoCRM auto-upload.</summary>
        public static bool IsRecordingUsableForUpload(string? path)
        {
            if (IsRecordingWavPlausible(path, out _)) return true;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            try { return new FileInfo(path).Length > 0; } catch { return false; }
        }

        /// <summary>
        /// Rejects PBX downloads that are clearly truncated vs CDR/talk duration.
        /// Does not compare file size to local WAV — Miko records from answer (no ringback) and uses compressed webm/mp3.
        /// </summary>
        public static bool IsPbxRecordingAcceptableForUpload(
            CallHistoryItem? call,
            string? pbxPath,
            int? cdrDurationSeconds,
            out string? rejectReason)
        {
            rejectReason = null;

            if (!IsRecordingUsableForUpload(pbxPath) || call == null)
            {
                rejectReason = call == null ? "no call context" : "PBX file missing or empty";
                return false;
            }

            bool wasAnswered = call.WasAnswered || call.AnswerTime.HasValue;
            int callDurationSec = call.Duration.HasValue && call.Duration.Value.TotalSeconds > 0
                ? (int)Math.Round(call.Duration.Value.TotalSeconds)
                : 0;

            int compareDurationSec = callDurationSec;
            if (wasAnswered && call.AnswerTime.HasValue && callDurationSec > 0)
            {
                double preAnswerSec = (call.AnswerTime.Value - call.CallTime).TotalSeconds;
                if (preAnswerSec > 0 && preAnswerSec < callDurationSec)
                    compareDurationSec = Math.Max(1, callDurationSec - (int)Math.Round(preAnswerSec));
            }

            if (cdrDurationSeconds is > 0 && compareDurationSec >= 3)
            {
                int durDiff = Math.Abs(cdrDurationSeconds.Value - compareDurationSec);
                int tolerance = Math.Max(30, compareDurationSec / 2);
                if (durDiff > tolerance)
                {
                    rejectReason = $"CDR duration {cdrDurationSeconds}s vs talk ~{compareDurationSec}s (diff {durDiff}s > tolerance {tolerance}s)";
                    return false;
                }
            }

            if (!wasAnswered || compareDurationSec < 5)
                return true;

            try
            {
                long pbxBytes = new FileInfo(pbxPath!).Length;
                int durationForSize = cdrDurationSeconds is > 0 ? cdrDurationSeconds.Value : compareDurationSec;
                bool compressed = pbxPath!.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
                                  || pbxPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                                  || pbxPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
                long minBytesPerSec = compressed ? 400L : 4000L;
                long floorBytes = compressed ? 4000L : 50_000L;
                long minBytes = Math.Max(floorBytes, durationForSize * minBytesPerSec);
                if (pbxBytes < minBytes)
                {
                    rejectReason = $"PBX file too small ({pbxBytes} bytes < min {minBytes} for ~{durationForSize}s {(compressed ? "compressed" : "wav")})";
                    return false;
                }
            }
            catch
            {
                return true;
            }

            return true;
        }

        public static bool IsPbxRecordingAcceptableForUpload(
            CallHistoryItem? call,
            string? pbxPath,
            int? cdrDurationSeconds = null)
            => IsPbxRecordingAcceptableForUpload(call, pbxPath, cdrDurationSeconds, out _);

        public static AmoCrmUploadedRecordingSource DetectRecordingUploadSource(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return AmoCrmUploadedRecordingSource.None;
            if (IsPbxDownloadedRecordingPath(path))
                return AmoCrmUploadedRecordingSource.MikoPbx;
            if (IsLocalClientRecordingPath(path))
                return AmoCrmUploadedRecordingSource.Local;
            if (path.Contains("mikopbx-", StringComparison.OrdinalIgnoreCase))
                return AmoCrmUploadedRecordingSource.MikoPbx;
            if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return AmoCrmUploadedRecordingSource.Local;
            return AmoCrmUploadedRecordingSource.None;
        }

        public static string GetUploadedRecordingSourceLabel(AmoCrmUploadedRecordingSource source) => source switch
        {
            AmoCrmUploadedRecordingSource.MikoPbx => "PBX Server",
            AmoCrmUploadedRecordingSource.Local => "Local client recording (WAV)",
            _ => "Unknown"
        };

        /// <summary>ISO-8601 call end for PBX Gateway CDR matching (call start + total duration).</summary>
        public static string? FormatCallEndTimeIso(CallHistoryItem? call)
        {
            if (call?.Duration is not { } dur || dur.TotalSeconds <= 0)
                return null;
            return call.CallTime.Add(dur).ToUniversalTime().ToString("o");
        }

        /// <summary>ISO-8601 UTC call time for gateway CDR matching.</summary>
        public static string FormatCallTimeUtcIso(DateTime callTime) =>
            callTime.ToUniversalTime().ToString("o");

        /// <summary>Local client WAV (not PBX cache), from history or Recordings folder scan.</summary>
        public static string? ResolveLocalClientRecordingPath(CallHistoryItem? call)
        {
            if (call == null) return null;

            if (IsLocalClientRecordingPath(call.RecordingFilePath))
                return call.RecordingFilePath;

            try
            {
                string recordingsDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Callspire", "Recordings");
                if (!Directory.Exists(recordingsDir))
                    return null;

                string phoneToken = call.PhoneNumber.TrimStart('+').Replace(" ", "");
                return Directory.GetFiles(recordingsDir, $"*{phoneToken}*.wav", SearchOption.TopDirectoryOnly)
                    .Where(p => IsRecordingWavPlausible(p, out _))
                    .OrderBy(p => Math.Abs((new FileInfo(p).LastWriteTime - call.CallTime).TotalSeconds))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Infers answered call + duration from recording when signaling missed the B-leg answer (Originate/WebRTC).
        /// </summary>
        public static bool TryInferAnsweredFromRecording(
            string? recordingFilePath,
            CallEndedBy endedBy,
            TimeSpan? ringbackDuration,
            out int estimatedRecordingSeconds,
            bool remoteAudioDetected = false,
            bool isOriginateCall = false)
        {
            estimatedRecordingSeconds = EstimateWavDurationSeconds(recordingFilePath);
            if (estimatedRecordingSeconds <= 0)
                return false;

            if (isOriginateCall && remoteAudioDetected && estimatedRecordingSeconds >= 2)
                return true;

            // Originate: recording starts at ICE (includes ringback); ringbackEnd often unset until hangup.
            if (isOriginateCall && estimatedRecordingSeconds >= 5)
                return true;

            if (endedBy == CallEndedBy.RemoteParty && estimatedRecordingSeconds >= 5)
                return true;

            if (ringbackDuration.HasValue
                && estimatedRecordingSeconds > ringbackDuration.Value.TotalSeconds + 5)
                return true;

            return false;
        }

        /// <summary>
        /// Applies recording-based duration and wasAnswered before AmoCRM upload.
        /// </summary>
        public static void ApplyRecordingMetrics(
            ref int durationSeconds,
            ref bool wasAnswered,
            string? recordingFilePath,
            CallEndedBy endedBy = CallEndedBy.Unknown,
            TimeSpan? ringbackDuration = null,
            bool isOriginateCall = false)
        {
            int recSeconds = EstimateWavDurationSeconds(recordingFilePath);
            if (recSeconds <= 0)
                return;

            if (durationSeconds <= 0)
                durationSeconds = Math.Max(recSeconds, 1);

            if (!wasAnswered && TryInferAnsweredFromRecording(
                    recordingFilePath,
                    endedBy,
                    ringbackDuration,
                    out _,
                    isOriginateCall: isOriginateCall))
                wasAnswered = true;
        }
    }
}
