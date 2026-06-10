using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Softphone
{
    /// <summary>
    /// P/Invoke wrapper for native AEC library (webrtc_apm.dll).
    /// The DLL must expose a flat C API:
    ///   void* apm_create(int sample_rate, int channels, int frame_ms);
    ///   void  apm_destroy(void* handle);
    ///   int   apm_process_render(void* handle, short* data, int num_samples);
    ///   int   apm_process_capture(void* handle, short* data, int num_samples);
    ///
    /// If the DLL is not found, Create() returns IntPtr.Zero and all other
    /// methods become no-ops, allowing the app to run without AEC.
    /// </summary>
    internal static class NativeAec
    {
        private const string DLL_NAME = "webrtc_apm.dll";
        private static bool _available = true;
        private static bool _loggedCpuUnsupported;

        /// <summary>
        /// Native webrtc_apm.dll is built with MSVC /arch:AVX2 (see native-aec/CMakeLists.txt). Loading or
        /// running it on CPUs without AVX2 causes STATUS_ILLEGAL_INSTRUCTION (0xC000001D). Do not P/Invoke
        /// until this returns true.
        /// </summary>
        public static bool IsNativeAecRuntimeSupported => Avx2.IsSupported;

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "apm_create")]
        private static extern IntPtr apm_create(int sampleRate, int channels, int frameMs);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "apm_destroy")]
        private static extern void apm_destroy(IntPtr handle);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "apm_process_render")]
        private static extern int apm_process_render(IntPtr handle, short[] data, int numSamples);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "apm_process_capture")]
        private static extern int apm_process_capture(IntPtr handle, short[] data, int numSamples);

        public static IntPtr Create(int sampleRate, int channels, int frameMs)
        {
            if (!_available) return IntPtr.Zero;

            if (!Avx2.IsSupported)
            {
                if (!_loggedCpuUnsupported)
                {
                    _loggedCpuUnsupported = true;
                    MainWindow.Log("[NativeAec] webrtc_apm.dll is built with AVX2; this CPU has no AVX2 — skipping native AEC (no illegal-instruction crash). SIP calls work with echo cancellation off.");
                }
                return IntPtr.Zero;
            }

            try
            {
                return apm_create(sampleRate, channels, frameMs);
            }
            catch (DllNotFoundException)
            {
                _available = false;
                return IntPtr.Zero;
            }
        }

        public static void Destroy(IntPtr handle)
        {
            if (!_available || handle == IntPtr.Zero) return;
            try { apm_destroy(handle); } catch { }
        }

        /// <summary>
        /// Feed far-end (speaker) audio to the AEC as reference — the same samples
        /// WasapiOut reads for playback (see WasapiAudioEndPoint.AecTapWaveProvider).
        /// </summary>
        public static void ProcessRender(IntPtr handle, short[] data, int numSamples)
        {
            if (!_available || handle == IntPtr.Zero) return;
            apm_process_render(handle, data, numSamples);
        }

        /// <summary>
        /// Process near-end (microphone) audio through the AEC.
        /// Modifies the buffer in-place, removing the estimated echo.
        /// Called from the capture path BEFORE encoding.
        /// </summary>
        public static void ProcessCapture(IntPtr handle, short[] data, int numSamples)
        {
            if (!_available || handle == IntPtr.Zero) return;
            apm_process_capture(handle, data, numSamples);
        }
    }
}
