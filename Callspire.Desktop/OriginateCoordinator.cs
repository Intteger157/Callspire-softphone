#if WINDOWS
using System;

namespace Softphone
{
    /// <summary>
    /// Process-wide PBX Originate state. Shared across MainWindow instances so a second
    /// click-to-call launch cannot miss pending originate context.
    /// </summary>
    public static class OriginateCoordinator
    {
        private static readonly object Lock = new();

        private static string? _pendingId;
        private static string? _pendingCallerId;
        private static string? _pendingDestination;
        private static WeakReference<CallWindow>? _pendingCallWindow;
        private static string? _acceptedSessionId;

        public static bool IsPending
        {
            get
            {
                lock (Lock)
                {
                    return HasPendingCallWindowLocked()
                           || !string.IsNullOrEmpty(_pendingDestination)
                           || !string.IsNullOrEmpty(_pendingId);
                }
            }
        }

        public static string? PendingId
        {
            get { lock (Lock) { return _pendingId; } }
        }

        public static string? PendingCallerId
        {
            get { lock (Lock) { return _pendingCallerId; } }
        }

        public static string? PendingDestination
        {
            get { lock (Lock) { return _pendingDestination; } }
        }

        public static string? AcceptedSessionId
        {
            get { lock (Lock) { return _acceptedSessionId; } }
        }

        public static void BeginPending(string destination, string callerId, CallWindow callWindow)
        {
            lock (Lock)
            {
                _acceptedSessionId = null;
                _pendingId = null;
                _pendingDestination = destination;
                _pendingCallerId = callerId;
                _pendingCallWindow = new WeakReference<CallWindow>(callWindow);
            }
        }

        public static void SetPendingId(string? originateId)
        {
            lock (Lock) { _pendingId = originateId; }
        }

        public static CallWindow? GetPendingCallWindow()
        {
            lock (Lock)
            {
                if (_pendingCallWindow != null
                    && _pendingCallWindow.TryGetTarget(out var window)
                    && !window.IsClosing())
                {
                    return window;
                }

                return null;
            }
        }

        public static CallWindow? GetOutgoingCallWindow()
        {
            return GetPendingCallWindow() ?? CallHandlingHelpers.FindExistingOutgoingCallWindow();
        }

        public static void AcceptSession(string sessionId)
        {
            lock (Lock)
            {
                _acceptedSessionId = sessionId;
                ClearPendingLocked();
            }
        }

        public static void Clear(bool resetAcceptedSession = false)
        {
            lock (Lock)
            {
                ClearPendingLocked();
                if (resetAcceptedSession)
                    _acceptedSessionId = null;
            }
        }

        private static bool HasPendingCallWindowLocked()
        {
            return _pendingCallWindow != null
                   && _pendingCallWindow.TryGetTarget(out var window)
                   && !window.IsClosing();
        }

        private static void ClearPendingLocked()
        {
            _pendingId = null;
            _pendingCallerId = null;
            _pendingDestination = null;
            _pendingCallWindow = null;
        }
    }
}

#endif // WINDOWS
