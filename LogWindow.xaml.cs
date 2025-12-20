using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace Softphone
{
    public partial class LogWindow : Window
    {
        public LogWindow()
        {
            InitializeComponent();
        }

        public void AddLog(string message)
        {
            if (LogTextBox != null)
            {
                LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                LogTextBox.ScrollToEnd();
            }
        }

        public void SetLogs(string logs)
        {
            if (LogTextBox != null)
            {
                LogTextBox.Text = logs;
                LogTextBox.ScrollToEnd();
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
            Close();
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                Clipboard.SetText(LogTextBox.Text);
                CustomMessageBox.Show("Logs copied to clipboard!", "Success", MessageBoxButton.OK, MessageBoxImage.Information, this);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(LogTextBox.Text))
            {
                CustomMessageBox.Show("No logs to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information, this);
                return;
            }

            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
                    FileName = $"SoftphoneLogs_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DefaultExt = "txt"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    File.WriteAllText(saveDialog.FileName, LogTextBox.Text);
                    CustomMessageBox.Show($"Logs exported successfully to:\n{saveDialog.FileName}", "Export Success", MessageBoxButton.OK, MessageBoxImage.Information, this);
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Show($"Error exporting logs:\n{ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error, this);
            }
        }
    }
}

