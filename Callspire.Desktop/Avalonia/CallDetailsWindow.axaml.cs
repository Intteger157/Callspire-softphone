using System;
using System.Collections.Generic;
using System.IO;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;

namespace Softphone.Avalonia
{
    public partial class CallDetailsWindow : Window
    {
        // ── Model ─────────────────────────────────────────────────────────────
        private readonly string     _phoneNumber;
        private readonly DateTime   _callStartTime;
        private readonly DateTime?  _ringbackStartTime;
        private readonly DateTime?  _ringbackEndTime;
        private readonly DateTime?  _answerTime;
        private readonly bool       _wasAnswered;
        private readonly CallEndedBy _endedBy;
        private readonly TimeSpan?  _duration;
        private readonly string?    _recordingFilePath;
        private readonly CallTransport? _transport;
        private readonly string?    _outboundCallerId;
        private readonly string?    _connectionName;
        private readonly List<string>? _technicalDetails;

        // Callback invoked when user requests to call back
        public event Action<string>? CallBackRequested;

        // ── Constructor ───────────────────────────────────────────────────────
        public CallDetailsWindow(
            string          phoneNumber,
            DateTime        callStartTime,
            DateTime?       ringbackStartTime,
            DateTime?       ringbackEndTime,
            DateTime?       answerTime,
            bool            wasAnswered,
            CallEndedBy     endedBy,
            List<string>?   technicalDetails,
            TimeSpan?       duration,
            string?         recordingFilePath,
            CallTransport?  transport,
            string?         outboundCallerId,
            string?         connectionName)
        {
            InitializeComponent();
            AvaloniaWindowChromeHelper.Apply(this, preferAccent: false);

            _phoneNumber       = phoneNumber;
            _callStartTime     = callStartTime;
            _ringbackStartTime = ringbackStartTime;
            _ringbackEndTime   = ringbackEndTime;
            _answerTime        = answerTime;
            _wasAnswered       = wasAnswered;
            _endedBy           = endedBy;
            _technicalDetails  = technicalDetails;
            _duration          = duration;
            _recordingFilePath = recordingFilePath;
            _transport         = transport;
            _outboundCallerId  = outboundCallerId;
            _connectionName    = connectionName;

            PopulateCallInfo();
            PopulateRecording();
            PopulateTechnicalDetails();
        }

        // ── Data population ───────────────────────────────────────────────────
        private void PopulateCallInfo()
        {
            SetText("PhoneNumberTextBlock",    _phoneNumber);
            SetText("DirectionTextBlock",      _wasAnswered ? "Answered" : "Missed / Not Answered");
            SetText("StatusTextBlock",         _wasAnswered ? "Completed" : _endedBy.ToString());
            SetText("TransportTextBlock",      _transport?.ToString() ?? "SIP");
            SetText("ConnectionNameTextBlock", _connectionName ?? "—");
            SetText("CallerIdTextBlock",       _outboundCallerId ?? "—");

            SetText("CallStartedTextBlock",    _callStartTime.ToString("g"));
            SetText("RingbackStartTextBlock",  _ringbackStartTime?.ToString("T") ?? "—");
            SetText("AnswerTimeTextBlock",     _answerTime?.ToString("T") ?? "—");
            SetText("DurationTextBlock",       _duration.HasValue
                ? _duration.Value.ToString(_duration.Value.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss")
                : "—");

            if (_ringbackStartTime.HasValue && _answerTime.HasValue)
            {
                var ringTime = _answerTime.Value - _ringbackStartTime.Value;
                SetText("RingTimeTextBlock", ringTime.ToString(@"m\:ss"));
            }
            else
                SetText("RingTimeTextBlock", "—");
        }

        private void PopulateRecording()
        {
            if (string.IsNullOrEmpty(_recordingFilePath) || !File.Exists(_recordingFilePath))
                return;

            var section = this.FindControl<Border>("RecordingSection");
            if (section != null) section.IsVisible = true;

            SetText("RecordingPathTextBlock", _recordingFilePath);
        }

        private void PopulateTechnicalDetails()
        {
            if (_technicalDetails == null || _technicalDetails.Count == 0) return;

            var section = this.FindControl<Border>("TechnicalSection");
            if (section != null) section.IsVisible = true;

            SetText("TechnicalDetailsTextBlock", string.Join(Environment.NewLine, _technicalDetails));
        }

        // ── Playback handlers (stubbed — full implementation in Phase 6) ──────
        private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
        {
            AppLog.Log("[CallDetailsWindow] Play/Pause recording");
        }

        private void StopPlaybackButton_Click(object? sender, RoutedEventArgs e)
        {
            AppLog.Log("[CallDetailsWindow] Stop recording playback");
        }

        private void PlaybackSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            // TODO: seek audio playback
        }

        // ── Action bar ────────────────────────────────────────────────────────
        private void CallBackButton_Click(object? sender, RoutedEventArgs e)
        {
            CallBackRequested?.Invoke(_phoneNumber);
            Close();
        }

        // ── Title bar & chrome ────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object? sender, RoutedEventArgs e)
            => Close();

        // ── Helper ────────────────────────────────────────────────────────────
        private void SetText(string controlName, string value)
        {
            if (this.FindControl<TextBlock>(controlName) is TextBlock tb) tb.Text = value;
        }
    }
}
