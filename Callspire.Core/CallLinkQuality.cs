using System;
using System.Collections.Generic;

namespace Softphone
{
    /// <summary>How the media path to the PBX looks right now. Higher is better.</summary>
    public enum CallLinkQualityLevel
    {
        Unknown = 0,
        Poor = 1,
        Fair = 2,
        Good = 3,
        Excellent = 4,
    }

    /// <summary>
    /// One sample of the live media path to the PBX (loss, jitter, round-trip).
    /// Built by <see cref="SipService"/> and <see cref="WebRtcService"/> and shown on the active-call window.
    /// </summary>
    public sealed class CallLinkQuality
    {
        public CallLinkQualityLevel Level { get; }
        public string Label { get; }
        public string Detail { get; }
        public double? LossPercent { get; }
        public double? JitterMs { get; }
        public double? RttMs { get; }

        /// <summary>0 when still measuring, otherwise 1–4.</summary>
        public int Bars => Level == CallLinkQualityLevel.Unknown ? 0 : (int)Level;

        private CallLinkQuality(CallLinkQualityLevel level, string label, string detail, double? lossPercent, double? jitterMs, double? rttMs)
        {
            Level = level;
            Label = label;
            Detail = detail ?? "";
            LossPercent = lossPercent;
            JitterMs = jitterMs;
            RttMs = rttMs;
        }

        public static CallLinkQuality Measuring() =>
            new(CallLinkQualityLevel.Unknown, "Measuring", "", null, null, null);

        public static CallLinkQuality Broken(string detail) =>
            new(CallLinkQualityLevel.Poor, "Poor", detail ?? "", null, null, null);

        /// <summary>
        /// Worst of the metrics that are present wins.
        /// Loss %: &lt;1 excellent, &lt;3 good, &lt;8 fair. Jitter ms: &lt;20 / &lt;50 / &lt;100. RTT ms: &lt;150 / &lt;300 / &lt;500.
        /// </summary>
        public static CallLinkQuality Evaluate(double? lossPercent, double? jitterMs, double? rttMs)
        {
            lossPercent = Sanitize(lossPercent, 100);
            jitterMs = Sanitize(jitterMs, 2000);
            rttMs = Sanitize(rttMs, 5000);

            int loss = Rank(lossPercent, 1, 3, 8);
            int jitter = Rank(jitterMs, 20, 50, 100);
            int rtt = Rank(rttMs, 150, 300, 500);

            int worst = 0;
            if (loss > 0) worst = loss;
            if (jitter > 0 && (worst == 0 || jitter < worst)) worst = jitter;
            if (rtt > 0 && (worst == 0 || rtt < worst)) worst = rtt;

            var level = worst switch
            {
                4 => CallLinkQualityLevel.Excellent,
                3 => CallLinkQualityLevel.Good,
                2 => CallLinkQualityLevel.Fair,
                1 => CallLinkQualityLevel.Poor,
                _ => CallLinkQualityLevel.Unknown,
            };

            string label = level switch
            {
                CallLinkQualityLevel.Excellent => "Excellent",
                CallLinkQualityLevel.Good => "Good",
                CallLinkQualityLevel.Fair => "Fair",
                CallLinkQualityLevel.Poor => "Poor",
                _ => "Measuring",
            };

            return new CallLinkQuality(level, label, FormatMetrics(rttMs, jitterMs, lossPercent), lossPercent, jitterMs, rttMs);
        }

        public string ToLogLine(string source) =>
            $"[CallQuality] {source}: {Label} | {(string.IsNullOrEmpty(Detail) ? "measuring" : Detail)}";

        private static double? Sanitize(double? value, double max)
        {
            if (value is not double v || double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > max)
                return null;
            return v;
        }

        /// <summary>4 excellent … 1 poor, or 0 when the metric is absent.</summary>
        private static int Rank(double? value, double excellent, double good, double fair)
        {
            if (value is not double v) return 0;
            if (v < excellent) return 4;
            if (v < good) return 3;
            if (v < fair) return 2;
            return 1;
        }

        private static string FormatMetrics(double? rttMs, double? jitterMs, double? lossPercent)
        {
            var parts = new List<string>(3);
            if (rttMs is double rtt) parts.Add($"RTT {rtt:0} ms");
            if (jitterMs is double jitter) parts.Add($"jitter {jitter:0} ms");
            if (lossPercent is double loss) parts.Add($"loss {loss:0.#}%");
            return string.Join(" · ", parts);
        }
    }
}
