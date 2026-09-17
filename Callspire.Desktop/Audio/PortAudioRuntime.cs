using System;
using System.Collections.Generic;
using PortAudioSharp;

namespace Softphone.Audio
{
    /// <summary>One PortAudio device as shown in Settings.</summary>
    public sealed record PortAudioDeviceInfo(int Index, string Name, int InputChannels, int OutputChannels, bool IsDefaultInput, bool IsDefaultOutput, double DefaultSampleRate)
    {
        public bool IsInput => InputChannels > 0;
        public bool IsOutput => OutputChannels > 0;
        public override string ToString() => Name;
    }

    /// <summary>
    /// Reference-counted PortAudio initialisation + device enumeration shared by
    /// <see cref="PortAudioSink"/>, <see cref="PortAudioAudioSource"/> and <see cref="PortAudioTonePlayer"/>.
    /// PortAudio requires every <c>Pa_Initialize</c> to be paired with <c>Pa_Terminate</c>; the last
    /// terminate closes all open streams, so we count users instead of letting each component terminate.
    /// </summary>
    public static class PortAudioRuntime
    {
        private static readonly object _gate = new();
        private static int _refCount;
        private static bool _loadAttempted;
        private static string? _loadError;

        public static bool IsAvailable
        {
            get
            {
                lock (_gate)
                {
                    if (!_loadAttempted) TryProbe();
                    return _loadError == null;
                }
            }
        }

        public static string? LoadError { get { lock (_gate) { if (!_loadAttempted) TryProbe(); return _loadError; } } }

        private static void TryProbe()
        {
            _loadAttempted = true;
            try
            {
                PortAudio.LoadNativeLibrary();
                _ = PortAudio.Version;
                _loadError = null;
            }
            catch (Exception ex)
            {
                _loadError = ex.Message;
                AppLog.Log($"[PortAudio] native library unavailable: {ex.Message}");
            }
        }

        /// <summary>Acquire a runtime reference (calls Pa_Initialize on first use). Dispose the handle to release.</summary>
        public static IDisposable Acquire()
        {
            lock (_gate)
            {
                if (!_loadAttempted) TryProbe();
                if (_loadError != null) throw new InvalidOperationException($"PortAudio is not available: {_loadError}");
                if (_refCount == 0)
                {
                    PortAudio.Initialize();
                    AppLog.Log($"[PortAudio] initialised (version 0x{PortAudio.Version:X})");
                }
                _refCount++;
            }
            return new Handle();
        }

        private static void Release()
        {
            lock (_gate)
            {
                if (_refCount == 0) return;
                _refCount--;
                if (_refCount == 0)
                {
                    try { PortAudio.Terminate(); } catch (Exception ex) { AppLog.Log($"[PortAudio] terminate: {ex.Message}"); }
                }
            }
        }

        private sealed class Handle : IDisposable
        {
            private bool _disposed;
            public void Dispose() { if (_disposed) return; _disposed = true; Release(); }
        }

        public static int DefaultInputDevice { get { using var _ = Acquire(); return PortAudio.DefaultInputDevice; } }
        public static int DefaultOutputDevice { get { using var _ = Acquire(); return PortAudio.DefaultOutputDevice; } }

        /// <summary>Enumerate devices. Returns an empty list if PortAudio cannot be loaded.</summary>
        public static IReadOnlyList<PortAudioDeviceInfo> EnumerateDevices()
        {
            var list = new List<PortAudioDeviceInfo>();
            try
            {
                using var _ = Acquire();
                int count = PortAudio.DeviceCount;
                int defIn = PortAudio.DefaultInputDevice;
                int defOut = PortAudio.DefaultOutputDevice;
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var info = PortAudio.GetDeviceInfo(i);
                        list.Add(new PortAudioDeviceInfo(i, info.name ?? $"Device {i}", info.maxInputChannels, info.maxOutputChannels, i == defIn, i == defOut, info.defaultSampleRate));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Log($"[PortAudio] GetDeviceInfo({i}) failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PortAudio] EnumerateDevices failed: {ex.Message}");
            }
            return list;
        }

        public static IReadOnlyList<PortAudioDeviceInfo> InputDevices() => EnumerateDevices().FindAll(d => d.IsInput);
        public static IReadOnlyList<PortAudioDeviceInfo> OutputDevices() => EnumerateDevices().FindAll(d => d.IsOutput);

        private static List<PortAudioDeviceInfo> FindAll(this IReadOnlyList<PortAudioDeviceInfo> src, Predicate<PortAudioDeviceInfo> p)
        {
            var r = new List<PortAudioDeviceInfo>();
            foreach (var d in src) if (p(d)) r.Add(d);
            return r;
        }

        /// <summary>Build StreamParameters for the given device (or the default one when index &lt; 0).</summary>
        internal static StreamParameters? MakeParams(int deviceIndex, bool input, int channels, out int resolvedDevice)
        {
            resolvedDevice = deviceIndex >= 0 ? deviceIndex : (input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice);
            if (resolvedDevice < 0) return null;

            var info = PortAudio.GetDeviceInfo(resolvedDevice);
            int max = input ? info.maxInputChannels : info.maxOutputChannels;
            if (max <= 0)
            {
                // Selected device does not support this direction — fall back to default.
                resolvedDevice = input ? PortAudio.DefaultInputDevice : PortAudio.DefaultOutputDevice;
                if (resolvedDevice < 0) return null;
                info = PortAudio.GetDeviceInfo(resolvedDevice);
                max = input ? info.maxInputChannels : info.maxOutputChannels;
                if (max <= 0) return null;
            }

            return new StreamParameters
            {
                device = resolvedDevice,
                channelCount = Math.Min(channels, max),
                sampleFormat = SampleFormat.Int16,
                suggestedLatency = input ? info.defaultLowInputLatency : info.defaultLowOutputLatency,
                hostApiSpecificStreamInfo = IntPtr.Zero,
            };
        }
    }
}
