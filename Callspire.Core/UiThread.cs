using System;

namespace Softphone
{
    /// <summary>
    /// Platform-agnostic UI thread marshalling (replaces direct WPF Dispatcher usage in
    /// business services so they can live in Callspire.Core). The platform head registers
    /// the hooks at startup (WPF: Dispatcher.BeginInvoke; Avalonia: Dispatcher.UIThread.Post).
    /// When no hook is registered, actions run inline on the calling thread.
    /// </summary>
    public static class UiThread
    {
        /// <summary>Posts an action onto the UI thread with normal priority.</summary>
        public static Action<Action>? Post;

        /// <summary>Posts an action with the highest priority (e.g. incoming-call UI).</summary>
        public static Action<Action>? PostUrgent;

        public static void BeginInvoke(Action action)
        {
            var post = Post;
            if (post != null)
            {
                try { post(action); return; } catch { }
            }
            // Never run UI-bound callbacks inline from SIP/WebRTC worker threads.
        }

        public static void BeginInvokeUrgent(Action action)
        {
            var post = PostUrgent ?? Post;
            if (post != null)
            {
                try { post(action); return; } catch { }
            }
        }
    }
}
