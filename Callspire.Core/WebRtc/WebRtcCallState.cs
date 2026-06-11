namespace Softphone
{
    /// <summary>
    /// Состояние WebRTC-звонка (используется WebRtcService и UI).
    /// </summary>
    public enum WebRtcCallState
    {
        Idle,           // Нет активного звонка
        Ringing,        // Входящий звонок (звонят нам)
        Calling,        // Исходящий звонок (мы звоним)
        Connected,      // Звонок установлен
        Ending,         // Звонок завершается
        Ended           // Звонок завершен
    }
}
