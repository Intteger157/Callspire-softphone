#if WINDOWS
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FluentIcons.Common;

namespace Softphone
{
    public partial class CustomMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.OK;

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;

        [DllImport("dwmapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private CustomMessageBox(string title, string message, MessageBoxButton buttons, MessageBoxImage icon, string? yesButtonText = null, string? noButtonText = null)
        {
            InitializeComponent();

            Loaded += (s, e) =>
            {
                try
                {
                    if (Content is Grid rootGrid)
                    {
                        if (Application.Current != null && Application.Current.TryFindResource("DialogBackgroundBrush") is Brush b)
                            rootGrid.Background = b;
                    }
                }
                catch { }
            };

            if (NativeWindowAppearanceManager.IsWindows11OrGreater())
            {
                SourceInitialized += (s, e) =>
                {
                    try
                    {
                        var hwnd = new WindowInteropHelper(this).Handle;
                        if (hwnd != IntPtr.Zero)
                        {
                            int cornerPreference = DWMWCP_DONOTROUND;
                            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
                        }
                    }
                    catch { }
                };
            }

            TitleTextBlock.Text = title;
            MessageTextBlock.Text = message;

            switch (icon)
            {
                case MessageBoxImage.Information:
                    IconSymbol.Symbol = Symbol.Info;
                    IconSymbol.Foreground = (SolidColorBrush)FindResource("AccentBlueBrush");
                    break;
                case MessageBoxImage.Warning:
                    IconSymbol.Symbol = Symbol.Warning;
                    IconSymbol.Foreground = (SolidColorBrush)FindResource("AccentRedBrush");
                    break;
                case MessageBoxImage.Error:
                    IconSymbol.Symbol = Symbol.ErrorCircle;
                    IconSymbol.Foreground = (SolidColorBrush)FindResource("AccentRedBrush");
                    break;
                case MessageBoxImage.Question:
                    IconSymbol.Symbol = Symbol.QuestionCircle;
                    IconSymbol.Foreground = (SolidColorBrush)FindResource("AccentBlueBrush");
                    break;
                default:
                    IconSymbol.Symbol = Symbol.Info;
                    IconSymbol.Foreground = (SolidColorBrush)FindResource("AccentBlueBrush");
                    break;
            }

            if (buttons == MessageBoxButton.YesNo)
            {
                OkButton.Visibility = Visibility.Collapsed;
                YesNoButtonsPanel.Visibility = Visibility.Visible;
                YesButton.Content = yesButtonText ?? "Yes";
                NoButton.Content = noButtonText ?? "No";
                if (!string.IsNullOrEmpty(yesButtonText))
                    YesButton.MinWidth = MeasureChoiceButtonWidth(yesButtonText);
                if (!string.IsNullOrEmpty(noButtonText))
                    NoButton.MinWidth = MeasureChoiceButtonWidth(noButtonText);
            }
            else if (message.Length > 280)
            {
                WrapMessageInScrollViewer();
            }
        }

        private void WrapMessageInScrollViewer()
        {
            var parent = (Grid)MessageTextBlock.Parent;
            int col = Grid.GetColumn(MessageTextBlock);
            parent.Children.Remove(MessageTextBlock);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 420,
            };
            Grid.SetColumn(scroll, col);
            scroll.Content = MessageTextBlock;
            parent.Children.Add(scroll);
        }

        private static double MeasureChoiceButtonWidth(string text)
        {
            return Math.Max(80, text.Length * 9 + 34);
        }

        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Yes;
            DialogResult = true;
            Close();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.No;
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.Cancel;
            DialogResult = false;
            Close();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = MessageBoxResult.OK;
            DialogResult = true;
            Close();
        }

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, Window? owner = null, string? yesButtonText = null, string? noButtonText = null)
        {
            var dialog = new CustomMessageBox(caption, messageBoxText, button, icon, yesButtonText, noButtonText);
            if (owner != null)
            {
                try
                {
                    if (owner.IsLoaded && owner.IsVisible)
                        dialog.Owner = owner;
                }
                catch { }
            }
            dialog.ShowDialog();
            return dialog.Result;
        }

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        {
            return Show(messageBoxText, caption, button, icon, null);
        }

        public static MessageBoxResult Show(string messageBoxText, string caption)
        {
            return Show(messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public static MessageBoxResult Show(string messageBoxText)
        {
            return Show(messageBoxText, "Message");
        }
    }
}

#endif // WINDOWS
