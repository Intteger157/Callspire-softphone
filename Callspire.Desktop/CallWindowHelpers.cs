#if WINDOWS
using System;
using System.Collections.Generic;
using System.IO;

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

        /// <summary>
        /// Estimates WAV duration from file size (pcm_s16le, 48 kHz, mono — WebRtcCallRecorder output).
        /// </summary>
        public static int EstimateWavDurationSeconds(string? recordingFilePath)
        {
            if (string.IsNullOrEmpty(recordingFilePath) || !File.Exists(recordingFilePath))
                return 0;

            try
            {
                long fileLength = new FileInfo(recordingFilePath).Length;
                const double bytesPerSecond = 48000.0 * 2.0;
                int estimated = (int)Math.Round(fileLength / bytesPerSecond);
                return Math.Max(estimated, 0);
            }
            catch
            {
                return 0;
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

#endif // WINDOWS
