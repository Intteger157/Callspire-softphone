using System;
using System.Collections.Generic;

namespace Softphone
{
    /// <summary>
    /// Вспомогательные методы для CallWindow - общая логика для SIP и WebRTC звонков
    /// </summary>
    public static class CallWindowHelpers
    {
        /// <summary>
        /// Вычисляет длительность звонка на основе времени начала и текущего времени
        /// </summary>
        public static TimeSpan? CalculateCallDuration(DateTime startTime, DateTime? answerTime, bool wasAnswered, bool isIncomingCall)
        {
            if (!wasAnswered) return null;
            
            var effectiveStartTime = isIncomingCall ? (answerTime ?? startTime) : startTime;
            return DateTime.Now - effectiveStartTime;
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
    }
}
