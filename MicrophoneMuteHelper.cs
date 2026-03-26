using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Softphone
{
    /// <summary>
    /// Вспомогательный класс для управления микрофоном на уровне Windows
    /// </summary>
    public static class MicrophoneMuteHelper
    {
        /// <summary>
        /// Отключает или включает микрофон по умолчанию на уровне Windows.
        /// Управляет устройством для роли Communications (используется для звонков),
        /// что обеспечивает отображение перечеркнутого микрофона в трее Windows.
        /// </summary>
        public static bool SetMicrophoneMute(bool mute)
        {
            bool success = false;
            
            try
            {
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    try
                    {
                        // ВАЖНО:
                        // Мьют для Role.Communications на некоторых ноутбуках/драйверах (Realtek)
                        // может "затронуть" и рендер (динамики), из-за чего вы пропадаете в трубке.
                        // Поэтому для mute используем только Role.Multimedia (обычный capture device).
                        var multimediaDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                        if (multimediaDevice?.AudioEndpointVolume != null)
                        {
                            multimediaDevice.AudioEndpointVolume.Mute = mute;
                            success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error muting microphone (Communications role): {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error muting microphone: {ex.Message}");
            }
            
            return success;
        }

        /// <summary>
        /// Некоторые драйверы/микшеры Windows при mute микрофона для Role.Communications
        /// могут неожиданно влиять и на рендер (динамики) для этого же device.
        /// Чтобы при mute микрофона не пропадал звук собеседника, гарантируем
        /// что Render-endpoint для Communications/Multimedia не будет примьютен.
        /// </summary>
        public static void EnsureRenderUnmuted()
        {
            try
            {
                using var deviceEnumerator = new MMDeviceEnumerator();

                void unmuteEndpoint(DataFlow flow, Role role)
                {
                    try
                    {
                        var device = deviceEnumerator.GetDefaultAudioEndpoint(flow, role);
                        if (device?.AudioEndpointVolume != null)
                            device.AudioEndpointVolume.Mute = false;
                    }
                    catch { }
                }

                // Some systems primarily use Role.Console for speakers.
                unmuteEndpoint(DataFlow.Render, Role.Console);
                unmuteEndpoint(DataFlow.Render, Role.Communications);
                unmuteEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch { }
        }
        
        /// <summary>
        /// Получает текущее состояние mute микрофона по умолчанию
        /// </summary>
        public static bool IsMicrophoneMuted()
        {
            try
            {
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    // Проверяем устройство для коммуникаций (обычно используется для звонков)
                    var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    
                    if (defaultDevice != null)
                    {
                        var audioEndpointVolume = defaultDevice.AudioEndpointVolume;
                        if (audioEndpointVolume != null)
                        {
                            return audioEndpointVolume.Mute;
                        }
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            
            return false;
        }

        /// <summary>
        /// Получает текущее состояние mute для render-endpoint'а по умолчанию
        /// </summary>
        public static bool IsRenderMuted(Role role)
        {
            try
            {
                using var deviceEnumerator = new MMDeviceEnumerator();
                var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
                return defaultDevice?.AudioEndpointVolume?.Mute ?? false;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Устанавливает уровень громкости микрофона (0.0 - 1.0)
        /// </summary>
        public static bool SetMicrophoneVolume(float volume)
        {
            bool success = false;
            
            try
            {
                // Ограничиваем значение от 0.0 до 1.0
                volume = Math.Max(0.0f, Math.Min(1.0f, volume));
                
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    // Используем Role.Communications для VoIP приложений
                    try
                    {
                        var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                        
                        if (defaultDevice != null)
                        {
                            var audioEndpointVolume = defaultDevice.AudioEndpointVolume;
                            if (audioEndpointVolume != null)
                            {
                                audioEndpointVolume.MasterVolumeLevelScalar = volume;
                                success = true;
                                
                                // Также устанавливаем для Multimedia роли для совместимости
                                try
                                {
                                    var multimediaDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                                    if (multimediaDevice != null && multimediaDevice.AudioEndpointVolume != null)
                                    {
                                        multimediaDevice.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                                    }
                                }
                                catch
                                {
                                    // Игнорируем ошибки для Multimedia роли
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error setting microphone volume (Communications role): {ex.Message}");
                        
                        // Fallback: пробуем для Multimedia роли
                        try
                        {
                            var multimediaDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                            if (multimediaDevice != null && multimediaDevice.AudioEndpointVolume != null)
                            {
                                multimediaDevice.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
                                success = true;
                            }
                        }
                        catch
                        {
                            // Игнорируем ошибки
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting microphone volume: {ex.Message}");
            }
            
            return success;
        }
        
        /// <summary>
        /// Получает текущий уровень громкости микрофона (0.0 - 1.0)
        /// </summary>
        public static float GetMicrophoneVolume()
        {
            try
            {
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    
                    if (defaultDevice != null)
                    {
                        var audioEndpointVolume = defaultDevice.AudioEndpointVolume;
                        if (audioEndpointVolume != null)
                        {
                            return audioEndpointVolume.MasterVolumeLevelScalar;
                        }
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            
            return 0.5f; // Возвращаем значение по умолчанию
        }
        
        /// <summary>
        /// Включает усиление микрофона (Microphone Boost) для улучшения слышимости
        /// </summary>
        public static bool EnableMicrophoneBoost()
        {
            bool success = false;
            
            try
            {
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    
                    if (defaultDevice != null)
                    {
                        // Пробуем получить доступ к усилению через AudioEndpointVolume
                        var audioEndpointVolume = defaultDevice.AudioEndpointVolume;
                        if (audioEndpointVolume != null)
                        {
                            // Устанавливаем максимальную громкость
                            audioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                            success = true;
                            
                            // Также устанавливаем для Multimedia роли
                            try
                            {
                                var multimediaDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                                if (multimediaDevice != null && multimediaDevice.AudioEndpointVolume != null)
                                {
                                    multimediaDevice.AudioEndpointVolume.MasterVolumeLevelScalar = 1.0f;
                                }
                            }
                            catch
                            {
                                // Игнорируем ошибки для Multimedia роли
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error enabling microphone boost: {ex.Message}");
            }
            
            return success;
        }
        
        /// <summary>
        /// Оптимизирует настройки микрофона для максимальной слышимости
        /// </summary>
        public static bool OptimizeMicrophoneForVoIP()
        {
            bool success = false;
            
            try
            {
                // 1. Устанавливаем максимальную громкость
                success = SetMicrophoneVolume(1.0f);
                
                // 2. Включаем усиление
                EnableMicrophoneBoost();
                
                // 3. Убеждаемся, что микрофон не заглушен
                SetMicrophoneMute(false);
                
                return success;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error optimizing microphone: {ex.Message}");
                return false;
            }
        }
    }
}

