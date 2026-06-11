using System;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Платформо-независимый контракт хоста WebRTC-движка.
    /// На Windows реализуется поверх WebView2 (Callspire.Desktop/WebRtcEngineHost.cs),
    /// на других платформах может быть реализован поверх нативного WebRTC-стека
    /// или другого web-host'а. WebRtcService общается с движком только через этот интерфейс.
    /// </summary>
    public interface IWebRtcEngineHost
    {
        /// <summary>Сырые JSON-события от JS-движка (sip.js bundle).</summary>
        event Action<string>? EngineEvent;

        /// <summary>true, когда движок инициализирован и готов принимать команды.</summary>
        bool IsInitialized { get; }

        /// <summary>Отправляет команду движку (объект сериализуется в JSON).</summary>
        Task SendAsync(object command);
    }
}
