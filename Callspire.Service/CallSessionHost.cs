using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;
using Softphone.Service.Contracts;
using Softphone.Service.Ipc;

namespace Softphone.Service
{
    /// <summary>
    /// Sidecar-side replacement for the call window: owns the <see cref="CallViewModel"/> of the
    /// (single) active call, pushes <c>callStateChanged</c> to Swift on every property change and
    /// routes call verbs coming back over IPC. Mirrors <c>Avalonia/CallWindow.axaml.cs</c> lifecycle:
    /// StartAsync on open, <see cref="CallViewModel.OnViewClosed"/> when the user closes the window,
    /// plain Dispose when the view model itself requested the close.
    /// </summary>
    public sealed class CallSessionHost
    {
        private readonly IpcServer _ipc;
        private readonly ServiceDispatcher _dispatcher;
        private readonly DesktopAppController _controller;
        private readonly object _gate = new();
        private Session? _active;

        public event Action? ActiveCallChanged;

        public CallSessionHost(IpcServer ipc, ServiceDispatcher dispatcher, DesktopAppController controller)
        {
            _ipc = ipc;
            _dispatcher = dispatcher;
            _controller = controller;
        }

        public bool HasActiveCall { get { lock (_gate) return _active != null; } }
        public bool IsWebRtcCallActive { get { lock (_gate) return _active?.ViewModel.IsWebRtc == true; } }

        /// <summary>Current session as <c>showCallWindow</c> payload (used when the Swift client reconnects).</summary>
        public CallWindowDto? Describe()
        {
            lock (_gate) return _active == null ? null : BuildWindowDto(_active);
        }

        /// <summary>Called by <see cref="RemoteDesktopShell.ShowCallWindow"/> (any thread).</summary>
        public void Open(CallWindowRequest request)
        {
            _dispatcher.Post(() =>
            {
                Session? existing;
                lock (_gate) existing = _active;
                if (existing != null)
                {
                    // Second call while one is open: WPF/Avalonia just re-activate the window.
                    _ = _ipc.SendEventAsync("bringCallWindowToFront", new { sessionId = existing.Id });
                    return;
                }

                var vm = new CallViewModel(_controller, request);
                var session = new Session(Guid.NewGuid().ToString("N"), vm);
                lock (_gate) _active = session;

                vm.PropertyChanged += session.OnVmPropertyChanged;
                session.StateChanged = () => _ = _ipc.SendEventAsync("callStateChanged", CallStateDto.From(session.Id, vm));
                vm.Completed += report => _controller.OnCallCompleted(report);
                vm.CloseRequested += () => CloseSession(session, requestedByVm: true);

                _ = _ipc.SendEventAsync("showCallWindow", BuildWindowDto(session));
                ActiveCallChanged?.Invoke();

                _ = Task.Run(async () =>
                {
                    try { await vm.StartAsync().ConfigureAwait(false); }
                    catch (Exception ex) { AppLog.Log($"[CallSession] StartAsync: {ex.Message}"); }
                });
            });
        }

        private CallWindowDto BuildWindowDto(Session s) => new()
        {
            SessionId = s.Id,
            PhoneNumber = s.ViewModel.PhoneNumber,
            IsIncoming = s.ViewModel.IsIncoming,
            IsWebRtc = s.ViewModel.IsWebRtc,
            Slot = s.Request.Slot == ConnectionSlot.Main ? "main" : "secondary",
            TransportLabel = s.ViewModel.TransportLabel,
            ConnectionLabel = s.ViewModel.ConnectionLabel,
            WindowTitle = s.ViewModel.WindowTitle,
            CallStartTime = s.ViewModel.CallStartTime,
            IsOriginateCall = s.Request.IsOriginateCall,
            State = CallStateDto.From(s.Id, s.ViewModel),
        };

        private void CloseSession(Session session, bool requestedByVm)
        {
            _dispatcher.Post(() =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_active, session)) return;
                    _active = null;
                }
                session.ViewModel.PropertyChanged -= session.OnVmPropertyChanged;
                session.StateChanged = null;
                try
                {
                    if (requestedByVm) session.ViewModel.Dispose();
                    else session.ViewModel.OnViewClosed();
                }
                catch (Exception ex) { AppLog.Log($"[CallSession] close: {ex.Message}"); }
                _ = _ipc.SendEventAsync("callClosed", new { sessionId = session.Id });
                ActiveCallChanged?.Invoke();
            });
        }

        // ───────────────────────── IPC verbs ─────────────────────────

        public void RegisterHandlers()
        {
            _ipc.Register("answer", p => Verb(p, vm => vm.AnswerAsync()));
            _ipc.Register("reject", p => Verb(p, vm => vm.RejectAsync()));
            _ipc.Register("hangup", p => Verb(p, vm => vm.HangupAsync()));
            _ipc.Register("toggleMute", p => Verb(p, vm => vm.ToggleMuteAsync()));
            _ipc.Register("toggleHold", p => Verb(p, vm => vm.ToggleHoldAsync()));
            _ipc.Register("toggleKeypad", p => Verb(p, vm => { vm.ToggleKeypad(); return Task.CompletedTask; }));
            _ipc.Register("sendDtmf", p =>
            {
                var args = p.HasValue ? IpcJson.Deserialize<CallSessionParams>(p.Value) : null;
                var digit = args?.Digit;
                if (string.IsNullOrEmpty(digit)) throw new IpcException("digit is required", "bad_request");
                return Verb(p, vm => vm.SendDtmfAsync(digit[0]));
            });
            _ipc.Register("callViewClosed", (p, _) =>
            {
                var s = Resolve(p, throwIfMissing: false);
                if (s != null) CloseSession(s, requestedByVm: false);
                return Task.FromResult<object?>(null);
            });
            _ipc.Register("getActiveCall", _ => Describe());
        }

        private Task<object?> Verb(System.Text.Json.JsonElement? p, Func<CallViewModel, Task> action)
        {
            var s = Resolve(p, throwIfMissing: true)!;
            return _dispatcher.RunAsync<object?>(async () =>
            {
                await action(s.ViewModel).ConfigureAwait(false);
                return null;
            });
        }

        private Session? Resolve(System.Text.Json.JsonElement? p, bool throwIfMissing)
        {
            var args = p.HasValue ? IpcJson.Deserialize<CallSessionParams>(p.Value) : null;
            Session? s;
            lock (_gate) s = _active;
            if (s == null || (!string.IsNullOrEmpty(args?.SessionId) && s.Id != args!.SessionId))
            {
                if (throwIfMissing) throw new IpcException("No such call session", "no_session");
                return null;
            }
            return s;
        }

        private sealed class Session
        {
            public string Id { get; }
            public CallViewModel ViewModel { get; }
            public CallWindowRequest Request { get; }
            public Action? StateChanged;
            private int _pending;

            public Session(string id, CallViewModel vm)
            {
                Id = id;
                ViewModel = vm;
                Request = vm.Request;
            }

            /// <summary>Coalesces bursts of property changes into one push (timer ticks, status + tone, …).</summary>
            public void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
            {
                if (Interlocked.Exchange(ref _pending, 1) == 1) return;
                _ = Task.Delay(15).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref _pending, 0);
                    try { StateChanged?.Invoke(); } catch (Exception ex) { AppLog.Log($"[CallSession] push: {ex.Message}"); }
                }, TaskScheduler.Default);
            }
        }
    }
}
