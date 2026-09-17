using System;
using System.Collections.Generic;
using System.IO;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Threading;
using System.Threading.Tasks;
using Softphone.Audio;

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
            PlatformWindowChrome.Apply(this, preferAccent: false);

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
            _ = LoadRecordingAsync(_recordingFilePath);
        }

        // ── Kommo ─────────────────────────────────────────────────────────────
        private Func<Task<(bool Ok, string? Error)>>? _kommoRetry;
        private string? _kommoLeadUrl;

        /// <summary>
        /// Shows the Kommo block: current upload status, optional "Open lead" link and a retry action
        /// (gateway or local mode — the controller decides). Call once after construction.
        /// </summary>
        public void ConfigureKommo(AmoCrmUploadStatus status, string? reason, long? leadId, string? subdomain,
            Func<Task<(bool Ok, string? Error)>>? retryAsync)
        {
            _kommoRetry = retryAsync;
            _kommoLeadUrl = leadId.HasValue && !string.IsNullOrWhiteSpace(subdomain)
                ? $"https://{subdomain}.kommo.com/leads/detail/{leadId.Value}" : null;

            if (this.FindControl<Border>("KommoSection") is { } section) section.IsVisible = true;
            if (this.FindControl<Button>("KommoOpenLeadButton") is { } open) open.IsVisible = _kommoLeadUrl != null;
            if (this.FindControl<Button>("KommoRetryButton") is { } retry) retry.IsVisible = retryAsync != null;
            SetKommoStatus(status, reason, leadId);
        }

        private void SetKommoStatus(AmoCrmUploadStatus status, string? reason, long? leadId)
        {
            string text = status switch
            {
                AmoCrmUploadStatus.Uploaded => leadId.HasValue ? $"Uploaded to lead #{leadId}" : "Uploaded",
                AmoCrmUploadStatus.Failed => "Upload failed",
                AmoCrmUploadStatus.Cancelled => "Cancelled by user",
                _ => "Not uploaded",
            };
            SetText("KommoStatusTextBlock", text);
            SetText("KommoReasonTextBlock", reason ?? "");
        }

        private void KommoOpenLead_Click(object? sender, RoutedEventArgs e)
        {
            if (_kommoLeadUrl == null) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_kommoLeadUrl) { UseShellExecute = true }); }
            catch (Exception ex) { AppLog.Log($"[CallDetailsWindow] open lead failed: {ex.Message}"); }
        }

        private async void KommoRetry_Click(object? sender, RoutedEventArgs e)
        {
            if (_kommoRetry == null) return;
            var button = this.FindControl<Button>("KommoRetryButton");
            if (button != null) { button.IsEnabled = false; button.Content = "Uploading…"; }
            SetText("KommoStatusTextBlock", "Uploading…");
            SetText("KommoReasonTextBlock", "");
            try
            {
                var (ok, error) = await _kommoRetry();
                SetKommoStatus(ok ? AmoCrmUploadStatus.Uploaded : AmoCrmUploadStatus.Failed, error, null);
            }
            catch (Exception ex)
            {
                SetKommoStatus(AmoCrmUploadStatus.Failed, ex.Message, null);
            }
            finally
            {
                if (button != null) { button.IsEnabled = true; button.Content = "Retry upload"; }
            }
        }

        // ── Recording playback ────────────────────────────────────────────────
        private RecordingPlayer? _player;
        private bool _sliderUpdating;

        private async Task LoadRecordingAsync(string path)
        {
            _player = new RecordingPlayer();
            _player.PositionChanged += (pos, dur) => Dispatcher.UIThread.Post(() => UpdatePlaybackUi(pos, dur));
            _player.PlaybackEnded += () => Dispatcher.UIThread.Post(() =>
            {
                SetPlayIcon(playing: false);
                _player?.Seek(TimeSpan.Zero);
            });

            var error = await _player.LoadAsync(path);
            if (error != null)
            {
                SetText("PlaybackDurationText", "—");
                SetText("RecordingPathTextBlock", $"{path}  ({error})");
                if (this.FindControl<Button>("PlayPauseButton") is { } b) b.IsEnabled = false;
                return;
            }
            UpdatePlaybackUi(TimeSpan.Zero, _player.Duration);
        }

        private void UpdatePlaybackUi(TimeSpan pos, TimeSpan dur)
        {
            SetText("PlaybackPositionText", Fmt(pos));
            SetText("PlaybackDurationText", Fmt(dur));
            if (this.FindControl<Slider>("PlaybackSlider") is { } slider && dur.TotalSeconds > 0)
            {
                _sliderUpdating = true;
                try { slider.Value = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds * 100.0, 0, 100); }
                finally { _sliderUpdating = false; }
            }
        }

        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private void SetPlayIcon(bool playing)
        {
            if (this.FindControl<FluentIcons.Avalonia.SymbolIcon>("PlayIcon") is { } icon)
                icon.Symbol = playing ? FluentIcons.Common.Symbol.Pause : FluentIcons.Common.Symbol.Play;
        }

        private void PopulateTechnicalDetails()
        {
            if (_technicalDetails == null || _technicalDetails.Count == 0) return;

            var section = this.FindControl<Border>("TechnicalSection");
            if (section != null) section.IsVisible = true;

            SetText("TechnicalDetailsTextBlock", string.Join(Environment.NewLine, _technicalDetails));
        }

        // ── Playback handlers ─────────────────────────────────────────────────
        private void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
        {
            if (_player == null || !_player.IsLoaded) return;
            if (_player.IsPlaying) { _player.Pause(); SetPlayIcon(false); }
            else { _player.Play(); SetPlayIcon(true); }
        }

        private void StopPlaybackButton_Click(object? sender, RoutedEventArgs e)
        {
            _player?.Stop();
            SetPlayIcon(false);
            if (_player != null) UpdatePlaybackUi(TimeSpan.Zero, _player.Duration);
        }

        private void PlaybackSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        {
            if (_sliderUpdating || _player == null || !_player.IsLoaded) return;
            _player.Seek(TimeSpan.FromSeconds(_player.Duration.TotalSeconds * e.NewValue / 100.0));
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _player?.Dispose(); } catch { }
            _player = null;
            base.OnClosed(e);
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
