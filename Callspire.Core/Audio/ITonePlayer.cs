using System;

namespace Softphone.Audio
{
    /// <summary>
    /// Платформо-независимый плеер служебных тонов (гудки КПВ, тон «занято»).
    /// Desktop-реализация — ToneGenerator на NAudio; на других платформах
    /// подключается своя реализация через <see cref="TonePlayerFactory"/>.
    /// </summary>
    public interface ITonePlayer : IDisposable
    {
        /// <summary>Запускает воспроизведение гудков КПВ (ringback, 425 Гц 1с/4с).</summary>
        void PlayRingbackTone();

        /// <summary>Запускает воспроизведение тона «занято» (425 Гц 0.35с/0.35с).</summary>
        void PlayBusyTone();

        /// <summary>Останавливает любой проигрываемый тон.</summary>
        void Stop();
    }

    /// <summary>
    /// Точка подключения платформенной реализации <see cref="ITonePlayer"/>.
    /// Регистрируется на старте приложения (Desktop: App.xaml.cs).
    /// </summary>
    public static class TonePlayerFactory
    {
        /// <summary>Фабрика тон-плеера. null — тоны недоступны (звонки работают без локальных гудков).</summary>
        public static Func<ITonePlayer?>? Create { get; set; }
    }
}
