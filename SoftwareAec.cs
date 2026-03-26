using System;

namespace Softphone
{
    /// <summary>
    /// Two-stage software AEC with NLP (Non-Linear Processing).
    ///
    /// Stage 1 — Delay estimation:
    ///   Accumulate 2 s of far-end and near-end audio, then compute
    ///   cross-correlation to find the acoustic round-trip delay
    ///   (DAC → speaker → air → mic → ADC).
    ///
    /// Stage 2 — Delay-compensated short NLMS + NLP:
    ///   A short adaptive filter (128 ms / 1024 taps) models the echo
    ///   path after compensating for the bulk delay.  A quarter of the
    ///   filter extends before the estimated delay to tolerate estimation
    ///   error.  The NLP post-filter detects residual echo by comparing
    ///   error energy with far-end energy and applies soft gain reduction.
    ///
    /// Operates at 8 kHz (G.711 telephony codec rate).
    /// </summary>
    public sealed class SoftwareAec
    {
        private const float EPS = 1e-8f;
        private const int SAMPLE_RATE = 8000;

        // ── Delay estimation ──
        private const int DELAY_EST_DURATION_MS = 2000;
        private const int DELAY_SEARCH_MAX_MS = 600;
        private readonly int _delayEstLen;          // samples to accumulate
        private readonly int _delaySearchMax;       // max lag to search
        private float[]? _delayRefBuf;
        private float[]? _delayCapBuf;
        private int _delayRefPos;
        private int _delayCapPos;
        private int _estimatedDelay = -1;

        // ── NLMS adaptive filter ──
        private const int FILTER_LEN_MS = 128;
        private readonly int _filterLen;            // 1024 taps at 8 kHz
        private readonly int _filterMargin;         // taps before estimated delay
        private readonly float[] _w;
        private float _mu = 1.0f;

        // Far-end ring buffer (power-of-2 for fast masking)
        private readonly float[] _xBuf;
        private readonly int _xMask;
        private int _xWritePos;

        // ── NLP ──
        private float _farPwrSmooth;
        private float _errPwrSmooth;
        private float _corrSmooth;
        private const float SM = 0.92f;
        private const float NLP_ON = 0.20f;         // correlation above which NLP engages
        private const float NLP_FLOOR = 0.05f;      // minimum gain
        private const float NLP_MAX = 0.90f;         // max attenuation

        // ── Double-talk ──
        private float _nearPwrSmooth;
        private const float DTD_RATIO = 8.0f;

        // ── Diagnostics ──
        private long _totalProcessed;
        private bool _loggedConvergence;
        private long _loggedConvergence2Count;
        private readonly object _lock = new();

        public SoftwareAec()
        {
            _delayEstLen = SAMPLE_RATE * DELAY_EST_DURATION_MS / 1000;
            _delaySearchMax = SAMPLE_RATE * DELAY_SEARCH_MAX_MS / 1000;
            _delayRefBuf = new float[_delayEstLen];
            _delayCapBuf = new float[_delayEstLen];

            _filterLen = SAMPLE_RATE * FILTER_LEN_MS / 1000;     // 1024
            _filterMargin = _filterLen / 4;                       // 256 taps before delay
            _w = new float[_filterLen];

            int minBuf = (_delaySearchMax + _filterLen) * 4;
            int bufLen = 1;
            while (bufLen < minBuf) bufLen <<= 1;
            _xBuf = new float[bufLen];
            _xMask = bufLen - 1;
        }

