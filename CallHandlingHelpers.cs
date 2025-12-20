using System;
using System.Linq;
using System.Windows;

namespace Softphone
{
    /// <summary>
    /// Вспомогательные методы для обработки звонков в MainWindow
    /// </summary>
    public static class CallHandlingHelpers
    {
        /// <summary>
        /// Проверяет, есть ли уже открытое окно звонка
        /// </summary>
        public static CallWindow? FindExistingCallWindow()
        {
            return Application.Current.Windows.OfType<CallWindow>().FirstOrDefault();
        }

        /// <summary>
        /// Проверяет, активен ли WebRTC звонок (для блокировки SIP входящих)
        /// </summary>
        public static bool IsWebRtcCallActive()
        {
            try
            {
                var webRtcState = WebRtcService.Instance?.CurrentCallState ?? WebRtcCallState.Idle;
                return webRtcState == WebRtcCallState.Ringing || 
                       webRtcState == WebRtcCallState.Connected || 
                       webRtcState == WebRtcCallState.Calling;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Настраивает окно звонка для немедленного отображения
        /// </summary>
        public static void PrepareCallWindowForDisplay(CallWindow callWindow, bool isIncomingCall)
        {
            callWindow.ShowActivated = true;
            callWindow.ShowInTaskbar = true;
            callWindow.WindowState = WindowState.Normal;
            
            if (isIncomingCall)
            {
                callWindow.Topmost = true; // Делаем окно поверх всех для входящих звонков
            }
            
            callWindow.UpdateLayout();
        }
    }
}
