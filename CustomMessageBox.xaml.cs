using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Softphone
{
    public partial class CustomMessageBox : Window
    {
        public MessageBoxResult Result { get; private set; } = MessageBoxResult.OK;

        private Button? YesButton { get; set; }
        private Button? NoButton { get; set; }

        private CustomMessageBox(string title, string message, MessageBoxButton buttons, MessageBoxImage icon)
        {
            InitializeComponent();
            
            TitleTextBlock.Text = title;
            MessageTextBlock.Text = message;
            
            // Устанавливаем иконку в зависимости от типа сообщения
            switch (icon)
            {
                case MessageBoxImage.Information:
                    IconTextBlock.Text = "\uE783"; // Info icon
                    IconTextBlock.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentBlueBrush");
                    break;
                case MessageBoxImage.Warning:
                    IconTextBlock.Text = "\uE7BA"; // Warning icon
                    IconTextBlock.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentRedBrush");
                    break;
                case MessageBoxImage.Error:
                    IconTextBlock.Text = "\uE783"; // Error icon (можно использовать другой)
                    IconTextBlock.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentRedBrush");
                    break;
                case MessageBoxImage.Question:
                    IconTextBlock.Text = "\uE783"; // Question icon
                    IconTextBlock.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentBlueBrush");
                    break;
                default:
                    IconTextBlock.Text = "\uE783"; // Default info icon
                    IconTextBlock.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentBlueBrush");
                    break;
            }

            // Настраиваем кнопки в зависимости от типа
            if (buttons == MessageBoxButton.YesNo)
            {
                OkButton.Visibility = Visibility.Collapsed;
                SetupYesNoButtons();
            }
        }

        private void SetupYesNoButtons()
        {
            var buttonsPanel = (Grid)OkButton.Parent;
            
            buttonsPanel.ColumnDefinitions.Clear();
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonsPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Yes button - используем тот же стиль что и OK
            YesButton = new Button
            {
                Content = "Yes",
                Width = 80,
                Height = 32,
                Background = (System.Windows.Media.SolidColorBrush)FindResource("AccentBlueBrush"),
                Foreground = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right
            };
            YesButton.Click += (s, e) => { Result = MessageBoxResult.Yes; DialogResult = true; Close(); };
            YesButton.SetValue(Grid.ColumnProperty, 1);
            YesButton.Style = OkButton.Style;
            buttonsPanel.Children.Add(YesButton);

            // No button - вторичный стиль
            NoButton = new Button
            {
                Content = "No",
                Width = 80,
                Height = 32,
                Background = (System.Windows.Media.SolidColorBrush)FindResource("BackgroundMediumBrush"),
                Foreground = (System.Windows.Media.SolidColorBrush)FindResource("TextPrimaryBrush"),
                BorderThickness = new Thickness(1),
                BorderBrush = (System.Windows.Media.SolidColorBrush)FindResource("BorderBrush"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            NoButton.Click += (s, e) => { Result = MessageBoxResult.No; DialogResult = false; Close(); };
            NoButton.SetValue(Grid.ColumnProperty, 2);
            
            // Простой стиль для No кнопки через XAML-подобный подход
            var noButtonStyle = new Style(typeof(Button));
            noButtonStyle.Setters.Add(new Setter(Button.TemplateProperty, CreateNoButtonTemplate()));
            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, (System.Windows.Media.SolidColorBrush)FindResource("HoverBackgroundBrush")));
            noButtonStyle.Triggers.Add(hoverTrigger);
            NoButton.Style = noButtonStyle;
            buttonsPanel.Children.Add(NoButton);
        }

        private ControlTemplate CreateNoButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            
            var contentPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
            contentPresenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            contentPresenter.SetValue(ContentPresenter.MarginProperty, new Thickness(16, 8, 16, 8));
            border.AppendChild(contentPresenter);
            
            template.VisualTree = border;
            return template;
        }

        private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                DragMove();
            }
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

        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon, Window? owner = null)
        {
            var dialog = new CustomMessageBox(caption, messageBoxText, button, icon);
            if (owner != null)
            {
                // Проверяем, что окно еще не закрыто перед установкой Owner
                try
                {
                    // Проверяем, что окно загружено и не закрыто
                    if (owner.IsLoaded && owner.IsVisible)
                    {
                        dialog.Owner = owner;
                    }
                }
                catch
                {
                    // Если окно уже закрыто или недоступно, просто не устанавливаем Owner
                    // Диалог откроется без владельца
                }
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



