// Spicetify Updater - native WPF app (no Electron, no Node dependency).
// Targets .NET Framework 4.8, which ships with Windows 10/11.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpicetifyUpdater
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--selftest")
            {
                File.WriteAllText(args[1], Spicetify.SelfTestJson());
                return;
            }

            // Renders the window to a PNG for visual checks without a desktop session.
            if (args.Length >= 2 && args[0] == "--render")
            {
                var trace = Path.ChangeExtension(args[1], ".log");
                File.WriteAllText(trace, "starting\n");
                var renderApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var window = new MainWindow();
                double renderWidth, renderHeight;
                if (args.Length >= 4 &&
                    double.TryParse(args[2], out renderWidth) &&
                    double.TryParse(args[3], out renderHeight))
                {
                    window.Width = renderWidth;
                    window.Height = renderHeight;
                }
                window.Show();
                File.AppendAllText(trace, "shown\n");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    try
                    {
                        File.AppendAllText(trace, "tick\n");
                        var root = (FrameworkElement)window.Content;
                        window.UpdateLayout();
                        root.UpdateLayout();
                        var pageElement = (FrameworkElement)((Grid)root).Children[0];
                        var footerElement = (FrameworkElement)((Grid)pageElement).Children[4];
                        File.AppendAllText(trace, string.Format(
                            "window {0}x{1} root {2}x{3} page {4}x{5} footerH {6} pageBottom {7}\n",
                            window.ActualWidth, window.ActualHeight,
                            root.ActualWidth, root.ActualHeight,
                            pageElement.ActualWidth, pageElement.ActualHeight,
                            footerElement.ActualHeight,
                            pageElement.TranslatePoint(new Point(0, pageElement.ActualHeight), root).Y));
                        File.AppendAllText(trace, string.Format(
                            "footer {0}x{1} at ({2},{3})\n",
                            footerElement.ActualWidth, footerElement.ActualHeight,
                            footerElement.TranslatePoint(new Point(0, 0), root).X,
                            footerElement.TranslatePoint(new Point(0, 0), root).Y));
                        var bitmap = new RenderTargetBitmap(
                            Math.Max(1, (int)root.ActualWidth), Math.Max(1, (int)root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(root);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var stream = File.Create(args[1])) encoder.Save(stream);
                        File.AppendAllText(trace, "saved\n");
                    }
                    catch (Exception ex)
                    {
                        File.AppendAllText(trace, "ERR " + ex + "\n");
                    }
                    renderApp.Shutdown();
                };
                timer.Start();
                File.AppendAllText(trace, "timer started\n");
                renderApp.Run();
                File.AppendAllText(trace, "app exited\n");
                return;
            }

            // Headless self-update, used to verify the swap without opening a window.
            if (args.Length >= 1 && args[0] == "--selfupdate")
            {
                var logPath = args.Length >= 2
                    ? args[1]
                    : Path.Combine(Path.GetTempPath(), "spicetify-updater-selfupdate.log");
                Action<string> write = line => File.AppendAllText(logPath, line + Environment.NewLine);
                try
                {
                    write("current " + Spicetify.SelfVersion());
                    var tag = Spicetify.UpdateSelf(write, ratio => { });
                    write("OK " + tag);
                }
                catch (Exception ex)
                {
                    write("ERR " + ex.Message);
                    Environment.ExitCode = 1;
                }
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Run(new MainWindow());
        }
    }

    internal static class Theme
    {
        public static readonly Brush Background = Frozen("#08090A");
        public static readonly Brush CardTop = Frozen("#161818");
        public static readonly Brush CardBottom = Frozen("#101112");
        public static readonly Brush Line = Frozen("#262929");
        public static readonly Brush Text = Frozen("#F5F6F6");
        public static readonly Brush Muted = Frozen("#9AA0A0");
        public static readonly Brush Accent = Frozen("#1ED760");
        public static readonly Brush AccentHover = Frozen("#2BE96E");
        public static readonly Brush AccentPressed = Frozen("#16A349");
        public static readonly Brush Danger = Frozen("#F2596B");
        public static readonly Brush LogBackground = Frozen("#0A0B0B");
        public static readonly Brush Disabled = Frozen("#2A2E2E");

        public static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
    }

    internal sealed class MainWindow : Window
    {
        private readonly TextBlock _cliInstalled;
        private readonly TextBlock _cliLatest;
        private readonly TextBlock _cliPath;
        private readonly Border _cliPill;
        private readonly TextBlock _cliPillText;
        private readonly Button _cliPrimary;
        private readonly Button _cliSecondary;

        private readonly TextBlock _mpInstalled;
        private readonly TextBlock _mpLatest;
        private readonly TextBlock _mpPath;
        private readonly Border _mpPill;
        private readonly TextBlock _mpPillText;
        private readonly Button _mpPrimary;
        private readonly Button _mpSecondary;
        private readonly CheckBox _mpTheme;

        private readonly CheckBox _closeSpotify;
        private readonly Button _repair;
        private readonly Button _backupApply;
        private readonly Button _apply;
        private readonly Button _restore;
        private readonly Button _refresh;

        private readonly RichTextBox _log;
        private readonly ProgressBar _progress;
        private readonly TextBlock _state;

        private Status _status;
        private bool _busy;

        public MainWindow()
        {
            Title = "Spicetify Updater";
            Width = 1040;
            Height = 820;
            // Below this the maintenance text runs under its buttons and the
            // activity log has no height left at all.
            MinWidth = 900;
            MinHeight = 720;
            Background = Theme.Background;
            Foreground = Theme.Text;
            FontFamily = new FontFamily("Segoe UI");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Icon = AppIcon.Load();
            UseLayoutRounding = true;

            /* header */
            var title = new TextBlock
            {
                Text = "Spicetify Updater",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text
            };
            var subtitle = new TextBlock
            {
                Text = "Install and keep the Spicetify CLI and Marketplace up to date",
                FontSize = 12.5,
                Foreground = Theme.Muted,
                Margin = new Thickness(0, 3, 0, 0)
            };

            var mark = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(21),
                Background = Theme.Accent,
                Child = new TextBlock
                {
                    Text = "≡",
                    FontSize = 22,
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.Frozen("#06120A"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -4, 0, 0)
                }
            };

            var brandText = new StackPanel { Margin = new Thickness(13, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            brandText.Children.Add(title);
            brandText.Children.Add(subtitle);

            var brand = new StackPanel { Orientation = Orientation.Horizontal };
            brand.Children.Add(mark);
            brand.Children.Add(brandText);

            _refresh = Ui.MakeButton("Check for updates", false);
            _refresh.Click += async (s, e) => await RefreshStatusAsync(true);

            var header = new Grid { Margin = new Thickness(0, 0, 0, 22) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(brand, 0);
            Grid.SetColumn(_refresh, 1);
            _refresh.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(brand);
            header.Children.Add(_refresh);

            /* CLI card */
            _cliInstalled = Ui.Value();
            _cliLatest = Ui.Value();
            _cliPath = Ui.PathLabel();
            _cliPillText = Ui.PillText();
            _cliPill = Ui.Pill(_cliPillText);
            _cliPrimary = Ui.MakeButton("Install", true);
            _cliSecondary = Ui.MakeButton("Reinstall", false);
            _cliPrimary.Click += async (s, e) => await RunActionAsync(_cliPrimary.Tag as string, true);
            _cliSecondary.Click += async (s, e) => await RunActionAsync("install-cli", true);

            var cliCard = Ui.Card(Ui.CardContent(
                "\u25B8_", "Spicetify CLI", _cliPill,
                new[] { ("Installed", _cliInstalled), ("Latest release", _cliLatest) },
                _cliPath,
                null,
                _cliPrimary, _cliSecondary));

            /* Marketplace card */
            _mpInstalled = Ui.Value();
            _mpLatest = Ui.Value();
            _mpPath = Ui.PathLabel();
            _mpPillText = Ui.PillText();
            _mpPill = Ui.Pill(_mpPillText);
            _mpPrimary = Ui.MakeButton("Install", true);
            _mpSecondary = Ui.MakeButton("Reinstall", false);
            _mpTheme = Ui.Check("Set Marketplace as the current theme", true);
            _mpPrimary.Click += async (s, e) => await RunActionAsync(_mpPrimary.Tag as string, true);
            _mpSecondary.Click += async (s, e) => await RunActionAsync("install-marketplace", true);

            var mpCard = Ui.Card(Ui.CardContent(
                "\u25A2", "Marketplace", _mpPill,
                new[] { ("Installed", _mpInstalled), ("Latest release", _mpLatest) },
                _mpPath,
                _mpTheme,
                _mpPrimary, _mpSecondary));

            var cards = new Grid();
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(cliCard, 0);
            Grid.SetColumn(mpCard, 1);
            cliCard.Margin = new Thickness(0, 0, 8, 0);
            mpCard.Margin = new Thickness(8, 0, 0, 0);
            cards.Children.Add(cliCard);
            cards.Children.Add(mpCard);

            /* maintenance */
            _closeSpotify = Ui.Check("Close Spotify automatically, then reopen it", true);
            _repair = Ui.MakeButton("Repair", false);
            _backupApply = Ui.MakeButton("Backup & Apply", false);
            _apply = Ui.MakeButton("Apply", false);
            _restore = Ui.MakeButton("Restore Spotify", false);
            _repair.Click += async (s, e) => await RunActionAsync("repair", _closeSpotify.IsChecked == true);
            _backupApply.Click += async (s, e) => await RunActionAsync("backup-apply", _closeSpotify.IsChecked == true);
            _apply.Click += async (s, e) => await RunActionAsync("apply", _closeSpotify.IsChecked == true);
            _restore.Click += async (s, e) => await RunActionAsync("restore", _closeSpotify.IsChecked == true);

            var toolsLeft = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            toolsLeft.Children.Add(new TextBlock
            {
                Text = "Maintenance",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text
            });
            toolsLeft.Children.Add(new TextBlock
            {
                Text = "Repair reinstalls the full CLI package, restores Spotify and re-applies.",
                FontSize = 12.5,
                Foreground = Theme.Muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 12, 0)
            });
            toolsLeft.Children.Add(_closeSpotify);

            var toolsRight = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            foreach (var b in new[] { _repair, _backupApply, _apply, _restore })
            {
                b.Margin = new Thickness(8, 0, 0, 0);
                toolsRight.Children.Add(b);
            }

            var tools = new Grid();
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tools.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(toolsLeft, 0);
            Grid.SetColumn(toolsRight, 1);
            tools.Children.Add(toolsLeft);
            tools.Children.Add(toolsRight);
            var toolsCard = Ui.Card(tools);
            toolsCard.Margin = new Thickness(0, 16, 0, 0);

            /* console */
            _state = new TextBlock
            {
                Text = "IDLE",
                FontSize = 11,
                Foreground = Theme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var clear = Ui.MakeButton("Clear", false);
            clear.Padding = new Thickness(13, 5, 13, 5);
            clear.FontSize = 12;
            clear.Click += (s, e) =>
            {
                _log.Document.Blocks.Clear();
                AppendLog("Ready. Nothing has been changed yet.", "muted");
            };

            var consoleHead = new Grid { Margin = new Thickness(0, 0, 0, 11) };
            consoleHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            consoleHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headText = new TextBlock { Text = "Activity", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Theme.Text };
            var headRight = new StackPanel { Orientation = Orientation.Horizontal };
            headRight.Children.Add(_state);
            headRight.Children.Add(clear);
            Grid.SetColumn(headText, 0);
            Grid.SetColumn(headRight, 1);
            consoleHead.Children.Add(headText);
            consoleHead.Children.Add(headRight);

            _progress = new ProgressBar
            {
                Height = 3,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Foreground = Theme.Accent,
                Background = Theme.Frozen("#1A1D1D"),
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 11),
                Visibility = Visibility.Hidden
            };

            _log = new RichTextBox
            {
                IsReadOnly = true,
                Background = Theme.LogBackground,
                Foreground = Theme.Frozen("#8A9290"),
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Frozen("#1D2020"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                Padding = new Thickness(10, 8, 10, 8),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 96,
                IsDocumentEnabled = false
            };
            _log.Document.PagePadding = new Thickness(0);

            // A star row lets the log fill whatever height the window gives the card,
            // instead of leaving dead space under a fixed-height text box.
            var consoleBody = new Grid();
            consoleBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            consoleBody.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            consoleBody.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(consoleHead, 0);
            Grid.SetRow(_progress, 1);
            Grid.SetRow(_log, 2);
            consoleBody.Children.Add(consoleHead);
            consoleBody.Children.Add(_progress);
            consoleBody.Children.Add(_log);
            var consoleCard = Ui.Card(consoleBody);
            consoleCard.Margin = new Thickness(0, 16, 0, 0);

            var env = new TextBlock
            {
                Text = "checking…",
                FontSize = 11,
                Foreground = Theme.Frozen("#5F6665"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center
            };
            _envLabel = env;

            _selfLabel = new TextBlock
            {
                Text = "Updater v" + Spicetify.SelfVersion(),
                FontSize = 11,
                Foreground = Theme.Frozen("#5F6665"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            _selfButton = Ui.MakeButton("Update app", true);
            _selfButton.Padding = new Thickness(12, 4, 12, 4);
            _selfButton.FontSize = 11.5;
            _selfButton.Visibility = Visibility.Collapsed;
            _selfButton.Click += async (s, e) => await RunActionAsync("update-self", false);

            var footer = new Grid { Margin = new Thickness(2, 14, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var selfPanel = new StackPanel { Orientation = Orientation.Horizontal };
            selfPanel.Children.Add(_selfLabel);
            selfPanel.Children.Add(_selfButton);
            Grid.SetColumn(env, 0);
            Grid.SetColumn(selfPanel, 1);
            footer.Children.Add(env);
            footer.Children.Add(selfPanel);

            /* root */
            var root = new Grid { Margin = new Thickness(0), Background = Theme.Background };
            var page = new Grid { Margin = new Thickness(26, 26, 26, 22) };
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(header, 0);
            Grid.SetRow(cards, 1);
            Grid.SetRow(toolsCard, 2);
            Grid.SetRow(consoleCard, 3);
            Grid.SetRow(footer, 4);
            page.Children.Add(header);
            page.Children.Add(cards);
            page.Children.Add(toolsCard);
            page.Children.Add(consoleCard);
            page.Children.Add(footer);

            root.Children.Add(page);
            Content = root;

            AppendLog("Ready. Nothing has been changed yet.", "muted");
            Loaded += async (s, e) => await RefreshStatusAsync(false);
        }

        private readonly TextBlock _envLabel;
        private readonly TextBlock _selfLabel;
        private readonly Button _selfButton;

        /* ------------------------------------------------------------- logging */

        private void AppendLog(string text, string kind = "dim")
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendLog(text, kind)));
                return;
            }

            Brush brush;
            switch (kind)
            {
                case "success": brush = Theme.Accent; break;
                case "error": brush = Theme.Danger; break;
                case "heading": brush = Theme.Frozen("#D7DBDB"); break;
                case "muted": brush = Theme.Frozen("#5D6463"); break;
                default: brush = Theme.Frozen("#7C8482"); break;
            }

            var paragraph = new Paragraph(new Run(string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), text)))
            {
                Foreground = brush,
                Margin = new Thickness(0, 0, 0, 2),
                FontWeight = kind == "heading" ? FontWeights.SemiBold : FontWeights.Normal
            };
            _log.Document.Blocks.Add(paragraph);
            _log.ScrollToEnd();
        }

        private void SetBusy(bool busy, string label = "Working…")
        {
            _busy = busy;
            foreach (var button in new[] { _cliPrimary, _cliSecondary, _mpPrimary, _mpSecondary, _repair, _backupApply, _apply, _restore, _selfButton })
                button.IsEnabled = !busy;

            _state.Text = busy ? label.ToUpperInvariant() : "IDLE";
            _state.Foreground = busy ? Theme.Accent : Theme.Muted;
            _refresh.IsEnabled = !busy;

            if (!busy)
            {
                _progress.Visibility = Visibility.Hidden;
                _progress.Value = 0;
                Render(_status);
            }
        }

        /* -------------------------------------------------------------- actions */

        private async Task RunActionAsync(string action, bool closeSpotify)
        {
            if (_busy || string.IsNullOrEmpty(action)) return;

            SetBusy(true, "starting…");
            var result = await Task.Run(() =>
            {
                var wasRunning = false;
                try
                {
                    if (closeSpotify)
                    {
                        wasRunning = Spicetify.IsSpotifyRunning();
                        if (wasRunning)
                        {
                            AppendLog("Closing Spotify", "dim");
                            if (!Spicetify.StopSpotify()) AppendLog("Spotify is still running – file changes may fail", "error");
                        }
                        else
                        {
                            AppendLog("Spotify is not running", "dim");
                        }
                    }

                    switch (action)
                    {
                        case "install-cli":
                        case "update-cli":
                            AppendLog("Starting Spicetify CLI " + (action == "update-cli" ? "update" : "install"), "heading");
                            return Spicetify.InstallCli(Log, Progress);
                        case "install-marketplace":
                        case "update-marketplace":
                            AppendLog("Starting Marketplace " + (action == "update-marketplace" ? "update" : "install"), "heading");
                            return Spicetify.InstallMarketplace(Log, Progress, _mpTheme.IsChecked == true);
                        case "repair":
                            AppendLog("Repairing the Spicetify install", "heading");
                            return Spicetify.Repair(Log, Progress);
                        case "apply":
                            AppendLog("Running spicetify apply", "heading");
                            return Spicetify.RunCommand(new[] { "apply" }, Log, false);
                        case "backup-apply":
                            AppendLog("Running spicetify backup", "heading");
                            Spicetify.RunCommand(new[] { "backup" }, Log, true);
                            AppendLog("Running spicetify apply", "heading");
                            return Spicetify.RunCommand(new[] { "apply" }, Log, false);
                        case "restore":
                            AppendLog("Running spicetify restore", "heading");
                            return Spicetify.RunCommand(new[] { "restore" }, Log, false);
                        case "update-self":
                            AppendLog("Updating Spicetify Updater itself", "heading");
                            return Spicetify.UpdateSelf(Log, Progress);
                        default:
                            throw new InvalidOperationException("Unknown action: " + action);
                    }
                }
                finally
                {
                    if (wasRunning)
                    {
                        if (Spicetify.StartSpotify())
                        {
                            AppendLog("Reopened Spotify", "dim");
                            System.Threading.Thread.Sleep(1200);
                        }
                        else
                        {
                            AppendLog("Could not reopen Spotify – start it yourself", "error");
                        }
                    }
                }
            }).ContinueWith(t => t, TaskScheduler.FromCurrentSynchronizationContext());

            if (result.IsFaulted)
            {
                var error = result.Exception?.GetBaseException();
                AppendLog(error?.Message ?? "Unknown error", "error");
            }
            else
            {
                AppendLog("Done", "success");
                if (action == "update-self")
                {
                    Application.Current.Shutdown();
                    return;
                }
            }

            SetBusy(false);
            await RefreshStatusAsync(true);
        }

        private void Log(string line)
        {
            var level = line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 ? "error" : "dim";
            AppendLog(line.TrimEnd(), level);
        }

        private void Progress(double ratio)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Progress(ratio)));
                return;
            }

            if (double.IsNaN(ratio))
            {
                _progress.Visibility = Visibility.Hidden;
                return;
            }

            _progress.Visibility = Visibility.Visible;
            _progress.Value = Math.Max(1, Math.Min(100, ratio * 100));
        }

        /* --------------------------------------------------------------- status */

        private async Task RefreshStatusAsync(bool force)
        {
            _refresh.IsEnabled = false;
            try
            {
                _status = await Task.Run(() => Spicetify.GetStatus(force));
                Render(_status);
            }
            catch (Exception ex)
            {
                AppendLog("Could not read status: " + ex.Message, "error");
            }
            finally
            {
                _refresh.IsEnabled = !_busy;
            }
        }

        private static string VersionText(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : "v" + value;
        }

        private void Render(Status status)
        {
            if (status == null) return;

            _cliInstalled.Text = status.CliInstalled ? VersionText(status.CliVersion ?? "?") : "Not installed";
            _cliLatest.Text = VersionText(status.CliLatest);
            _cliPath.Text = status.CliInstalled ? status.CliPath : "";
            _cliPath.ToolTip = _cliPath.Text;

            if (!status.CliInstalled)
            {
                Ui.SetPill(_cliPill, _cliPillText, "Not installed", "warn");
                _cliPrimary.Content = "Install CLI";
                _cliPrimary.Tag = "install-cli";
                _cliPrimary.IsEnabled = !_busy;
                _cliPrimary.Visibility = Visibility.Visible;
                _cliSecondary.Visibility = Visibility.Collapsed;
            }
            else if (status.CliUpdate)
            {
                Ui.SetPill(_cliPill, _cliPillText, "Update available", "update");
                _cliPrimary.Content = "Update to " + VersionText(status.CliLatest);
                _cliPrimary.Tag = "update-cli";
                _cliPrimary.IsEnabled = !_busy;
                _cliPrimary.Visibility = Visibility.Visible;
                _cliSecondary.Visibility = Visibility.Visible;
                _cliSecondary.IsEnabled = !_busy;
            }
            else
            {
                Ui.SetPill(_cliPill, _cliPillText, "Up to date", "ok");
                _cliPrimary.Content = "Up to date";
                _cliPrimary.Tag = null;
                _cliPrimary.IsEnabled = false;
                _cliPrimary.Visibility = Visibility.Collapsed;
                _cliSecondary.Visibility = Visibility.Visible;
                _cliSecondary.IsEnabled = !_busy;
            }

            _mpInstalled.Text = status.MarketplaceInstalled
                ? (string.IsNullOrEmpty(status.MarketplaceVersion) ? "Installed" : VersionText(status.MarketplaceVersion))
                : "Not installed";
            _mpLatest.Text = VersionText(status.MarketplaceLatest);
            _mpPath.Text = status.MarketplaceInstalled ? status.MarketplacePath : "";
            _mpPath.ToolTip = _mpPath.Text;

            if (!status.CliInstalled)
            {
                Ui.SetPill(_mpPill, _mpPillText, "Needs the CLI", "warn");
                _mpPrimary.Content = "Install the CLI first";
                _mpPrimary.Tag = null;
                _mpPrimary.IsEnabled = false;
                _mpPrimary.Visibility = Visibility.Visible;
                _mpSecondary.Visibility = Visibility.Collapsed;
            }
            else if (!status.MarketplaceInstalled)
            {
                Ui.SetPill(_mpPill, _mpPillText, "Not installed", "warn");
                _mpPrimary.Content = "Install Marketplace";
                _mpPrimary.Tag = "install-marketplace";
                _mpPrimary.IsEnabled = !_busy;
                _mpPrimary.Visibility = Visibility.Visible;
                _mpSecondary.Visibility = Visibility.Collapsed;
            }
            else if (status.MarketplaceUpdate)
            {
                Ui.SetPill(_mpPill, _mpPillText, "Update available", "update");
                _mpPrimary.Content = "Update to " + VersionText(status.MarketplaceLatest);
                _mpPrimary.Tag = "update-marketplace";
                _mpPrimary.IsEnabled = !_busy;
                _mpPrimary.Visibility = Visibility.Visible;
                _mpSecondary.Visibility = Visibility.Visible;
                _mpSecondary.IsEnabled = !_busy;
            }
            else
            {
                Ui.SetPill(_mpPill, _mpPillText, "Up to date", "ok");
                _mpPrimary.Content = "Up to date";
                _mpPrimary.Tag = null;
                _mpPrimary.IsEnabled = false;
                _mpPrimary.Visibility = Visibility.Collapsed;
                _mpSecondary.Visibility = Visibility.Visible;
                _mpSecondary.IsEnabled = !_busy;
            }

            var spotify = status.SpotifyFound ? "Spotify found" : "Spotify not detected";
            var onPath = status.OnPath ? "on PATH" : "not on PATH";
            _envLabel.Text = string.Format("{0}-{1} · {2} · {3}", status.Platform, status.Arch, spotify, onPath);

            var selfText = "Updater v" + (status.SelfVersion ?? "?");
            if (!string.IsNullOrEmpty(status.SelfLatest))
                selfText += status.SelfUpdate ? " · v" + status.SelfLatest + " available" : " · up to date";
            _selfLabel.Text = selfText;
            _selfButton.Visibility = status.SelfUpdate ? Visibility.Visible : Visibility.Collapsed;
            if (status.SelfUpdate) _selfButton.Content = "Update to v" + status.SelfLatest;

            if (!string.IsNullOrEmpty(status.ReleaseError))
                AppendLog("GitHub check failed: " + status.ReleaseError, "error");
        }
    }

    /* ------------------------------------------------------------------ icon */

    internal static class AppIcon
    {
        public static ImageSource Load()
        {
            var exe = System.Reflection.Assembly.GetExecutingAssembly().Location;

            // A 64px PNG is embedded in the assembly, so the title bar, taskbar and
            // Alt-Tab all scale down from a sharp frame instead of a 32px one.
            try
            {
                var resource = Application.GetResourceStream(new Uri("pack://application:,,,/build/icon64.png"));
                if (resource != null)
                {
                    using (var stream = resource.Stream)
                    {
                        var frame = BitmapFrame.Create(
                            stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        frame.Freeze();
                        return frame;
                    }
                }
            }
            catch
            {
                // fall through to the exe icon
            }

            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe))
                using (var stream = new MemoryStream())
                {
                    if (icon == null) return null;
                    icon.Save(stream);
                    stream.Position = 0;
                    return System.Windows.Media.Imaging.BitmapFrame.Create(stream);
                }
            }
            catch
            {
                return null;
            }
        }
    }

    /* -------------------------------------------------------------- UI helpers */

    internal static class Ui
    {
        public static Border Card(UIElement content)
        {
            return new Border
            {
                Background = Theme.CardTop,
                BorderBrush = Theme.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18),
                Child = content
            };
        }

        public static Grid CardContent(
            string glyph,
            string title,
            Border pill,
            (string Label, TextBlock Value)[] meta,
            TextBlock path,
            CheckBox check,
            Button primary,
            Button secondary)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new Border
            {
                Width = 38,
                Height = 38,
                CornerRadius = new CornerRadius(11),
                Background = Theme.Frozen("#1B2A20"),
                BorderBrush = Theme.Frozen("#28402F"),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = 15,
                    FontFamily = new FontFamily("Segoe UI Symbol"),
                    Foreground = Theme.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text,
                Margin = new Thickness(0, 0, 0, 5)
            });
            titles.Children.Add(pill);
            head.Children.Add(titles);

            var metaGrid = new Grid { Margin = new Thickness(0, 18, 0, 10) };
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition());
            metaGrid.ColumnDefinitions.Add(new ColumnDefinition());
            for (var i = 0; i < meta.Length && i < 2; i++)
            {
                var column = new StackPanel();
                column.Children.Add(new TextBlock
                {
                    Text = meta[i].Label.ToUpperInvariant(),
                    FontSize = 10.5,
                    Foreground = Theme.Muted,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                column.Children.Add(meta[i].Value);
                Grid.SetColumn(column, i);
                metaGrid.Children.Add(column);
            }

            var buttons = new WrapPanel();
            primary.Margin = new Thickness(0, 0, 9, 0);
            buttons.Children.Add(primary);
            buttons.Children.Add(secondary);

            buttons.Margin = new Thickness(0, 6, 0, 0);

            // The star spacer above the buttons keeps both cards' action rows on the
            // same line even though Marketplace carries one extra control.
            var stack = new Grid();
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(head, 0);
            Grid.SetRow(metaGrid, 1);
            Grid.SetRow(path, 2);
            Grid.SetRow(buttons, 5);
            stack.Children.Add(head);
            stack.Children.Add(metaGrid);
            stack.Children.Add(path);
            if (check != null)
            {
                check.Margin = new Thickness(0, 0, 0, 4);
                Grid.SetRow(check, 3);
                stack.Children.Add(check);
            }
            stack.Children.Add(buttons);
            return stack;
        }

        public static TextBlock Value()
        {
            return new TextBlock
            {
                Text = "—",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Text
            };
        }

        public static TextBlock PathLabel()
        {
            return new TextBlock
            {
                Text = "",
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = Theme.Frozen("#6F7675"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 0, 8),
                Height = 15
            };
        }

        public static TextBlock PillText()
        {
            return new TextBlock { Text = "Checking…", FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        }

        public static Border Pill(TextBlock text)
        {
            var border = new Border
            {
                Background = Theme.Frozen("#1E2222"),
                // Half of the ~20px height: WPF draws a far-too-large radius as a
                // bent lens at the ends instead of a clean pill.
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 8, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = text
            };
            text.Foreground = Theme.Muted;
            return border;
        }

        public static void SetPill(Border border, TextBlock text, string label, string kind)
        {
            text.Text = label.ToUpperInvariant();
            switch (kind)
            {
                case "ok":
                    border.Background = Theme.Accent;
                    text.Foreground = Theme.Frozen("#06120A");
                    break;
                case "update":
                    border.Background = Theme.Frozen("#57C8FF");
                    text.Foreground = Theme.Frozen("#041018");
                    break;
                case "warn":
                    border.Background = Theme.Frozen("#33291A");
                    text.Foreground = Theme.Frozen("#FFD7A8");
                    break;
                default:
                    border.Background = Theme.Frozen("#1E2222");
                    text.Foreground = Theme.Muted;
                    break;
            }
        }

        public static CheckBox Check(string label, bool isChecked)
        {
            var check = new CheckBox
            {
                // A wrapping label, so a narrow window shortens nothing.
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                IsChecked = isChecked,
                Foreground = Theme.Frozen("#A8AEB2"),
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            return check;
        }

        public static Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Content = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Padding = new Thickness(17, 8, 17, 8),
                Cursor = Cursors.Hand,
                SnapsToDevicePixels = true
            };

            var border = new FrameworkElementFactory(typeof(Border), "bg");
            // Half of the ~35px button height. Anything much larger (999) makes WPF
            // bend the stroke into a doubled arc at the left and right ends.
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(17));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.BackgroundProperty, primary ? Theme.Accent : Brushes.Transparent);
            border.SetValue(Border.BorderBrushProperty, primary ? Theme.Accent : Theme.Frozen("#343938"));
            border.SetValue(Border.PaddingProperty, new Thickness(17, 8, 17, 8));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            if (primary)
            {
                button.Foreground = Theme.Frozen("#06120A");
                template.Triggers.Add(Style(border, Border.BackgroundProperty, Theme.AccentHover, Button.IsMouseOverProperty));
                template.Triggers.Add(Style(border, Border.BackgroundProperty, Theme.AccentPressed, Button.IsPressedProperty));
                template.Triggers.Add(Style(border, Border.BackgroundProperty, Theme.Frozen("#2C3333"), Button.IsEnabledProperty, false));
            }
            else
            {
                button.Foreground = Theme.Text;
                template.Triggers.Add(Style(border, Border.BackgroundProperty, Theme.Frozen("#1C2020"), Button.IsMouseOverProperty));
                template.Triggers.Add(Style(border, Border.BorderBrushProperty, Theme.Frozen("#5B6362"), Button.IsMouseOverProperty));
                template.Triggers.Add(Style(border, Border.BackgroundProperty, Theme.Frozen("#262B2B"), Button.IsPressedProperty));
            }

            var disabled = new Trigger { Property = Button.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Button.OpacityProperty, 0.45));
            template.Triggers.Add(disabled);

            button.Template = template;
            return button;
        }

        private static Trigger Style(FrameworkElementFactory target, DependencyProperty property, Brush value, DependencyProperty triggerProperty, bool? triggerValue = null)
        {
            var trigger = new Trigger { Property = triggerProperty };
            if (triggerValue.HasValue) trigger.Value = triggerValue.Value;
            else if (triggerProperty == Button.IsEnabledProperty) trigger.Value = false;
            else trigger.Value = true;

            var setter = new Setter(property, value) { TargetName = "bg" };
            trigger.Setters.Add(setter);
            return trigger;
        }
    }

    /* ---------------------------------------------------------------- domain */

    internal sealed class Status
    {
        public bool CliInstalled;
        public string CliVersion;
        public string CliLatest;
        public bool CliUpdate;
        public string CliPath;

        public bool MarketplaceInstalled;
        public string MarketplaceVersion;
        public string MarketplaceLatest;
        public bool MarketplaceUpdate;
        public string MarketplacePath;

        public bool SpotifyFound;
        public bool OnPath;
        public string Platform;
        public string Arch;
        public string ReleaseError;

        public string SelfVersion;
        public string SelfLatest;
        public bool SelfUpdate;
    }

    internal static class Spicetify
    {
        private const string CliRepo = "spicetify/cli";
        private const string MarketRepo = "spicetify/marketplace";
        private const string SelfRepo = "thecloudyy/SpicetifyUpdater";
        private const string ColorsUrl = "https://raw.githubusercontent.com/spicetify/marketplace/main/resources/color.ini";
        private const string UserAgent = "SpicetifyUpdater/2.0 (+https://spicetify.app)";

        private static readonly Dictionary<string, Release> ReleaseCache = new Dictionary<string, Release>();
        private static DateTime _cacheTime = DateTime.MinValue;

        public static string CliRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "spicetify"); }
        }

        public static string CliExe
        {
            get { return Path.Combine(CliRoot, "spicetify.exe"); }
        }

        public static string LegacyRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".spicetify"); }
        }

        public static string InstalledExe()
        {
            if (File.Exists(CliExe)) return CliExe;
            var legacy = Path.Combine(LegacyRoot, "spicetify.exe");
            return File.Exists(legacy) ? legacy : null;
        }

        private static void EnsureTls()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /* --------------------------------------------------------- github lookups */

        private sealed class Release
        {
            public string Tag;
            public List<KeyValuePair<string, string>> Assets = new List<KeyValuePair<string, string>>();
        }

        private static async Task<Release> LatestReleaseAsync(string repo, bool force)
        {
            EnsureTls();
            if (!force && ReleaseCache.ContainsKey(repo) && (DateTime.Now - _cacheTime).TotalMinutes < 5)
                return ReleaseCache[repo];

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var json = await client.GetStringAsync("https://api.github.com/repos/" + repo + "/releases/latest");
                var data = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);

                var release = new Release { Tag = Convert.ToString(data["tag_name"]).TrimStart('v') };
                foreach (var item in (object[])data["assets"])
                {
                    var asset = (Dictionary<string, object>)item;
                    release.Assets.Add(new KeyValuePair<string, string>(
                        Convert.ToString(asset["name"]),
                        Convert.ToString(asset["browser_download_url"])));
                }

                ReleaseCache[repo] = release;
                _cacheTime = DateTime.Now;
                return release;
            }
        }

        private static Release LatestRelease(string repo, bool force)
        {
            return LatestReleaseAsync(repo, force).GetAwaiter().GetResult();
        }

        /* -------------------------------------------------------------- status */

        public static Status GetStatus(bool force)
        {
            var status = new Status
            {
                Platform = "win32",
                Arch = Environment.Is64BitOperatingSystem ? "x64" : "x32"
            };

            var exe = InstalledExe();
            status.CliInstalled = exe != null;
            status.CliPath = exe ?? "";
            status.OnPath = IsOnUserPath();
            status.SpotifyFound = SpotifyExe() != null;

            if (exe != null)
            {
                var versionOut = new StringBuilder();
                RunProcess(exe, "-v", line => versionOut.AppendLine(line), 30000);
                status.CliVersion = ParseVersion(versionOut.ToString());
            }

            var userdata = UserDataDirectory();
            status.MarketplacePath = Path.Combine(userdata, "CustomApps", "marketplace");
            status.MarketplaceInstalled = File.Exists(Path.Combine(status.MarketplacePath, "manifest.json"));
            if (status.MarketplaceInstalled) status.MarketplaceVersion = ReadMarker(userdata);

            try
            {
                var cli = LatestRelease(CliRepo, force);
                status.CliLatest = cli.Tag;
                status.CliUpdate = status.CliVersion != null && Compare(status.CliLatest, status.CliVersion) > 0;
            }
            catch (Exception ex)
            {
                status.ReleaseError = ex.Message;
            }

            try
            {
                var market = LatestRelease(MarketRepo, force);
                status.MarketplaceLatest = market.Tag;
                status.MarketplaceUpdate = status.MarketplaceInstalled &&
                    Compare(status.MarketplaceLatest, status.MarketplaceVersion ?? "0") > 0;
            }
            catch (Exception ex)
            {
                if (status.ReleaseError == null) status.ReleaseError = ex.Message;
            }

            status.SelfVersion = SelfVersion();
            try
            {
                var self = LatestRelease(SelfRepo, force);
                status.SelfLatest = self.Tag;
                status.SelfUpdate = Compare(status.SelfLatest, status.SelfVersion) > 0;
            }
            catch (Exception ex)
            {
                if (status.ReleaseError == null) status.ReleaseError = ex.Message;
            }

            return status;
        }

        public static string SelfVersion()
        {
            var version = typeof(Spicetify).Assembly.GetName().Version;
            return version == null ? "0.0.0" : string.Format("{0}.{1}.{2}", version.Major, version.Minor, version.Build);
        }

        public static string SelfTestJson()
        {
            var s = GetStatus(true);
            var serializer = new JavaScriptSerializer();
            return serializer.Serialize(new Dictionary<string, object>
            {
                { "cliInstalled", s.CliInstalled },
                { "cliVersion", s.CliVersion },
                { "cliLatest", s.CliLatest },
                { "cliUpdate", s.CliUpdate },
                { "cliPath", s.CliPath },
                { "marketplaceInstalled", s.MarketplaceInstalled },
                { "marketplaceVersion", s.MarketplaceVersion },
                { "marketplaceLatest", s.MarketplaceLatest },
                { "marketplaceUpdate", s.MarketplaceUpdate },
                { "marketplacePath", s.MarketplacePath },
                { "spotifyFound", s.SpotifyFound },
                { "onPath", s.OnPath },
                { "releaseError", s.ReleaseError },
                { "selfVersion", s.SelfVersion },
                { "selfLatest", s.SelfLatest },
                { "selfUpdate", s.SelfUpdate }
            });
        }

        private static string ParseVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\.(\d+)\.(\d+)");
            return match.Success ? match.Value : null;
        }

        private static int Compare(string a, string b)
        {
            var left = (a ?? "0").Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
            var right = (b ?? "0").Split('.').Select(p => int.TryParse(p, out var v) ? v : 0).ToArray();
            for (var i = 0; i < Math.Max(left.Length, right.Length); i++)
            {
                var l = i < left.Length ? left[i] : 0;
                var r = i < right.Length ? right[i] : 0;
                if (l != r) return l > r ? 1 : -1;
            }
            return 0;
        }

        private static bool IsOnUserPath()
        {
            var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            return path.Split(';').Any(p => p.Trim().TrimEnd('\\').Equals(CliRoot, StringComparison.OrdinalIgnoreCase));
        }

        private static string UserDataDirectory()
        {
            var exe = InstalledExe();
            if (exe != null)
            {
                var output = new StringBuilder();
                RunProcess(exe, "path userdata", line => output.AppendLine(line), 30000);
                var last = output.ToString().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .LastOrDefault(l => l.Length > 0);
                if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) return last;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "spicetify");
        }

        private static string ReadMarker(string userdata)
        {
            try
            {
                var marker = Path.Combine(userdata, "CustomApps", "marketplace", ".marketplace-version");
                return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /* ------------------------------------------------------------- spotify */

        public static string SpotifyExe()
        {
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spotify", "Spotify.exe");
            if (File.Exists(roaming)) return roaming;
            return null;
        }

        public static bool IsSpotifyRunning()
        {
            return Process.GetProcessesByName("Spotify").Length > 0;
        }

        public static bool StopSpotify()
        {
            foreach (var process in Process.GetProcessesByName("Spotify"))
            {
                try { process.Kill(); process.WaitForExit(5000); }
                catch { /* already gone */ }
            }

            for (var i = 0; i < 20; i++)
            {
                if (!IsSpotifyRunning()) return true;
                System.Threading.Thread.Sleep(150);
            }
            return !IsSpotifyRunning();
        }

        public static bool StartSpotify()
        {
            var exe = SpotifyExe();
            if (exe == null) return false;
            try
            {
                // Shell launch behaves like a Start-menu click and keeps one instance.
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
            catch
            {
                return false;
            }
        }

        /* ------------------------------------------------------------- actions */

        public static string InstallCli(Action<string> log, Action<double> progress)
        {
            var release = LatestRelease(CliRepo, true);
            var asset = release.Assets.FirstOrDefault(a => a.Key.EndsWith("windows-x64.zip", StringComparison.OrdinalIgnoreCase));
            if (asset.Key == null) throw new InvalidOperationException("No Windows x64 build in release v" + release.Tag);

            var work = TempDir("spicetify-cli-");
            try
            {
                var archive = Path.Combine(work, asset.Key);
                log("Downloading " + asset.Key);
                Download(asset.Value, archive, progress);

                log("Extracting");
                var unpacked = Path.Combine(work, "unpacked");
                ZipFile.ExtractToDirectory(archive, unpacked);

                log("Installing into " + CliRoot);
                Directory.CreateDirectory(CliRoot);
                CopyDirectory(unpacked, CliRoot, true);

                if (!File.Exists(CliExe)) throw new InvalidOperationException("spicetify.exe missing after extraction");

                var output = new StringBuilder();
                RunProcess(CliExe, "-v", line => output.AppendLine(line), 60000);
                log("spicetify -v -> " + output.ToString().Trim());

                AddToPath(log);

                if (Directory.Exists(LegacyRoot))
                    log("Note: an older install is still in " + LegacyRoot + " – the up-to-date one is " + CliRoot);

                log("Spicetify CLI v" + release.Tag + " installed");
                return release.Tag;
            }
            finally
            {
                TryDeleteDir(work);
            }
        }

        public static string InstallMarketplace(Action<string> log, Action<double> progress, bool setCurrentTheme)
        {
            var exe = InstalledExe();
            if (exe == null) throw new InvalidOperationException("Install the Spicetify CLI first");

            var userdata = UserDataDirectory();
            var appDir = Path.Combine(userdata, "CustomApps", "marketplace");
            var themeDir = Path.Combine(userdata, "Themes", "marketplace");

            var release = LatestRelease(MarketRepo, true);
            var asset = release.Assets.FirstOrDefault(a => a.Key.Equals("marketplace.zip", StringComparison.OrdinalIgnoreCase));
            if (asset.Key == null) asset = release.Assets.FirstOrDefault();
            if (asset.Key == null) throw new InvalidOperationException("No marketplace build in the latest release");

            var work = TempDir("spicetify-marketplace-");
            try
            {
                var archive = Path.Combine(work, "marketplace.zip");
                log("Downloading " + asset.Key);
                Download(asset.Value, archive, progress);

                log("Extracting");
                var unpacked = Path.Combine(work, "unpacked");
                ZipFile.ExtractToDirectory(archive, unpacked);

                var manifest = Directory.GetFiles(unpacked, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
                if (manifest == null) throw new InvalidOperationException("marketplace.zip did not contain a manifest.json");

                log("Installing into " + appDir);
                TryDeleteDir(appDir);
                TryDeleteDir(themeDir);
                CopyDirectory(Path.GetDirectoryName(manifest), appDir, true);
                Directory.CreateDirectory(themeDir);

                log("Downloading placeholder Marketplace theme");
                var colors = Path.Combine(work, "color.ini");
                Download(ColorsUrl, colors, null);
                File.Copy(colors, Path.Combine(themeDir, "color.ini"), true);

                log("Registering Marketplace as a custom app");
                RunProcess(exe, "config custom_apps spicetify-marketplace- -q", log, 60000);
                if (RunProcess(exe, "config custom_apps marketplace", log, 60000) != 0)
                {
                    log("Could not add marketplace to custom_apps automatically – set it manually with:");
                    log("  spicetify config custom_apps marketplace");
                }
                RunProcess(exe, "config inject_css 1 replace_colors 1", log, 60000);

                if (setCurrentTheme)
                {
                    if (RunProcess(exe, "config current_theme marketplace", log, 60000) == 0)
                        log("Marketplace theme set as current theme");
                }

                log("Backing up Spotify and applying changes");
                RunProcess(exe, "backup", log, 180000);
                if (RunProcess(exe, "apply", log, 300000) != 0)
                    log("spicetify apply returned a non-zero exit code – see the output above");

                File.WriteAllText(Path.Combine(appDir, ".marketplace-version"), release.Tag + Environment.NewLine);
                log("Marketplace v" + release.Tag + " installed");
                return release.Tag;
            }
            finally
            {
                TryDeleteDir(work);
            }
        }

        public static string Repair(Action<string> log, Action<double> progress)
        {
            var version = InstallCli(log, progress);
            var exe = InstalledExe();
            if (exe == null) throw new InvalidOperationException("Spicetify CLI is not installed");

            log("Restoring the original Spotify files");
            if (RunProcess(exe, "restore", log, 300000) != 0)
                log("No usable backup to restore from – continuing with apply");

            if (!Directory.Exists(Path.Combine(CliRoot, "jsHelper")))
                log("Warning: " + Path.Combine(CliRoot, "jsHelper") + " is still missing");

            log("Re-applying Spicetify");
            if (RunProcess(exe, "apply", log, 300000) != 0)
                throw new InvalidOperationException("spicetify apply exited with an error – is Spotify running?");

            return version;
        }

        public static string UpdateSelf(Action<string> log, Action<double> progress)
        {
            var release = LatestRelease(SelfRepo, true);
            if (Compare(release.Tag, SelfVersion()) <= 0)
                throw new InvalidOperationException("Already on the latest version (v" + SelfVersion() + ")");

            var asset = release.Assets.FirstOrDefault(a =>
                a.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                a.Key.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) < 0);
            if (asset.Key == null)
                asset = release.Assets.FirstOrDefault(a => a.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset.Key == null)
                throw new InvalidOperationException("Release v" + release.Tag + " has no downloadable exe");

            var target = Process.GetCurrentProcess().MainModule.FileName;

            // Fail early with a clear message instead of swapping a half-written file.
            try
            {
                var probe = target + ".probe";
                File.Copy(target, probe, true);
                File.Delete(probe);
            }
            catch
            {
                throw new InvalidOperationException(
                    "The app folder is not writable. Download the setup for v" + release.Tag + " from the release page instead.");
            }

            var download = Path.Combine(Path.GetTempPath(), asset.Key);
            log("Downloading " + asset.Key);
            Download(asset.Value, download, progress);

            var script = Path.Combine(Path.GetTempPath(), "spicetify-updater-swap.cmd");
            var lines = new[]
            {
                "@echo off",
                "ping 127.0.0.1 -n 3 >nul",
                "copy /y \"" + download + "\" \"" + target + "\" >nul || exit /b 1",
                "start \"\" \"" + target + "\"",
                "del \"%~f0\""
            };
            File.WriteAllText(script, string.Join("\r\n", lines), Encoding.Default);

            log("Restarting with v" + release.Tag);
            Process.Start(new ProcessStartInfo(script) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            return release.Tag;
        }

        public static string RunCommand(string[] args, Action<string> log, bool allowFailure)
        {
            var exe = InstalledExe();
            if (exe == null) throw new InvalidOperationException("Spicetify CLI is not installed");

            var code = RunProcess(exe, string.Join(" ", args), log, 300000);
            if (code != 0 && !allowFailure)
                throw new InvalidOperationException("spicetify " + string.Join(" ", args) + " exited with code " + code);
            return code.ToString(CultureInfo.InvariantCulture);
        }

        private static void AddToPath(Action<string> log)
        {
            if (IsOnUserPath()) return;

            log("Adding " + CliRoot + " to your user PATH");
            var user = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? "";
            var entries = user.Split(';')
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Where(p => !p.Trim().TrimEnd('\\').Equals(LegacyRoot, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!entries.Any(p => p.Trim().TrimEnd('\\').Equals(CliRoot, StringComparison.OrdinalIgnoreCase)))
                entries.Add(CliRoot);

            Environment.SetEnvironmentVariable("Path", string.Join(";", entries), EnvironmentVariableTarget.User);
        }

        /* ------------------------------------------------------------- plumbing */

        private static string TempDir(string prefix)
        {
            var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* leftovers in temp are harmless */ }
        }

        private static void CopyDirectory(string source, string destination, bool overwrite)
        {
            Directory.CreateDirectory(destination);
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, Relative(dir, source)));

            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(destination, Relative(file, source)), overwrite);
        }

        private static string Relative(string path, string root)
        {
            return path.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
        }

        private static void Download(string url, string destination, Action<double> progress)
        {
            EnsureTls();
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                using (var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? 0;
                    using (var source = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var target = File.Create(destination))
                    {
                        var buffer = new byte[81920];
                        long copied = 0;
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            target.Write(buffer, 0, read);
                            copied += read;
                            if (progress != null && total > 0) progress((double)copied / total);
                        }
                    }
                }
            }
        }

        private static int RunProcess(string fileName, string arguments, Action<string> onLine, int timeoutMs)
        {
            var info = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            var prefix = CliRoot + Path.PathSeparator + (Environment.GetEnvironmentVariable("Path") ?? "");
            info.EnvironmentVariables["Path"] = prefix;

            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(Clean(e.Data)); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null && onLine != null) onLine(Clean(e.Data)); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    return -1;
                }

                process.WaitForExit();
                return process.ExitCode;
            }
        }

        private static string Clean(string line)
        {
            return System.Text.RegularExpressions.Regex.Replace(line, @"\x1B\[[0-9;]*m", "").Trim();
        }
    }
}
