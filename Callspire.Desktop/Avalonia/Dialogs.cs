using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.Primitives;
using global::Avalonia.Input.Platform;
using global::Avalonia.Layout;
using global::Avalonia.Media;
using global::Avalonia.Threading;
using Softphone.AppHost;

namespace Softphone.Avalonia
{
    /// <summary>
    /// Small code-built dialogs shared by the Avalonia shell: message box, Kommo lead picker
    /// and a live log viewer. Kept in one file — they are simple and have no XAML of their own.
    /// </summary>
    internal static class Dialogs
    {
        private static IBrush Brush(string key, IBrush fallback)
            => Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var v) == true && v is IBrush b ? b : fallback;

        private static Window Frame(string title, double width, double height, Window? owner)
        {
            var w = new Window
            {
                Title = title,
                Width = width,
                Height = height,
                MinWidth = Math.Min(width, 320),
                CanResize = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Background = Brush("BackgroundDarkBrush", Brushes.Black),
                ShowInTaskbar = false,
            };
            PlatformWindowChrome.Apply(w);
            return w;
        }

        private static Button MakeButton(string text, bool accent = false)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(16, 8),
                MinWidth = 96,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(6),
                Background = Brush(accent ? "AccentBlueBrush" : "BackgroundMediumBrush", accent ? Brushes.DodgerBlue : Brushes.DimGray),
                Foreground = accent ? Brushes.White : Brush("TextPrimaryBrush", Brushes.White),
                BorderThickness = new Thickness(accent ? 0 : 1),
                BorderBrush = Brush("BorderBrush", Brushes.Gray),
            };
            return b;
        }

        // ── Message box ───────────────────────────────────────────────────────

        public static Task ShowMessageAsync(Window? owner, string title, string text)
        {
            var tcs = new TaskCompletionSource();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var w = Frame(title, 420, 200, owner);
                    var ok = MakeButton("OK", accent: true);
                    ok.Click += (_, _) => w.Close();

                    w.Content = new Grid
                    {
                        Margin = new Thickness(24),
                        RowDefinitions = new RowDefinitions("Auto,*,Auto"),
                        Children =
                        {
                            new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimaryBrush", Brushes.White), [Grid.RowProperty] = 0 },
                            new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16), Foreground = Brush("TextSecondaryBrush", Brushes.LightGray), [Grid.RowProperty] = 1 },
                            new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok }, [Grid.RowProperty] = 2 },
                        }
                    };
                    w.Closed += (_, _) => tcs.TrySetResult();
                    if (owner != null && owner.IsVisible) await w.ShowDialog(owner); else w.Show();
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[Dialogs] ShowMessage failed: {ex.Message}");
                    tcs.TrySetResult();
                }
            });
            return tcs.Task;
        }

        // ── Kommo lead selection ──────────────────────────────────────────────

        /// <summary>
        /// Gateway-mode lead picker. Lead search itself happens on the gateway; the user can let the
        /// gateway attach the call to the contact automatically or type a specific lead ID.
        /// </summary>
        public static Task<LeadSelectionResult> ShowLeadSelectionAsync(Window? owner, string phoneNumber)
        {
            var tcs = new TaskCompletionSource<LeadSelectionResult>();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var w = Frame("Kommo: attach call", 460, 300, owner);
                    var result = LeadSelectionResult.CancelledResult;

                    var leadBox = new TextBox { Watermark = "Lead ID (optional)", Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
                    var error = new TextBlock { Foreground = Brush("AccentRedBrush", Brushes.OrangeRed), FontSize = 12, IsVisible = false };

                    var auto = MakeButton("Attach to contact", accent: true);
                    auto.Click += (_, _) => { result = LeadSelectionResult.Contact; w.Close(); };

                    var useLead = MakeButton("Use lead ID");
                    useLead.Click += (_, _) =>
                    {
                        if (long.TryParse((leadBox.Text ?? "").Trim(), out var id) && id > 0)
                        {
                            result = new LeadSelectionResult { Proceed = true, LeadId = id };
                            w.Close();
                        }
                        else
                        {
                            error.Text = "Enter a numeric Kommo lead ID.";
                            error.IsVisible = true;
                        }
                    };

                    var cancel = MakeButton("Skip");
                    cancel.Click += (_, _) => { result = LeadSelectionResult.CancelledResult; w.Close(); };

                    w.Content = new StackPanel
                    {
                        Margin = new Thickness(24),
                        Spacing = 10,
                        Children =
                        {
                            new TextBlock { Text = $"Call with {phoneNumber} has ended.", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimaryBrush", Brushes.White) },
                            new TextBlock { Text = "Choose where the call note and recording should be attached in Kommo.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush", Brushes.LightGray) },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { leadBox, useLead } },
                            error,
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, auto } },
                        }
                    };
                    w.Closed += (_, _) => tcs.TrySetResult(result);
                    if (owner != null && owner.IsVisible) await w.ShowDialog(owner); else w.Show();
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[Dialogs] LeadSelection failed: {ex.Message}");
                    tcs.TrySetResult(LeadSelectionResult.Contact);
                }
            });
            return tcs.Task;
        }

        /// <summary>
        /// Local-mode lead picker (<see cref="KommoLeadSelectionUi.Handler"/>): shows the leads that
        /// <see cref="AmoCrmService"/> found for the contact and lets the user pick one or skip.
        /// </summary>
        public static Task<KommoLeadSelectionResult?> ShowKommoLeadPickerAsync(Window? owner, KommoLeadSelectionRequest request)
        {
            var tcs = new TaskCompletionSource<KommoLeadSelectionResult?>();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var w = Frame("Kommo: select lead", 520, 420, owner);
                    KommoLeadSelectionResult? result = null;

                    var list = new ListBox
                    {
                        ItemsSource = request.Leads,
                        SelectionMode = SelectionMode.Single,
                        Background = Brush("BackgroundMediumBrush", Brushes.DimGray),
                        CornerRadius = new CornerRadius(6),
                        ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<KommoLeadInfo>((lead, _) =>
                            new StackPanel
                            {
                                Margin = new Thickness(4, 6),
                                Children =
                                {
                                    new TextBlock { Text = lead.Name, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimaryBrush", Brushes.White) },
                                    new TextBlock { Text = string.IsNullOrWhiteSpace(lead.Description) ? $"#{lead.Id}" : lead.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush", Brushes.LightGray) },
                                }
                            }, true),
                    };
                    if (request.Leads.Count > 0) list.SelectedIndex = 0;

                    var open = MakeButton("Open in Kommo");
                    open.Click += (_, _) =>
                    {
                        if (list.SelectedItem is KommoLeadInfo lead && !string.IsNullOrWhiteSpace(request.Subdomain))
                            OpenExternal($"https://{request.Subdomain}.kommo.com/leads/detail/{lead.Id}");
                    };

                    var pick = MakeButton("Attach to lead", accent: true);
                    pick.Click += (_, _) =>
                    {
                        if (list.SelectedItem is KommoLeadInfo lead)
                        {
                            result = new KommoLeadSelectionResult { LeadId = lead.Id };
                            w.Close();
                        }
                    };
                    list.DoubleTapped += (_, _) => pick.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

                    var cancel = MakeButton("Skip");
                    cancel.Click += (_, _) => { result = null; w.Close(); };

                    string who = request.IsIncoming ? "Incoming call from" : "Outgoing call to";
                    string dur = request.WasAnswered ? $"{TimeSpan.FromSeconds(request.DurationSeconds):m\\:ss}" : "not answered";

                    w.Content = new Grid
                    {
                        Margin = new Thickness(24),
                        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
                        Children =
                        {
                            new TextBlock { Text = $"{who} {request.PhoneNumber}", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimaryBrush", Brushes.White), [Grid.RowProperty] = 0 },
                            new TextBlock { Text = $"{dur} · {request.Leads.Count} lead(s) found. Choose the lead this call belongs to.", Margin = new Thickness(0, 4, 0, 12), TextWrapping = TextWrapping.Wrap, Foreground = Brush("TextSecondaryBrush", Brushes.LightGray), [Grid.RowProperty] = 1 },
                            new Border { Child = list, [Grid.RowProperty] = 2 },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { open, cancel, pick }, [Grid.RowProperty] = 3 },
                        }
                    };
                    w.Closed += (_, _) => tcs.TrySetResult(result);
                    if (owner != null && owner.IsVisible) await w.ShowDialog(owner); else w.Show();
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[Dialogs] KommoLeadPicker failed: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });
            return tcs.Task;
        }

        // ── Update available ──────────────────────────────────────────────────

        private static Window? _updateWindow;

        /// <summary>
        /// Non-modal "new version" notice. On macOS/Linux the update is a package download opened in the
        /// browser (no in-app installer); the SHA-256 is shown so the user can verify the file.
        /// </summary>
        public static Task ShowUpdateAvailableAsync(Window? owner, UpdateInfo info, string currentVersion)
        {
            var tcs = new TaskCompletionSource();
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (_updateWindow is { IsVisible: true })
                    {
                        _updateWindow.Activate();
                        tcs.TrySetResult();
                        return;
                    }

                    var w = Frame("Update available", 480, 360, owner);
                    _updateWindow = w;

                    string url = info.PlatformUrl;
                    var download = MakeButton("Download", accent: true);
                    download.IsEnabled = !string.IsNullOrWhiteSpace(url);
                    download.Click += (_, _) => { OpenExternal(url); w.Close(); };

                    var later = MakeButton(info.Mandatory ? "Close" : "Later");
                    later.Click += (_, _) => w.Close();

                    var notes = new TextBox
                    {
                        Text = string.IsNullOrWhiteSpace(info.Notes) ? "No release notes." : info.Notes,
                        IsReadOnly = true,
                        AcceptsReturn = true,
                        TextWrapping = TextWrapping.Wrap,
                        Background = Brush("BackgroundMediumBrush", Brushes.DimGray),
                        Foreground = Brush("TextSecondaryBrush", Brushes.LightGray),
                        BorderThickness = new Thickness(0),
                    };

                    string? sha = info.PlatformSha256;
                    w.Content = new Grid
                    {
                        Margin = new Thickness(24),
                        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
                        Children =
                        {
                            new TextBlock { Text = $"Callspire {info.Version} is available", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextPrimaryBrush", Brushes.White), [Grid.RowProperty] = 0 },
                            new TextBlock { Text = $"You are running {currentVersion}." + (info.Mandatory ? " This update is required." : ""), Margin = new Thickness(0, 4, 0, 12), Foreground = Brush("TextSecondaryBrush", Brushes.LightGray), [Grid.RowProperty] = 1 },
                            new ScrollViewer { Content = notes, [Grid.RowProperty] = 2 },
                            new TextBlock { Text = string.IsNullOrWhiteSpace(sha) ? "" : $"SHA-256: {sha}", FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), IsVisible = !string.IsNullOrWhiteSpace(sha), Foreground = Brush("TextSecondaryBrush", Brushes.Gray), [Grid.RowProperty] = 3 },
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { later, download }, [Grid.RowProperty] = 4 },
                        }
                    };
                    w.Closed += (_, _) => { if (ReferenceEquals(_updateWindow, w)) _updateWindow = null; tcs.TrySetResult(); };
                    w.Show();
                    w.Activate();
                }
                catch (Exception ex)
                {
                    AppLog.Log($"[Dialogs] ShowUpdateAvailable failed: {ex.Message}");
                    tcs.TrySetResult();
                }
            });
            return tcs.Task;
        }

        private static void OpenExternal(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Log($"[Dialogs] open url failed: {ex.Message}"); }
        }
    }

    /// <summary>Live application log viewer fed by <see cref="LogBuffer"/>.</summary>
    public sealed class LogWindow : Window
    {
        private readonly TextBox _box;
        private readonly ScrollViewer _scroll;
        private bool _autoScroll = true;

        public LogWindow()
        {
            Title = "Callspire — Log";
            Width = 900;
            Height = 560;
            MinWidth = 480;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            PlatformWindowChrome.Apply(this);

            _box = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("Menlo, Consolas, monospace"),
                FontSize = 12,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Text = string.Join(Environment.NewLine, LogBuffer.Snapshot()),
            };
            _scroll = new ScrollViewer { Content = _box, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

            var clear = new Button { Content = "Clear", Padding = new Thickness(12, 6) };
            clear.Click += (_, _) => { LogBuffer.Clear(); _box.Text = ""; };
            var copy = new Button { Content = "Copy all", Padding = new Thickness(12, 6) };
            copy.Click += async (_, _) => { try { if (Clipboard != null) await Clipboard.SetTextAsync(_box.Text ?? ""); } catch { } };
            var openFolder = new Button { Content = "Open log folder", Padding = new Thickness(12, 6) };
            openFolder.Click += (_, _) =>
            {
                try
                {
                    var dir = AppDataHelper.GetLogsDirectory();
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch (Exception ex) { AppLog.Log($"[LogWindow] open folder: {ex.Message}"); }
            };
            var autoScroll = new CheckBox { Content = "Auto-scroll", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
            autoScroll.IsCheckedChanged += (_, _) => _autoScroll = autoScroll.IsChecked == true;

            Content = new Grid
            {
                Margin = new Thickness(12),
                RowDefinitions = new RowDefinitions("Auto,*"),
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8), Children = { clear, copy, openFolder, autoScroll }, [Grid.RowProperty] = 0 },
                    new Border { Child = _scroll, [Grid.RowProperty] = 1 },
                }
            };

            LogBuffer.LineAdded += OnLine;
            Closed += (_, _) => LogBuffer.LineAdded -= OnLine;
        }

        private void OnLine(string line)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _box.Text = string.IsNullOrEmpty(_box.Text) ? line : _box.Text + Environment.NewLine + line;
                if (_autoScroll) _scroll.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>Ring buffer behind <see cref="AppLog.UiSink"/> so the log window can open late and still show history.</summary>
    public static class LogBuffer
    {
        private const int MaxLines = 4000;
        private static readonly LinkedList<string> _lines = new();
        private static readonly object _gate = new();
        private static bool _attached;

        public static event Action<string>? LineAdded;

        public static void Attach()
        {
            lock (_gate)
            {
                if (_attached) return;
                _attached = true;
            }
            var previous = AppLog.UiSink;
            AppLog.UiSink = line =>
            {
                try { previous?.Invoke(line); } catch { }
                Add(line);
            };
        }

        private static void Add(string line)
        {
            string stamped = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            lock (_gate)
            {
                _lines.AddLast(stamped);
                while (_lines.Count > MaxLines) _lines.RemoveFirst();
            }
            try { LineAdded?.Invoke(stamped); } catch { }
        }

        public static IReadOnlyList<string> Snapshot()
        {
            lock (_gate) return new List<string>(_lines);
        }

        public static void Clear()
        {
            lock (_gate) _lines.Clear();
        }
    }
}
