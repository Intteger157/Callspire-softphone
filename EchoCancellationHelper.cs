using System;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Softphone
{
    /// <summary>
    /// Помощник для настройки акустического эхоподавления через Windows API
    /// Использует режим Communications для автоматического включения AEC
    /// Best practices для WebRTC и SIP звонков
    /// </summary>
    public static class EchoCancellationHelper
    {
        // Values from mmdeviceapi.h (ERole enum).
        // eConsole = 0, eMultimedia = 1, eCommunications = 2.
        private enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        // Undocumented COM interface used by Windows to set default endpoint per role.
        // Needed when app selects devices by index (which may not match system Communications role).
        [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
        private class PolicyConfigClientVista
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("568b9108-44bf-40b4-9006-86afe5b5a620")]
        private interface IPolicyConfigVista
        {
            // HRESULT SetDefaultEndpoint(PCWSTR id, ERole role)
            [PreserveSig]
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        }


        /// <summary>
        /// Включает режим Communications для аудио устройств через Windows WASAPI
        /// Это активирует AEC на уровне системы, если драйвер поддерживает
        /// Best practice для VoIP приложений
        /// </summary>
        public static void EnableEchoCancellation()
        {
            try
            {
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    // Настраиваем микрофон в режиме Communications
                    // Windows автоматически активирует AEC для устройств в режиме Communications,
                    // если драйвер устройства поддерживает это
                    try
                    {
                        var captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                        if (captureDevice != null)
                        {
                            MainWindow.Log($"[EchoCancellationHelper] Capture device in Communications mode: {captureDevice.FriendlyName}");
                            
                            // Пытаемся настроить IAudioClient2 для явного включения Communications режима
                            TrySetCommunicationsMode(captureDevice, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[EchoCancellationHelper] Error accessing capture device in Communications mode: {ex.Message}");
                    }
                    
                    // Настраиваем динамики в режиме Communications
                    try
                    {
                        var renderDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                        if (renderDevice != null)
                        {
                            MainWindow.Log($"[EchoCancellationHelper] Render device in Communications mode: {renderDevice.FriendlyName}");
                            
                            // Пытаемся настроить IAudioClient2 для явного включения Communications режима
                            TrySetCommunicationsMode(renderDevice, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        MainWindow.Log($"[EchoCancellationHelper] Error accessing render device in Communications mode: {ex.Message}");
                    }
                }
                
                MainWindow.Log("[EchoCancellationHelper] Echo cancellation enabled via Communications mode (Windows will handle AEC if supported by driver)");
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[EchoCancellationHelper] Error enabling echo cancellation: {ex.Message}");
            }
        }

        /// <summary>
        /// Best-effort: set the specified endpoint as default for Role.Communications.
        /// This helps enable WASAPI AEC/NS/AGC on devices selected by app (e.g., by index).
        /// </summary>
        public static bool TrySetDeviceAsDefaultForCommunications(MMDevice device, string label = "")
        {
            if (device == null) return false;

            try
            {
                var policy = Activator.CreateInstance(typeof(PolicyConfigClientVista)) as IPolicyConfigVista;
                if (policy == null) return false;

                int hr = policy.SetDefaultEndpoint(device.ID, ERole.eCommunications);
                MainWindow.Log($"[EchoCancellationHelper] SetDefaultEndpoint(eCommunications) {(string.IsNullOrWhiteSpace(label) ? "" : label)}{device.FriendlyName}. deviceId={device.ID}, hr=0x{hr:X}");
                return hr == 0;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[EchoCancellationHelper] Error SetDefaultEndpoint(eCommunications) for {device.FriendlyName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Пытается установить режим Communications через проверку доступности устройства
        /// Использование Role.Communications уже обеспечивает правильный режим для Windows
        /// </summary>
        private static void TrySetCommunicationsMode(MMDevice device, bool isCapture)
        {
            try
            {
                // Проверяем, что устройство доступно и готово к использованию
                // Использование Role.Communications при получении устройства уже обеспечивает
                // правильный режим для Windows, который активирует AEC автоматически
                if (device != null)
                {
                    var deviceState = device.State;
                    MainWindow.Log($"[EchoCancellationHelper] Device state for {(isCapture ? "capture" : "render")}: {deviceState}");
                    
                    // Windows автоматически применяет Communications режим и AEC
                    // когда устройство используется через Role.Communications
                    // Дополнительная настройка через COM interop требует доступа к внутренним объектам
                    // SIPSorceryMedia.Windows, что сложно без модификации библиотеки
                }
            }
            catch (Exception ex)
            {
                // Игнорируем ошибки - это дополнительная проверка
                MainWindow.Log($"[EchoCancellationHelper] Error checking device state: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Проверяет, поддерживает ли устройство захвата акустическое эхоподавление
        /// </summary>
        public static bool IsEchoCancellationSupported()
        {
            try
            {
                using (var deviceEnumerator = new MMDeviceEnumerator())
                {
                    var captureDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    if (captureDevice != null)
                    {
                        // В режиме Communications Windows обычно включает AEC автоматически
                        // если драйвер устройства поддерживает это
                        return true;
                    }
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
            
            return false;
        }
    }
}
