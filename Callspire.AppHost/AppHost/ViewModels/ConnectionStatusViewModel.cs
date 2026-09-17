namespace Softphone.AppHost.ViewModels
{
    /// <summary>Status of one telephony line as shown in the dialer status block.</summary>
    public sealed class ConnectionStatusViewModel : ObservableObject
    {
        private string _label = "PBX:";
        private string _text = "Not connected";
        private bool _isOnline;
        private bool _isError;
        private bool _isConfigured;
        private bool _isWebRtc;
        private string _displayName = "";

        public ConnectionSlot Slot { get; }

        public ConnectionStatusViewModel(ConnectionSlot slot)
        {
            Slot = slot;
            _label = slot == ConnectionSlot.Main ? "PBX:" : "SIP:";
        }

        /// <summary>Short label before the status dot ("PBX:", "SIP:", or a user-given name).</summary>
        public string Label { get => _label; set => Set(ref _label, value); }

        /// <summary>Human-friendly status text ("Registered", "Connecting…", "Auth failed").</summary>
        public string Text { get => _text; set => Set(ref _text, value); }

        public bool IsOnline { get => _isOnline; set { if (Set(ref _isOnline, value)) OnPropertyChanged(nameof(IsOffline)); } }

        public bool IsOffline => !IsOnline;

        public bool IsError { get => _isError; set => Set(ref _isError, value); }

        /// <summary>True when settings contain credentials for this line (row shown in UI).</summary>
        public bool IsConfigured { get => _isConfigured; set => Set(ref _isConfigured, value); }

        public bool IsWebRtc { get => _isWebRtc; set => Set(ref _isWebRtc, value); }

        /// <summary>User-configured connection name (falls back to transport label).</summary>
        public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }

        public void Update(string text, bool isOnline, bool isError = false)
        {
            Text = text;
            IsOnline = isOnline;
            IsError = isError;
        }
    }
}
