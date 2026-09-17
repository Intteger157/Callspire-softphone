using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Softphone.WebRtc
{
    /// <summary>
    /// Translates <see cref="IWebRtcEngineHost.SendAsync"/> command objects into the JavaScript that
    /// <c>WebRtcClient/phone.js</c> exposes as <c>window.SoftphoneWebRtc.*</c> / <c>window.Soft(slot).*</c>.
    /// UI-agnostic port of the switch in the Windows <c>WebRtcEngineHost</c>, so both hosts drive the
    /// same <c>phone.js</c> contract. Commands may be anonymous objects, <c>Dictionary&lt;string, object&gt;</c>
    /// or <see cref="JsonElement"/>.
    /// </summary>
    public static class WebRtcCommandScript
    {
        public sealed record Built(string Cmd, string Slot, string Script, bool IsNoisy, bool IsSensitive);

        /// <summary>Returns null when the command carries no script (e.g. <c>pong</c>) or is malformed.</summary>
        public static Built? Build(object command, Action<string>? log = null)
        {
            string? cmd = GetString(command, "cmd");
            string slot = GetString(command, "slot") ?? "main";
            if (string.IsNullOrEmpty(cmd))
            {
                log?.Invoke("[WebRtcCommandScript] command without 'cmd'");
                return null;
            }

            string? script = null;
            switch (cmd)
            {
                case "initUA":
                    var initJson = command is JsonElement je
                        ? je.GetRawText()
                        : JsonSerializer.Serialize(command, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    script = $"window.SoftphoneWebRtc.initUA({initJson});";
                    break;
                case "makeCall":
                    script = $"window.SoftphoneWebRtc.cleanupSessions(); window.SoftphoneWebRtc.makeCall('{Js(GetString(command, "number") ?? "")}');";
                    break;
                case "cleanupSessions":
                    script = "window.SoftphoneWebRtc.cleanupSessions();";
                    break;
                case "answer":
                    script = "window.SoftphoneWebRtc.answer();";
                    break;
                case "hangup":
                {
                    var sid = GetString(command, "sessionId");
                    script = string.IsNullOrEmpty(sid) ? "window.SoftphoneWebRtc.hangup();" : $"window.SoftphoneWebRtc.hangup('{Js(sid)}');";
                    break;
                }
                case "setHold":
                {
                    var hold = GetBool(command, "hold");
                    if (hold == null) { log?.Invoke("[WebRtcCommandScript] setHold without 'hold'"); return null; }
                    var sid = GetString(command, "sessionId");
                    var h = hold.Value ? "true" : "false";
                    script = string.IsNullOrEmpty(sid) ? $"window.SoftphoneWebRtc.setHold({h});" : $"window.SoftphoneWebRtc.setHold({h}, '{Js(sid)}');";
                    break;
                }
                case "resetEngine":
                    script = "window.SoftphoneWebRtc.resetEngine();";
                    break;
                case "ping":
                    script = "window.SoftphoneWebRtc.ping();";
                    break;
                case "getStats":
                    script = "window.SoftphoneWebRtc.getStats();";
                    break;
                case "checkCallActivity":
                {
                    var sid = GetString(command, "sessionId");
                    script = string.IsNullOrEmpty(sid) ? "window.SoftphoneWebRtc.checkCallActivity();" : $"window.SoftphoneWebRtc.checkCallActivity('{Js(sid)}');";
                    break;
                }
                case "executeScript":
                    script = GetString(command, "script");
                    if (string.IsNullOrEmpty(script)) { log?.Invoke("[WebRtcCommandScript] executeScript without 'script'"); return null; }
                    break;
                case "setMute":
                {
                    var mute = GetBool(command, "mute");
                    if (mute == null) { log?.Invoke("[WebRtcCommandScript] setMute without 'mute'"); return null; }
                    script = $"window.SoftphoneWebRtc.setMute({(mute.Value ? "true" : "false")});";
                    break;
                }
                case "sendDtmf":
                {
                    var digit = GetString(command, "digit");
                    if (string.IsNullOrEmpty(digit)) { log?.Invoke("[WebRtcCommandScript] sendDtmf without 'digit'"); return null; }
                    script = $"window.SoftphoneWebRtc.sendDtmf('{Js(digit)}');";
                    break;
                }
                case "enumerateAudioDevices":
                    script = "window.SoftphoneWebRtc.enumerateAudioDevices();";
                    break;
                case "switchAudioDevice":
                {
                    var input = GetString(command, "inputDeviceId");
                    var output = GetString(command, "outputDeviceId");
                    script = $"window.SoftphoneWebRtc.switchAudioDevice({(input != null ? $"'{Js(input)}'" : "null")}, {(output != null ? $"'{Js(output)}'" : "null")});";
                    break;
                }
                case "startRecording":
                    script = "window.SoftphoneWebRtc.startRecording();";
                    break;
                case "stopRecording":
                    script = "window.SoftphoneWebRtc.stopRecording();";
                    break;
                default:
                    if (cmd != "pong") log?.Invoke($"[WebRtcCommandScript] no script for command '{cmd}'");
                    return null;
            }

            // Route to the right iframe slot: "main" keeps the legacy window.SoftphoneWebRtc getter,
            // any other slot is rewritten to the explicit router window.Soft('<slot>').
            if (!string.Equals(slot, "main", StringComparison.OrdinalIgnoreCase))
                script = script.Replace("window.SoftphoneWebRtc.", $"window.Soft('{Js(slot)}').");

            bool noisy = cmd == "ping" || cmd == "getStats";
            bool sensitive = cmd == "initUA";
            return new Built(cmd, slot, script, noisy, sensitive);
        }

        /// <summary>Escape for a single-quoted JS string literal.</summary>
        public static string Js(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");

        private static string? GetString(object command, string name)
        {
            var v = GetValue(command, name);
            return v switch
            {
                null => null,
                JsonElement el => el.ValueKind == JsonValueKind.String ? el.GetString() : el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : el.ToString(),
                _ => v.ToString(),
            };
        }

        private static bool? GetBool(object command, string name)
        {
            var v = GetValue(command, name);
            try
            {
                return v switch
                {
                    null => null,
                    bool b => b,
                    JsonElement el => el.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.String => bool.TryParse(el.GetString(), out var pb) ? pb : null,
                        _ => null,
                    },
                    _ => Convert.ToBoolean(v),
                };
            }
            catch { return null; }
        }

        private static object? GetValue(object command, string name)
        {
            switch (command)
            {
                case Dictionary<string, object> dict:
                    return dict.TryGetValue(name, out var dv) ? dv : null;
                case IReadOnlyDictionary<string, object?> rdict:
                    return rdict.TryGetValue(name, out var rv) ? rv : null;
                case JsonElement el:
                    return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var prop) ? prop : null;
                default:
                    var p = command.GetType().GetProperty(name);
                    return p?.GetValue(command);
            }
        }
    }
}
