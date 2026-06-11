using System;
using System.Collections.Generic;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Input;
using global::Avalonia.Layout;
using global::Avalonia.Media;
using global::FluentIcons.Avalonia;
using global::FluentIcons.Common;

namespace Softphone.Avalonia
{
    public partial class ConnectionSelectionWindow : Window
    {
        // ── Result properties ─────────────────────────────────────────────────
        public string?         SelectedCallerId   { get; private set; }
        public ConnectionType? SelectedConnection { get; private set; }

        // ── Types ─────────────────────────────────────────────────────────────
        public enum ConnectionType { Main, Secondary }

        public class ConnectionInfo
        {
            public ConnectionType Type        { get; set; }
            public string         Name        { get; set; } = string.Empty;
            public string         Description { get; set; } = string.Empty;
            public string         Status      { get; set; } = string.Empty;
            public IBrush         StatusColor { get; set; } = Brushes.Gray;
        }

        // ── Constructor ───────────────────────────────────────────────────────
        public ConnectionSelectionWindow(
            bool          hasMainConnection,
            bool          isMainWebRtc,
            string?       mainConnectionStatus,
            bool          hasSecondaryConnection,
            string?       secondaryConnectionStatus,
            string?       mainConnectionName        = null,
            string?       secondaryConnectionName   = null,
            List<CallerIdItem>? mainCallerIdItems   = null,
            string?       selectedMainCallerId       = null,
            bool          forceShowForSingleConnection = false,
            bool          isSecondaryWebRtc           = false)
        {
            InitializeComponent();

            var connections = new List<ConnectionInfo>();

            if (hasMainConnection)
            {
                connections.Add(new ConnectionInfo
                {
                    Type        = ConnectionType.Main,
                    Name        = mainConnectionName ?? (isMainWebRtc ? "WebRTC" : "PBX"),
                    Description = isMainWebRtc ? "WebRTC call via browser engine" : "Primary SIP connection",
                    Status      = mainConnectionStatus ?? "Ready",
                    StatusColor = Brushes.LimeGreen,
                });
            }

            if (hasSecondaryConnection)
            {
                connections.Add(new ConnectionInfo
                {
                    Type        = ConnectionType.Secondary,
                    Name        = secondaryConnectionName ?? (isSecondaryWebRtc ? "WebRTC (2)" : "SIP"),
                    Description = isSecondaryWebRtc ? "WebRTC secondary engine" : "Additional SIP connection",
                    Status      = secondaryConnectionStatus ?? "Ready",
                    StatusColor = Brushes.LimeGreen,
                });
            }

            BuildConnectionButtons(connections, mainCallerIdItems ?? new List<CallerIdItem>(), selectedMainCallerId);
        }

        // ── UI builders ───────────────────────────────────────────────────────
        private void BuildConnectionButtons(
            List<ConnectionInfo> connections,
            List<CallerIdItem>   callerIdItems,
            string?              selectedCallerId)
        {
            var panel = this.FindControl<StackPanel>("ConnectionsPanel");
            if (panel == null) return;

            foreach (var conn in connections)
            {
                var btn = new Button
                {
                    HorizontalAlignment          = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment   = HorizontalAlignment.Stretch,
                    Background                   = Application.Current?.TryGetResource("BackgroundMediumBrush", null, out var r1) == true ? r1 as IBrush : Brushes.Transparent,
                    BorderThickness              = new Thickness(1),
                    CornerRadius                 = new CornerRadius(8),
                    Margin                       = new Thickness(0, 0, 0, 8),
                    Padding                      = new Thickness(12, 10),
                    Cursor                       = new Cursor(StandardCursorType.Hand),
                    Tag                          = conn,
                };

                var icon = new SymbolIcon
                {
                    Symbol      = conn.Type == ConnectionType.Main ? Symbol.PhoneAdd : Symbol.Phone,
                    IconVariant = IconVariant.Regular,
                    Width       = 20,
                    Height      = 20,
                    Margin      = new Thickness(0, 0, 10, 0),
                };

                var nameText = new TextBlock
                {
                    Text       = conn.Name,
                    FontSize   = 14,
                    FontWeight = FontWeight.SemiBold,
                };

                var statusText = new TextBlock
                {
                    Text       = conn.Status,
                    FontSize   = 12,
                    Foreground = conn.StatusColor,
                    Margin     = new Thickness(0, 2, 0, 0),
                };

                var textStack = new StackPanel();
                textStack.Children.Add(nameText);
                textStack.Children.Add(statusText);

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(icon);
                row.Children.Add(textStack);

                btn.Content = row;
                btn.Click  += (_, _) => { SelectedConnection = conn.Type; Close(); };

                panel.Children.Add(btn);
            }

            // CallerID ComboBox (if multiple IDs available for main connection)
            if (callerIdItems.Count > 1)
            {
                var labelBlock = new TextBlock
                {
                    Text     = "Outbound Caller ID",
                    FontSize = 12,
                    Margin   = new Thickness(0, 8, 0, 4),
                };

                var combo = new ComboBox
                {
                    ItemsSource              = callerIdItems,
                    DisplayMemberBinding     = new global::Avalonia.Data.Binding("DisplayText"),
                    HorizontalAlignment      = HorizontalAlignment.Stretch,
                    Margin                   = new Thickness(0, 0, 0, 8),
                };

                combo.SelectedIndex = 0;
                if (!string.IsNullOrEmpty(selectedCallerId))
                {
                    for (int i = 0; i < callerIdItems.Count; i++)
                    {
                        if (callerIdItems[i].Number == selectedCallerId)
                        {
                            combo.SelectedIndex = i;
                            break;
                        }
                    }
                }

                combo.SelectionChanged += (_, e) =>
                {
                    if (combo.SelectedItem is CallerIdItem item)
                        SelectedCallerId = item.Number;
                };

                panel.Children.Add(labelBlock);
                panel.Children.Add(combo);
            }
        }

        // ── Window chrome handlers ────────────────────────────────────────────
        private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void CloseButton_Click(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
            => Close();
    }
}
