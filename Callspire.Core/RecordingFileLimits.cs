using System;
using System.IO;

namespace Softphone
{
    /// <summary>Shared validation for local call recording WAV files.</summary>
    public static class RecordingFileLimits
    {
        public const long MinWavBytes = 16 * 1024;
        public const long MaxWavBytes = 100 * 1024 * 1024;

        public static bool IsPlausibleWav(string? path, out long fileBytes)
        {
            fileBytes = 0;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return false;

            try
            {
                fileBytes = new FileInfo(path).Length;
                if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    return fileBytes >= MinWavBytes && fileBytes <= MaxWavBytes;
                return fileBytes > 0 && fileBytes <= MaxWavBytes;
            }
            catch
            {
                return false;
            }
        }

        public static bool ObserveStableWav(
            string? path,
            ref long lastObservedBytes,
            ref int unchangedObservations,
            int requiredUnchangedObservations = 2)
        {
            if (!IsPlausibleWav(path, out long fileBytes))
            {
                lastObservedBytes = fileBytes;
                unchangedObservations = 0;
                return false;
            }

            if (fileBytes == lastObservedBytes)
                unchangedObservations++;
            else
            {
                lastObservedBytes = fileBytes;
                unchangedObservations = 1;
            }

            return unchangedObservations >= requiredUnchangedObservations;
        }
    }
}
