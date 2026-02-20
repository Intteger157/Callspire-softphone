using System;

namespace Softphone
{
    /// <summary>
    /// Singleton сервис для ringback tone (гудки на исходящем звонке).
    /// Нужен, чтобы гарантированно останавливать гудок даже если CallWindow уже закрыто/не существует.
    /// </summary>
    public sealed class RingbackToneService
    {
        private static readonly Lazy<RingbackToneService> s_instance = new Lazy<RingbackToneService>(() => new RingbackToneService());
        public static RingbackToneService Instance => s_instance.Value;

        private readonly object _lock = new object();
        private ToneGenerator? _tone;

        private RingbackToneService() { }

        public void Play()
        {
            lock (_lock)
            {
                try
                {
                    _tone ??= new ToneGenerator();
                    _tone.PlayRingbackTone();
                }
                catch (Exception ex)
                {
                    try { MainWindow.Log($"[RingbackToneService] Error starting ringback: {ex.Message}"); } catch { }
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                try
                {
                    _tone?.Stop();
                }
                catch (Exception ex)
                {
                    try { MainWindow.Log($"[RingbackToneService] Error stopping ringback: {ex.Message}"); } catch { }
                }
            }
        }

        public void DisposeAndReset()
        {
            lock (_lock)
            {
                try
                {
                    _tone?.Dispose();
                }
                catch { }
                finally
                {
                    _tone = null;
                }
            }
        }
    }
}

