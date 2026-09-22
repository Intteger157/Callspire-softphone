using System;

namespace Softphone.Audio
{
    /// <summary>
    /// Platform-neutral acoustic echo cancellation. Implementations:
    /// <see cref="NativeEchoCanceller"/> (webrtc_apm.dll, Windows only) and
    /// <see cref="SoftwareEchoCanceller"/> (pure C#, all platforms).
    /// </summary>
    public interface IEchoCanceller : IDisposable
    {
        string Description { get; }

        /// <summary>Feed far-end (speaker) reference samples.</summary>
        void ProcessRender(short[] frame, int numSamples);

        /// <summary>Process near-end (microphone) samples in-place, removing estimated echo.</summary>
        void ProcessCapture(short[] frame, int numSamples);
    }

    /// <summary>Wraps the native webrtc_apm.dll audio processing module (Windows, AVX2 CPUs).</summary>
    public sealed class NativeEchoCanceller : IEchoCanceller
    {
        private IntPtr _handle;

        private NativeEchoCanceller(IntPtr handle) => _handle = handle;

        public string Description => "Native AEC (webrtc_apm.dll)";

        /// <summary>
        /// Returns null when the native AEC cannot be used: non-Windows OS, missing DLL,
        /// or CPU without AVX2. Never throws.
        /// </summary>
        public static NativeEchoCanceller? TryCreate(int sampleRateHz, int channels, int frameMs)
        {
            if (!OperatingSystem.IsWindows()) return null;
            if (!NativeAec.IsNativeAecRuntimeSupported) return null;

            var handle = NativeAec.Create(sampleRateHz, channels, frameMs);
            return handle == IntPtr.Zero ? null : new NativeEchoCanceller(handle);
        }

        public void ProcessRender(short[] frame, int numSamples) => NativeAec.ProcessRender(_handle, frame, numSamples);

        public void ProcessCapture(short[] frame, int numSamples) => NativeAec.ProcessCapture(_handle, frame, numSamples);

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                NativeAec.Destroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    /// <summary>Pure managed AEC (see <see cref="SoftwareAec"/>); used where the native DLL is unavailable.</summary>
    public sealed class SoftwareEchoCanceller : IEchoCanceller
    {
        private readonly SoftwareAec _aec = new SoftwareAec();

        public string Description => "Software AEC (managed)";

        public void ProcessRender(short[] frame, int numSamples) => _aec.FeedFarEnd(frame, 0, numSamples);

        public void ProcessCapture(short[] frame, int numSamples) => _aec.CancelEcho(frame, 0, numSamples);

        public void Dispose()
        {
            // No unmanaged resources; reset so a pooled instance can't leak state.
            try { _aec.Reset(); } catch { }
        }
    }

    /// <summary>
    /// Selects the AEC implementation per platform:
    /// Windows → native webrtc_apm.dll (or none, preserving historical behaviour when the DLL
    /// is absent); macOS → SoftwareAec, never touching the native DLL.
    /// </summary>
    public static class EchoCancellerFactory
    {
        public static IEchoCanceller? Create(int sampleRateHz, int channels, int frameMs)
        {
            if (OperatingSystem.IsWindows())
            {
                var native = NativeEchoCanceller.TryCreate(sampleRateHz, channels, frameMs);
                if (native != null)
                {
                    AppLog.Log($"[EchoCancellerFactory] Using {native.Description}");
                    return native;
                }

                // Historical Windows behaviour: no native DLL → AEC off (WASAPI Communications
                // mode still provides driver-level AEC on most systems).
                AppLog.Log("[EchoCancellerFactory] Native AEC unavailable on Windows — AEC disabled (driver-level AEC may still apply)");
                return null;
            }

            var soft = new SoftwareEchoCanceller();
            AppLog.Log($"[EchoCancellerFactory] Using {soft.Description} (non-Windows platform)");
            return soft;
        }
    }
}
