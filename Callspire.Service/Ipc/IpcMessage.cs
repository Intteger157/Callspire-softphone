using System.Text.Json;
using System.Text.Json.Serialization;

namespace Softphone.Service.Ipc
{
    /// <summary>
    /// Wire envelope. One JSON object per line (NDJSON) over the Unix domain socket, symmetric in both
    /// directions:
    /// <list type="bullet">
    ///   <item><c>{"type":"request","id":"…","method":"…","params":{…}}</c> — caller expects a response with the same <c>id</c>.</item>
    ///   <item><c>{"type":"response","id":"…","result":{…}}</c> or <c>{"type":"response","id":"…","error":{"message":"…"}}</c>.</item>
    ///   <item><c>{"type":"event","event":"…","data":{…}}</c> — fire-and-forget notification.</item>
    /// </list>
    /// Swift → C# requests are commands (placeCall, saveSettings, …). C# → Swift requests are modal
    /// prompts (showConnectionSelection, showLeadSelection) and WebRTC bridge calls (webRtcInvokeScript);
    /// the <c>id</c> is the correlation id the Swift side answers with.
    /// </summary>
    public sealed class IpcMessage
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
        [JsonPropertyName("params")] public JsonElement? Params { get; set; }
        [JsonPropertyName("result")] public JsonElement? Result { get; set; }
        [JsonPropertyName("error")] public IpcError? Error { get; set; }
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public JsonElement? Data { get; set; }

        public const string TypeRequest = "request";
        public const string TypeResponse = "response";
        public const string TypeEvent = "event";

        public static IpcMessage Request(string id, string method, object? @params) => new()
        {
            Type = TypeRequest, Id = id, Method = method,
            Params = @params == null ? null : IpcJson.ToElement(@params),
        };

        public static IpcMessage Response(string id, object? result) => new()
        {
            Type = TypeResponse, Id = id,
            Result = result == null ? null : IpcJson.ToElement(result),
        };

        public static IpcMessage ErrorResponse(string id, string message, string? code = null) => new()
        {
            Type = TypeResponse, Id = id, Error = new IpcError { Message = message, Code = code },
        };

        public static IpcMessage EventMessage(string name, object? data) => new()
        {
            Type = TypeEvent, Event = name,
            Data = data == null ? null : IpcJson.ToElement(data),
        };
    }

    public sealed class IpcError
    {
        [JsonPropertyName("message")] public string Message { get; set; } = "";
        [JsonPropertyName("code")] public string? Code { get; set; }
    }

    public sealed class IpcException : System.Exception
    {
        public string? Code { get; }
        public IpcException(string message, string? code = null) : base(message) { Code = code; }
    }
}