        /// <summary>
        /// Feed far-end (reference) audio going to the speaker.
        /// </summary>
        public void FeedFarEnd(short[] samples, int offset, int count)
        {
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    float s = samples[offset + i] * (1f / 32768f);
                    _xBuf[_xWritePos & _xMask] = s;
                    _xWritePos++;
                    _farPwrSmooth = SM * _farPwrSmooth + (1f - SM) * s * s;

                    if (_delayRefBuf != null && _delayRefPos < _delayEstLen)
                        _delayRefBuf[_delayRefPos++] = s;
                }
            }
        }

        /// <summary>
        /// Process captured near-end audio — removes echo in-place (8 kHz).
        /// </summary>
        public void CancelEcho(short[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                // ── Phase 1: accumulate for delay estimation ──
                if (_estimatedDelay < 0)
                {
                    if (_delayCapBuf != null)
                    {
                        for (int i = 0; i < count && _delayCapPos < _delayEstLen; i++)
                            _delayCapBuf[_delayCapPos++] = buffer[offset + i] * (1f / 32768f);

                        if (_delayCapPos >= _delayEstLen && _delayRefPos >= _delayEstLen)
                            EstimateDelay();
                    }
                    return;
                }

                // ── Phase 2: NLMS + NLP ──
                // The filter window is centred so that tap _filterMargin aligns
                // with the estimated delay.  This gives _filterMargin taps of
                // headroom for early echo components and (_filterLen - _filterMargin)
                // taps for late reflections.
                //
                //  refBase = _xWritePos - _estimatedDelay + _filterMargin
                //
                //  k = 0           → age = estDelay - margin     (slightly before echo)
                //  k = margin      → age = estDelay              (primary echo)
                //  k = filterLen-1 → age = estDelay + 3/4*filter (late reflections)

                int refBase = _xWritePos - _estimatedDelay + _filterMargin;
                if (refBase < _filterLen)
                    return;

                for (int n = 0; n < count; n++)
                {
                    float mic = buffer[offset + n] * (1f / 32768f);

                    int rPos = refBase + n;
                    float y = 0f;
                    float xPow = 0f;

                    for (int k = 0; k < _filterLen; k++)
                    {
                        float xk = _xBuf[(rPos - k) & _xMask];
                        y += _w[k] * xk;
                        xPow += xk * xk;
                    }

                    float error = mic - y;

                    _nearPwrSmooth = SM * _nearPwrSmooth + (1f - SM) * mic * mic;

                    bool dtd = _farPwrSmooth > EPS &&
                               (_nearPwrSmooth / (_farPwrSmooth + EPS)) > DTD_RATIO;

                    if (!dtd && xPow > EPS)
                    {
                        float norm = _mu / (xPow + EPS);
                        for (int k = 0; k < _filterLen; k++)
                        {
                            float xk = _xBuf[(rPos - k) & _xMask];
                            _w[k] += norm * error * xk;
                        }
                    }

                    // ── NLP ──
                    float farSample = _xBuf[(_xWritePos - _estimatedDelay + n) & _xMask];
                    float instCorr = Math.Abs(error * farSample);
                    _corrSmooth = SM * _corrSmooth + (1f - SM) * instCorr;
                    _errPwrSmooth = SM * _errPwrSmooth + (1f - SM) * error * error;

                    float nlpGain = 1f;
                    if (_farPwrSmooth > 1e-6f)
                    {
                        float denom = MathF.Sqrt(_errPwrSmooth * _farPwrSmooth) + EPS;
                        float normCorr = _corrSmooth / denom;
                        if (normCorr > NLP_ON)
                        {
                            float sup = (normCorr - NLP_ON) / (1f - NLP_ON);
                            sup = Math.Clamp(sup, 0f, NLP_MAX);
                            nlpGain = Math.Max(1f - sup, NLP_FLOOR);
                        }
                    }

                    buffer[offset + n] = (short)(Math.Clamp(error * nlpGain, -1f, 1f) * 32767f);
                    _totalProcessed++;
                }

                // Log at 5 s and then every 30 s
                long sec = _totalProcessed / SAMPLE_RATE;
                if (!_loggedConvergence && sec >= 5)
                {
                    _loggedConvergence = true;
                    _loggedConvergence2Count = 0;
                    LogFilterState("5s");
                }
                else if (_loggedConvergence && sec >= 10 + _loggedConvergence2Count * 30)
                {
                    _loggedConvergence2Count++;
                    LogFilterState($"{sec}s");
                }
            }
        }

        private void LogFilterState(string label)
        {
            float maxW = 0f; int peakK = 0;
            for (int k = 0; k < _filterLen; k++)
            {
                float a = Math.Abs(_w[k]);
                if (a > maxW) { maxW = a; peakK = k; }
            }
            float peakAge = (_estimatedDelay - _filterMargin + peakK) * 1000f / SAMPLE_RATE;
            MainWindow.Log($"[SoftwareAec] [{label}] peak tap={peakK}/{_filterLen} (echo age ~{peakAge:F1}ms), " +
                           $"|w_max|={maxW:F4}, delay={_estimatedDelay} ({_estimatedDelay * 1000f / SAMPLE_RATE:F1}ms), " +
                           $"farPwr={_farPwrSmooth:E2}, errPwr={_errPwrSmooth:E2}, corr={_corrSmooth:E2}, " +
                           $"margin={_filterMargin}");
        }

        private void EstimateDelay()
        {
            var refBuf = _delayRefBuf!;
            var capBuf = _delayCapBuf!;
            int len = _delayEstLen;
            int maxLag = Math.Min(_delaySearchMax, len / 2);

            float refEnergy = 0f;
            for (int i = 0; i < len; i++)
                refEnergy += refBuf[i] * refBuf[i];

            if (refEnergy < 1e-4f)
            {
                _estimatedDelay = SAMPLE_RATE * 150 / 1000;
                MainWindow.Log($"[SoftwareAec] Low reference energy ({refEnergy:E2}), using default delay " +
                               $"{_estimatedDelay} samples ({_estimatedDelay * 1000f / SAMPLE_RATE:F0}ms)");
                FreeDelayBuffers();
                return;
            }

            float bestCorr = float.MinValue;
            int bestLag = 0;

            for (int lag = 0; lag < maxLag; lag++)
            {
                float corr = 0f;
                float capEnergy = 0f;
                int n = len - lag;
                for (int i = 0; i < n; i++)
                {
                    corr += refBuf[i] * capBuf[i + lag];
                    capEnergy += capBuf[i + lag] * capBuf[i + lag];
                }
                float denom = MathF.Sqrt(refEnergy * capEnergy) + EPS;
                float normCorr = corr / denom;

                if (normCorr > bestCorr)
                {
                    bestCorr = normCorr;
                    bestLag = lag;
                }
            }

            // If the peak is at the boundary, the actual delay might be longer.
            // Warn but use it — the filter margin will help.
            bool atBoundary = bestLag >= maxLag - 50;
            _estimatedDelay = bestLag;

            MainWindow.Log($"[SoftwareAec] Delay estimated: {bestLag} samples ({bestLag * 1000f / SAMPLE_RATE:F1}ms), " +
                           $"correlation={bestCorr:F4}, search range=0-{maxLag} ({maxLag * 1000f / SAMPLE_RATE:F0}ms)" +
                           (atBoundary ? " ⚠️ AT BOUNDARY — real delay may be longer" : ""));
            FreeDelayBuffers();
        }

        private void FreeDelayBuffers()
        {
            _delayRefBuf = null;
            _delayCapBuf = null;
        }

        public void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_w);
                Array.Clear(_xBuf);
                _xWritePos = 0;
                _estimatedDelay = -1;
                _delayRefBuf = new float[_delayEstLen];
                _delayCapBuf = new float[_delayEstLen];
                _delayRefPos = 0;
                _delayCapPos = 0;
                _farPwrSmooth = 0;
                _errPwrSmooth = 0;
                _corrSmooth = 0;
                _nearPwrSmooth = 0;
                _totalProcessed = 0;
                _loggedConvergence = false;
                _loggedConvergence2Count = 0;
            }
        }
    }
}
