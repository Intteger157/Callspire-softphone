using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace Softphone
{
    public partial class MainWindow : Window
    {
        private SipService? _sipService;

        public MainWindow()
        {
            InitializeComponent();
            ShowView(DialerView);
        }

        private void ShowView(Grid view)
        {
            ContactsView.Visibility = Visibility.Collapsed;
            DialerView.Visibility = Visibility.Collapsed;
            HistoryView.Visibility = Visibility.Collapsed;

            view.Visibility = Visibility.Visible;
        }

        private void ContactsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(ContactsView);
        }

        private void DialerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(DialerView);
        }

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(HistoryView);
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow
            {
                Owner = this
            };
            settingsWindow.ShowDialog();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string server = SipServerTextBox.Text.Trim();
                string username = SipUsernameTextBox.Text.Trim();
                string password = SipPasswordBox.Password;

                if (string.IsNullOrEmpty(server) || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    MessageBox.Show("Please fill in all SIP connection fields.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Dispose existing service if any
                _sipService?.Dispose();

                _sipService = new SipService(username, password, server);
                _sipService.OnStatusChanged += (status) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusTextBlock.Text = $"Status: {status}";
                    });
                };

                ConnectButton.IsEnabled = false;
                StatusTextBlock.Text = "Status: Connecting...";

                await _sipService.StartAsync();

                CallButton.IsEnabled = true;
                StatusTextBlock.Text = "Status: Connected";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Connection error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = $"Status: Error - {ex.Message}";
                ConnectButton.IsEnabled = true;
            }
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sipService == null)
            {
                MessageBox.Show("Please connect first.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string number = PhoneNumberTextBox.Text.Trim();
            if (string.IsNullOrEmpty(number))
            {
                MessageBox.Show("Please enter a phone number.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                CallButton.IsEnabled = false;
                HangupButton.IsEnabled = true;
                CallStatusTextBlock.Text = $"Calling {number}...";

                await _sipService.CallAsync(number);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Call error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CallStatusTextBlock.Text = $"Error: {ex.Message}";
                CallButton.IsEnabled = true;
                HangupButton.IsEnabled = false;
            }
        }

        private void HangupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_sipService == null) return;

            try
            {
                _sipService.Hangup();
                CallButton.IsEnabled = true;
                HangupButton.IsEnabled = false;
                CallStatusTextBlock.Text = "Call ended";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Hangup error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _sipService?.Dispose();
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _sipService?.Dispose();
            base.OnClosed(e);
        }
    }
}

