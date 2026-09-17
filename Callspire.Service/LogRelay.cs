using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// Ring buffer behind <see cref="AppLog.UiSink"/> (port of the Avalonia <c>LogBuffer</c>) plus a
    /// batched <c>logLines</c> push so the Swift LogView can open late and still show history.
    /// </summary>
    public sealed class LogRelay
    {
        private const int MaxLines = 4000;
        private readonly LinkedList<string> _lines = new();
        private readonly List<string> _outbox = new();
        private readonly object _gate = new();
        private readonly IpcServer _ipc;
        private int _flushScheduled;
        private volatile bool _streaming;

        public LogRelay(IpcServer ipc)
        {
            _ipc = ipc;
            var previous = AppLog.UiSink;
            AppLog.UiSink = line =>
            {
                try { previous?.Invoke(line); } catch { }
                Add(line);
            };
        }

        private void Add(string line)
        {
            string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            lock (_gate)
            {
                _lines.AddLast(stamped);
                while (_lines.Count > MaxLines) _lines.RemoveFirst();
                if (_streaming) _outbox.Add(stamped);
            }
            if (_streaming && Interlocked.Exchange(ref _flushScheduled, 1) == 0)
                _ = Task.Delay(250).ContinueWith(_ => Flush(), TaskScheduler.Default);
        }

        private void Flush()
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            string[] batch;
            lock (_gate)
            {
                if (_outbox.Count == 0) return;
                batch = _outbox.ToArray();
                _outbox.Clear();
            }
            _ = _ipc.SendEventAsync("logLines", new { lines = batch });
        }

        public void RegisterHandlers()
        {
            // Swift calls getLogSnapshot when the log window opens (streaming starts) and setLogStreaming(false) on close.
            _ipc.Register("getLogSnapshot", _ =>
            {
                lock (_gate) { _streaming = true; _outbox.Clear(); return new { lines = new List<string>(_lines) }; }
            });
            _ipc.Register("setLogStreaming", p =>
            {
                bool on = p.HasValue && p.Value.TryGetProperty("enabled", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True;
                lock (_gate) { _streaming = on; if (!on) _outbox.Clear(); }
                return null;
            });
            _ipc.Register("clearLog", _ => { lock (_gate) { _lines.Clear(); _outbox.Clear(); } return null; });
            _ipc.Register("log", p =>
            {
                var text = p.HasValue && p.Value.TryGetProperty("text", out var t) ? t.GetString() : null;
                if (!string.IsNullOrEmpty(text)) AppLog.Log($"[Swift] {text}");
                return null;
            });
        }
    }
}
