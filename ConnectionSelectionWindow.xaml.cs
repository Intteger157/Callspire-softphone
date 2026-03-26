using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FluentIcons.Wpf;
using FluentIcons.Common;

namespace Softphone
{
    public partial class ConnectionSelectionWindow : Window
    {
        public string? SelectedCallerId { get; private set; }

        private readonly List<CallerIdItem> _mainCallerIdItems;
        private readonly string? _initialSelectedMainCallerId;

        public enum ConnectionType
        {
            Main,      // Основное подключение (WebRTC или SIP)
            Secondary  // Второе подключение (только SIP)
        }

        public ConnectionType? SelectedConnection { get; private set; }

        public class ConnectionInfo
        {
            public ConnectionType Type { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public Brush StatusColor { get; set; } = Brushes.Gray;
        }

        public ConnectionSelectionWindow(bool hasMainConnection, bool isMainWebRtc, string? mainConnectionStatus,
            bool hasSecondaryConnection, string? secondaryConnectionStatus,
            string? mainConnectionName = null, string? secondaryConnectionName = null,
            List<CallerIdItem>? mainCallerIdItems = null, string? selectedMainCallerId = null)
        {
            InitializeComponent();

            _mainCallerIdItems = mainCallerIdItems ?? new List<CallerIdItem>();
            _initialSelectedMainCallerId = selectedMainCallerId;
            
            // Устанавливаем фон окна сразу после инициализации, чтобы избежать белой полосы сверху
            try
            {
                if (Application.Current?.TryFindResource("BackgroundDarkBrush") is System.Windows.Media.Brush brush)
                {
                    this.Background = brush;
                }
            }
            catch { }
            
            // Также устанавливаем фон в SourceInitialized, чтобы он точно был установлен до показа окна
            this.SourceInitialized += (s, e) =>
            {
                try
                {
                    if (Application.Current?.TryFindResource("BackgroundDarkBrush") is System.Windows.Media.Brush brush)
                    {
                        this.Background = brush;
                    }
                }
                catch { }
            };
            
            NativeWindowAppearanceManager.Attach(this);

            var connections = new List<ConnectionInfo>();

            // Основное подключение
            if (hasMainConnection)
            {
                // Используем пользовательское название или стандартное
                string mainName = !string.IsNullOrWhiteSpace(mainConnectionName) 
                    ? mainConnectionName 
                    : (isMainWebRtc ? "Main Connection (WebRTC)" : "Main Connection (SIP)");
                
                connections.Add(new ConnectionInfo
                {
                    Type = ConnectionType.Main,
                    Name = mainName,
                    Description = "", // Убрали описание
                    Status = mainConnectionStatus ?? "Unknown",
                    StatusColor = GetStatusColor(mainConnectionStatus)
                });
            }

            // Второе подключение
            if (hasSecondaryConnection)
            {
                // Используем пользовательское название или стандартное
                string secondaryName = !string.IsNullOrWhiteSpace(secondaryConnectionName)
                    ? secondaryConnectionName
                    : "Additional Connection (SIP Only)";
                
                connections.Add(new ConnectionInfo
                {
                    Type = ConnectionType.Secondary,
                    Name = secondaryName,
                    Description = "", // Убрали описание
                    Status = secondaryConnectionStatus ?? "Unknown",
                    StatusColor = GetStatusColor(secondaryConnectionStatus)
                });
            }

            if (connections.Count == 0)
            {
                // Если нет доступных подключений, закрываем окно
                DialogResult = false;
                Close();
                return;
            }

            // Если только одно подключение, автоматически выбираем его
            if (connections.Count == 1)
            {
                SelectedConnection = connections[0].Type;
                if (SelectedConnection == ConnectionType.Main && _mainCallerIdItems.Count > 0)
                {
                    var selectedNumber =
                        !string.IsNullOrWhiteSpace(_initialSelectedMainCallerId) &&
                        _mainCallerIdItems.Any(i => i.Number == _initialSelectedMainCallerId)
                            ? _initialSelectedMainCallerId
                            : _mainCallerIdItems[0].Number;

                    SelectedCallerId = selectedNumber;
                }
                DialogResult = true;
                Close();
                return;
            }

            // Создаем блоки для каждого подключения
            bool isFirst = true;
            foreach (var connection in connections)
            {
                var connectionBlock = CreateConnectionBlock(connection);
                // Добавляем промежуток между блоками (кроме первого)
                if (!isFirst)
                {
                    connectionBlock.Margin = new Thickness(0, 8, 0, 0);
                }
                isFirst = false;
                
                ConnectionsPanel.Children.Add(connectionBlock);
            }
        }

