using System;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using Softphone.AppHost;
using Softphone.AppHost.ViewModels;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Active-call window. Thin view over <see cref="CallViewModel"/>: all SIP/WebRTC handling,
    /// tones, recording and completion reporting live in the view model.
    /// </summary>
    public partial class CallWindow : Window
    {
        private readonly CallViewModel _vm;
        private bool _closeRequestedByVm;

        public CallViewModel ViewModel => _vm;

        /// <summary>Raised once per call with the final report (forwarded from the view model).</summary>
        public event Action<CallEndedReport>? CallCompleted;

        /// <summary>XAML-compiler / previewer constructor only.</summary>
        [Obsolete("Design-time only", error: true)]
        public CallWindow()
        {
            _vm = null!;
            InitializeComponent();
        }

        public CallWindow(DesktopAppController controller, CallWindowRequest request)
        {
            _vm = new CallViewModel(controller, request);
            DataContext = _vm;
            InitializeComponent();
            PlatformWindowChrome.Apply(this, preferAccent: true);

            _vm.Completed += report => CallCompleted?.Invoke(report);
            _vm.CloseRequested += () =>
            {
                _closeRequestedByVm = true;
                try { Close(); } catch { }
            };

            Opened += async (_, _) =>
            {
                try { await _vm.StartAsync(); }
                catch (Exception ex) { AppLog.Log($"[CallWindow] StartAsync: {ex.Message}"); }
            };
        }

        // ── Title bar ─────────────────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object? sender, RoutedEventArgs e) => _ = _vm.HangupAsync();

        // ── Call buttons ──────────────────────────────────────────────────────
        private void HangupButton_Click(object? sender, RoutedEventArgs e) => _ = _vm.HangupAsync();
        private void AnswerButton_Click(object? sender, RoutedEventArgs e) => _ = _vm.AnswerAsync();
        private void RejectButton_Click(object? sender, RoutedEventArgs e) => _ = _vm.RejectAsync();
        private void MuteButton_Click(object? sender, RoutedEventArgs e) => _ = _vm.ToggleMuteAsync();
        private void HoldButton_Click(object? sender, RoutedEventArgs e) => _ = _vm.ToggleHoldAsync();
        private void KeypadToggleButton_Click(object? sender, RoutedEventArgs e) => _vm.ToggleKeypad();

        private void DtmfButton_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Content: string s } && s.Length > 0)
                _ = _vm.SendDtmfAsync(s[0]);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;

            if (e.Key == Key.Escape) { e.Handled = true; _ = _vm.HangupAsync(); return; }
            if (e.Key == Key.Enter && _vm.ShowIncomingButtons) { e.Handled = true; _ = _vm.AnswerAsync(); return; }

            // Type DTMF digits while connected.
            if (_vm.WasAnswered)
            {
                char? digit = e.Key switch
                {
                    >= Key.D0 and <= Key.D9 => (char)('0' + (e.Key - Key.D0)),
                    >= Key.NumPad0 and <= Key.NumPad9 => (char)('0' + (e.Key - Key.NumPad0)),
                    Key.Multiply => '*',
                    _ => null,
                };
                if (digit.HasValue) { e.Handled = true; _ = _vm.SendDtmfAsync(digit.Value); }
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (!_closeRequestedByVm) _vm.OnViewClosed();
            else _vm.Dispose();
        }
    }
}
