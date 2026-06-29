#if WINDOWS
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
        /// Returns the first open CallWindow opened for an outbound call (including Originate before Show()).
        /// </summary>
        public static CallWindow? FindExistingOutgoingCallWindow()
        {
            return Application.Current.Windows.OfType<CallWindow>()
                .FirstOrDefault(w => w.OpenedAsOutgoingCall && !w.IsClosing());
        }

        /// <summary>
        /// Проверяет, активен ли звонок на указанном WebRTC-слоте. Если service не указан —
        /// возвращает true при активности любого слота (используется для блокировки SIP-входящих).
        /// </summary>
        public static bool IsWebRtcCallActive(WebRtcService? service = null)
        {
            try
            {
                bool IsActive(WebRtcService? s)
                {
                    var st = s?.CurrentCallState ?? WebRtcCallState.Idle;
                    return st == WebRtcCallState.Ringing ||
                           st == WebRtcCallState.Connected ||
                           st == WebRtcCallState.Calling;
                }

                if (service != null) return IsActive(service);
                return IsActive(WebRtcService.Main) || IsActive(WebRtcService.Secondary);
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
            
            if (isIncomingCall || WindowForegroundHelper.IsElevatedForegroundActive)
            {
                callWindow.Topmost = true;
            }
            
            callWindow.UpdateLayout();
        }
    }
}

#endif // WINDOWS