        private Brush GetStatusColor(string? status)
        {
            if (string.IsNullOrEmpty(status))
                return (Brush)FindResource("TextSecondaryBrush");

            string statusLower = status.ToLowerInvariant();
            if (statusLower.Contains("connected") || statusLower.Contains("registered"))
            {
                return (Brush)FindResource("AccentGreenBrush");
            }
            else if (statusLower.Contains("not connected") || statusLower.Contains("failed") || statusLower.Contains("error"))
            {
                return (Brush)FindResource("AccentRedBrush");
            }
            else if (statusLower.Contains("connecting"))
            {
                return (Brush)FindResource("AccentBlueBrush");
            }
            else
            {
                return (Brush)FindResource("TextSecondaryBrush");
            }
        }

        private Border CreateConnectionBlock(ConnectionInfo connection)
        {
            // Создаем Grid для размещения элементов
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            
            // Левая часть: название и статус
            var leftPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            
            leftPanel.Children.Add(new TextBlock
            {
                Text = connection.Name,
                Foreground = (Brush)FindResource("TextPrimaryBrush"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            
            leftPanel.Children.Add(new TextBlock
            {
                Text = connection.Status,
                Foreground = connection.StatusColor,
                FontSize = 11,
                FontWeight = FontWeights.Medium
            });
            
            ComboBox? callerIdComboBox = null;
            if (connection.Type == ConnectionType.Main && _mainCallerIdItems.Count > 0)
            {
                // Для WebRTC->PBX Originate нужен выбор Outbound Caller ID.
                // Показываем выпадающий список только для Main подключения.
                var initialNumber =
                    !string.IsNullOrWhiteSpace(_initialSelectedMainCallerId) &&
                    _mainCallerIdItems.Any(i => i.Number == _initialSelectedMainCallerId)
                        ? _initialSelectedMainCallerId
                        : _mainCallerIdItems[0].Number;

                callerIdComboBox = new ComboBox
                {
                    // Делаем ComboBox компактнее именно в окне выбора подключения
                    MinWidth = 180,
                    MaxWidth = 210,
                    Height = 28,
                    Margin = new Thickness(0, 6, 0, 0),
                    Padding = new Thickness(10, 4, 10, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    // Используем общий стиль для ComboBox (иначе в светлой/темной теме может быть неверная раскраска Popup).
                    Style = (Style)FindResource("ModernComboBoxStyle"),
                    ItemsSource = _mainCallerIdItems,
                    DisplayMemberPath = "DisplayText",
                    SelectedValuePath = "Number",
                    SelectedValue = initialNumber
                };

                leftPanel.Children.Add(callerIdComboBox);
            }

            Grid.SetColumn(leftPanel, 0);
            grid.Children.Add(leftPanel);
            
            // Правая часть: кнопка вызова
            var callIcon = new FluentIcons.Wpf.SymbolIcon
            {
                Symbol = FluentIcons.Common.Symbol.Call,
                Width = 20,
                Height = 20,
                Foreground = Brushes.White
            };
            
            var callButton = new Button
            {
                Content = callIcon,
                Background = (Brush)FindResource("AccentGreenBrush"),
                BorderThickness = new Thickness(0),
                Width = 40,
                Height = 40,
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = connection,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            
            // Стиль для кнопки вызова
            var callButtonStyle = new Style(typeof(Button));
            var callButtonTemplate = new ControlTemplate(typeof(Button));
            var callButtonBorder = new FrameworkElementFactory(typeof(Border));
            callButtonBorder.Name = "callButtonBorder";
            callButtonBorder.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
            callButtonBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
            callButtonBorder.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));
            
            var callButtonContent = new FrameworkElementFactory(typeof(ContentPresenter));
            callButtonContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            callButtonContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            
            callButtonBorder.AppendChild(callButtonContent);
            callButtonTemplate.VisualTree = callButtonBorder;
            
            // Триггеры для кнопки вызова
            var callButtonHoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            callButtonHoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(31, 177, 88)), "callButtonBorder"));
            callButtonTemplate.Triggers.Add(callButtonHoverTrigger);
            
            var callButtonPressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
            callButtonPressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(23, 136, 53)), "callButtonBorder"));
            callButtonTemplate.Triggers.Add(callButtonPressedTrigger);
            
            callButtonStyle.Setters.Add(new Setter(Control.TemplateProperty, callButtonTemplate));
            callButton.Style = callButtonStyle;
            
            callButton.Click += (s, e) =>
            {
                SelectedConnection = connection.Type;
                if (callerIdComboBox != null)
                    SelectedCallerId = callerIdComboBox.SelectedValue as string;
                MainWindow.Log($"[ConnectionSelectionWindow] User selected connection: {connection.Type}");
                DialogResult = true;
                Close();
            };
            
            Grid.SetColumn(callButton, 1);
            grid.Children.Add(callButton);
            
            // Создаем Border для блока подключения
            var border = new Border
            {
                Background = (Brush)FindResource("BackgroundMediumBrush"),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 14, 16, 14),
                Child = grid
            };
            
            return border;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedConnection = null;
            SelectedCallerId = null;
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
            }
        }
    }
}
