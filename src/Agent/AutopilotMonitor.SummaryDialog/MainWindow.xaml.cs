using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AutopilotMonitor.SummaryDialog.Models;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace AutopilotMonitor.SummaryDialog
{
    public partial class MainWindow : Window
    {
        private DispatcherTimer _countdownTimer;
        private int _remainingSeconds;
        private FinalStatus _status;

        // Theme colors
        private bool _isDarkTheme;

        public MainWindow()
        {
            App.Log("MainWindow constructor — InitializeComponent");
            InitializeComponent();
            App.Log("MainWindow constructor — done");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            App.Log("Window_Loaded — start");
            try
            {
                _isDarkTheme = App.ForceTheme ?? DetectDarkTheme();
                App.Log($"Theme: isDark={_isDarkTheme} (forced={App.ForceTheme})");
                if (_isDarkTheme)
                    ApplyDarkTheme();

                ApplyScrollBarStyle();
                ApplyContentClip();
                ApplyRoundedCorners();

                LoadStatusData();

                // Schema-version dispatch: V2 renderer is enabled when the agent emitted
                // schemaVersion >= 2. V1 agents (no schemaVersion field) deserialise to 0
                // and we keep the legacy rendering for backward compat. When V1 is
                // retired, the V1 branch can be deleted in one cut.
                var outcomeKind = OutcomeMapper.Map(_status);
                ApplyTaskbarIcon(outcomeKind == OutcomeKind.Success
                                 || outcomeKind == OutcomeKind.PreProvisioningComplete);

                if (OutcomeMapper.IsV2Schema(_status))
                {
                    RenderOutcomeV2(outcomeKind);
                    RenderAppListV2();
                }
                else
                {
                    RenderOutcome();
                    RenderAppList();
                }

                StartCountdown();

                // Load branding image (async, best-effort)
                if (!string.IsNullOrEmpty(App.BrandingImageUrl))
                {
                    await LoadBrandingImageAsync();
                }

                App.Log("Window_Loaded — complete, window should be visible");
            }
            catch (Exception ex)
            {
                App.LogError($"Window_Loaded FAILED: {ex}");
            }
        }

        private void LoadStatusData()
        {
            try
            {
                if (!string.IsNullOrEmpty(App.StatusFilePath) && File.Exists(App.StatusFilePath))
                {
                    var json = File.ReadAllText(App.StatusFilePath);
                    _status = JsonConvert.DeserializeObject<FinalStatus>(json);
                    App.Log($"LoadStatusData: outcome={_status?.Outcome} apps={_status?.AppSummary?.TotalApps}");
                }
                else
                {
                    App.Log($"LoadStatusData: status file missing or empty (path={App.StatusFilePath})");
                }
            }
            catch (Exception ex)
            {
                App.LogError($"LoadStatusData FAILED: {ex.Message}");
                _status = null;
            }
        }

        private void RenderOutcome()
        {
            if (_status == null)
            {
                OutcomeText.Text = "Enrollment Completed";
                DurationText.Text = "Details unavailable";
                SuccessIcon.Visibility = Visibility.Visible;
                return;
            }

            var isSuccess = string.Equals(_status.Outcome, "completed", StringComparison.OrdinalIgnoreCase);

            if (isSuccess)
            {
                OutcomeText.Text = "Enrollment Completed Successfully";
                SuccessIcon.Visibility = Visibility.Visible;
                FailureIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                OutcomeText.Text = "Enrollment Failed";
                SuccessIcon.Visibility = Visibility.Collapsed;
                FailureIcon.Visibility = Visibility.Visible;

                OutcomeText.Foreground = (Brush)FindResource("ErrorColor");
            }

            // Format duration
            var duration = TimeSpan.FromSeconds(_status.AgentUptimeSeconds);
            if (duration.TotalHours >= 1)
                DurationText.Text = $"Duration: {(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            else if (duration.TotalMinutes >= 1)
                DurationText.Text = $"Duration: {(int)duration.TotalMinutes} min {duration.Seconds} sec";
            else
                DurationText.Text = $"Duration: {duration.Seconds} sec";
        }

        /// <summary>
        /// Schema 2 — six-state outcome rendering plus failure banner. Reuses the existing
        /// SuccessIcon / FailureIcon canvases and recolours them at runtime so we don't
        /// have to declare four separate XAML icon shapes.
        /// </summary>
        private void RenderOutcomeV2(OutcomeKind kind)
        {
            if (_status == null)
            {
                OutcomeText.Text = "Enrollment Status Unknown";
                DurationText.Text = "Details unavailable";
                FailureIcon.Visibility = Visibility.Visible;
                return;
            }

            OutcomeText.Text = OutcomeMapper.HeaderText(kind);

            // Pick icon shape (check vs X), then recolour for the specific kind.
            switch (kind)
            {
                case OutcomeKind.Success:
                    SuccessIcon.Visibility = Visibility.Visible;
                    FailureIcon.Visibility = Visibility.Collapsed;
                    break;
                case OutcomeKind.PreProvisioningComplete:
                    SuccessIcon.Visibility = Visibility.Visible;
                    FailureIcon.Visibility = Visibility.Collapsed;
                    RecolourIcon(SuccessIcon, (Brush)FindResource("InfoBackground"), (Brush)FindResource("InfoColor"));
                    OutcomeText.Foreground = (Brush)FindResource("InfoColor");
                    break;
                case OutcomeKind.TimedOut:
                case OutcomeKind.Unknown:
                    SuccessIcon.Visibility = Visibility.Collapsed;
                    FailureIcon.Visibility = Visibility.Visible;
                    RecolourIcon(FailureIcon, (Brush)FindResource("WarningBackground"), (Brush)FindResource("WarningColor"));
                    OutcomeText.Foreground = (Brush)FindResource("WarningColor");
                    break;
                case OutcomeKind.Failure:
                default:
                    SuccessIcon.Visibility = Visibility.Collapsed;
                    FailureIcon.Visibility = Visibility.Visible;
                    OutcomeText.Foreground = (Brush)FindResource("ErrorColor");
                    break;
            }

            // Failure / info banner — the agent-supplied reason wins, otherwise fall back
            // to a generic per-state message. Skip entirely for clean Success.
            var bannerText = !string.IsNullOrWhiteSpace(_status.FailureReason)
                ? _status.FailureReason
                : OutcomeMapper.DefaultBannerText(kind);
            if (!string.IsNullOrEmpty(bannerText))
            {
                StatusBannerText.Text = bannerText;
                StatusBanner.Visibility = Visibility.Visible;
                switch (kind)
                {
                    case OutcomeKind.PreProvisioningComplete:
                        StatusBanner.Background = (Brush)FindResource("InfoBackground");
                        StatusBannerText.Foreground = (Brush)FindResource("InfoColor");
                        break;
                    case OutcomeKind.TimedOut:
                    case OutcomeKind.Unknown:
                        StatusBanner.Background = (Brush)FindResource("WarningBackground");
                        StatusBannerText.Foreground = (Brush)FindResource("WarningColor");
                        break;
                    case OutcomeKind.Failure:
                    default:
                        StatusBanner.Background = (Brush)FindResource("ErrorBackground");
                        StatusBannerText.Foreground = (Brush)FindResource("ErrorColor");
                        break;
                }
            }

            // Format duration (same for all V2 outcome kinds).
            var duration = TimeSpan.FromSeconds(_status.AgentUptimeSeconds);
            if (duration.TotalHours >= 1)
                DurationText.Text = $"Duration: {(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            else if (duration.TotalMinutes >= 1)
                DurationText.Text = $"Duration: {(int)duration.TotalMinutes} min {duration.Seconds} sec";
            else
                DurationText.Text = $"Duration: {duration.Seconds} sec";
        }

        /// <summary>
        /// Walks the SuccessIcon / FailureIcon canvas and replaces its two-tone fill +
        /// stroke with the given pair, so a single XAML icon shape can carry four
        /// outcome variants without duplicating the Path geometry.
        /// </summary>
        private static void RecolourIcon(Canvas icon, Brush background, Brush foreground)
        {
            foreach (var child in icon.Children)
            {
                if (child is System.Windows.Shapes.Ellipse el)
                {
                    if (el.Fill != null) el.Fill = background;
                    if (el.Stroke != null) el.Stroke = foreground;
                }
                else if (child is System.Windows.Shapes.Path p)
                {
                    p.Stroke = foreground;
                }
            }
        }

        private void RenderAppList()
        {
            if (_status?.AppSummary == null || _status.AppSummary.TotalApps == 0)
            {
                NoAppsText.Visibility = Visibility.Visible;
                AppSummaryText.Visibility = Visibility.Collapsed;
                return;
            }

            var summary = _status.AppSummary;

            // Count skipped apps across all phases to exclude from totals
            var skippedCount = _status.PackageStatesByPhase != null
                ? _status.PackageStatesByPhase.Values.SelectMany(p => p)
                    .Count(a => string.Equals(a.State, "Skipped", StringComparison.OrdinalIgnoreCase))
                : 0;

            var visibleTotal = summary.TotalApps - skippedCount;
            var visibleCompleted = summary.CompletedApps - skippedCount;
            if (visibleTotal < 0) visibleTotal = 0;
            if (visibleCompleted < 0) visibleCompleted = 0;

            // If all apps were skipped, show "no apps" message
            if (visibleTotal == 0)
            {
                NoAppsText.Visibility = Visibility.Visible;
                AppSummaryText.Visibility = Visibility.Collapsed;
                return;
            }

            // Update progress bar
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var parentGrid = (Grid)ProgressBarSuccess.Parent;
                var totalWidth = parentGrid.ActualWidth;
                if (totalWidth > 0 && visibleTotal > 0)
                {
                    ProgressBarSuccess.Width = (double)visibleCompleted / visibleTotal * totalWidth;
                    if (summary.ErrorCount > 0)
                        ProgressBarSuccess.Background = (Brush)FindResource("ProgressError");
                }
            }));

            // Summary text
            if (summary.ErrorCount > 0)
            {
                AppSummaryText.Text = $"{visibleCompleted} of {visibleTotal} apps installed, {summary.ErrorCount} failed";
                AppSummaryText.Foreground = (Brush)FindResource("ErrorColor");
            }
            else
            {
                AppSummaryText.Text = $"{visibleCompleted} of {visibleTotal} apps installed";
            }

            // Render phases
            if (_status.PackageStatesByPhase != null)
            {
                foreach (var phase in _status.PackageStatesByPhase)
                {
                    AddPhaseSection(phase.Key, phase.Value);
                }
            }
        }

        /// <summary>
        /// Schema 2 — same overall layout as <see cref="RenderAppList"/> but pushes failed
        /// apps to the top of each phase (so the user sees what's broken first) and uses
        /// <see cref="CreateAppItemV2"/> which shows per-app duration and error detail.
        /// </summary>
        private void RenderAppListV2()
        {
            if (_status?.AppSummary == null || _status.AppSummary.TotalApps == 0)
            {
                NoAppsText.Visibility = Visibility.Visible;
                AppSummaryText.Visibility = Visibility.Collapsed;
                return;
            }

            var summary = _status.AppSummary;

            var skippedCount = _status.PackageStatesByPhase != null
                ? _status.PackageStatesByPhase.Values.SelectMany(p => p)
                    .Count(a => string.Equals(a.State, "Skipped", StringComparison.OrdinalIgnoreCase))
                : 0;

            var visibleTotal = summary.TotalApps - skippedCount;
            var visibleCompleted = summary.CompletedApps - skippedCount;
            if (visibleTotal < 0) visibleTotal = 0;
            if (visibleCompleted < 0) visibleCompleted = 0;

            if (visibleTotal == 0)
            {
                NoAppsText.Visibility = Visibility.Visible;
                AppSummaryText.Visibility = Visibility.Collapsed;
                return;
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var parentGrid = (Grid)ProgressBarSuccess.Parent;
                var totalWidth = parentGrid.ActualWidth;
                if (totalWidth > 0 && visibleTotal > 0)
                {
                    ProgressBarSuccess.Width = (double)visibleCompleted / visibleTotal * totalWidth;
                    if (summary.ErrorCount > 0)
                        ProgressBarSuccess.Background = (Brush)FindResource("ProgressError");
                }
            }));

            if (summary.ErrorCount > 0)
            {
                AppSummaryText.Text = $"{visibleCompleted} of {visibleTotal} apps installed, {summary.ErrorCount} failed";
                AppSummaryText.Foreground = (Brush)FindResource("ErrorColor");
            }
            else
            {
                AppSummaryText.Text = $"{visibleCompleted} of {visibleTotal} apps installed";
            }

            if (_status.PackageStatesByPhase != null)
            {
                foreach (var phase in _status.PackageStatesByPhase)
                {
                    AddPhaseSectionV2(phase.Key, phase.Value);
                }
            }
        }

        private void AddPhaseSectionV2(string phaseName, List<PackageInfo> apps)
        {
            if (apps == null || apps.Count == 0) return;

            // Errors first, then completed, then anything else — gives the user actionable
            // information at a glance instead of buried inside a long list.
            var visibleApps = apps
                .Where(a => !string.Equals(a.State, "Skipped", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.IsError)
                .ThenBy(a => a.AppName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (visibleApps.Count == 0) return;

            var header = new TextBlock
            {
                Text = $"{FormatPhaseName(phaseName)} ({visibleApps.Count})",
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 12,
                Foreground = (Brush)FindResource("PhaseHeaderText"),
                Margin = new Thickness(0, 8, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            AppListPanel.Children.Add(header);

            foreach (var app in visibleApps)
            {
                AppListPanel.Children.Add(CreateAppItemV2(app));
            }
        }

        /// <summary>
        /// Schema 2 — app row layout:
        /// <para>
        /// <c>[icon] [name + optional error detail]              [duration / "Error"]</c>
        /// </para>
        /// Failed apps additionally render the agent-supplied <see cref="PackageInfo.ErrorDetail"/>
        /// (or <see cref="PackageInfo.ErrorCode"/> as fallback) on a second line under the
        /// app name, so the user sees the "why" without opening logs.
        /// </summary>
        private Border CreateAppItemV2(PackageInfo app)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            if (app.IsError)
            {
                iconText.Text = "";
                iconText.Foreground = (Brush)FindResource("ErrorColor");
            }
            else if (app.IsCompleted)
            {
                iconText.Text = "";
                iconText.Foreground = (Brush)FindResource("SuccessColor");
            }
            else
            {
                iconText.Text = "";
                iconText.Foreground = (Brush)FindResource("SecondaryText");
            }
            Grid.SetColumn(iconText, 0);
            grid.Children.Add(iconText);

            // Name + (when present) per-app error detail under it.
            var nameStack = new StackPanel { Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock
            {
                Text = app.AppName ?? "Unknown App",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = (Brush)FindResource("PrimaryText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (app.IsError)
            {
                var detail = !string.IsNullOrWhiteSpace(app.ErrorDetail)
                    ? app.ErrorDetail
                    : (!string.IsNullOrWhiteSpace(app.ErrorCode) ? $"Error code: {app.ErrorCode}" : null);
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    nameStack.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11,
                        Foreground = (Brush)FindResource("SecondaryText"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                }
            }
            Grid.SetColumn(nameStack, 1);
            grid.Children.Add(nameStack);

            // Right column: "Error" label OR duration. Error wins for failed apps so the
            // user sees the most actionable label; duration is shown otherwise.
            string rightText = null;
            Brush rightBrush = (Brush)FindResource("SecondaryText");
            if (app.IsError)
            {
                rightText = "Error";
                rightBrush = (Brush)FindResource("ErrorColor");
            }
            else if (app.DurationSeconds.HasValue && app.DurationSeconds.Value > 0)
            {
                rightText = OutcomeMapper.FormatDuration(app.DurationSeconds.Value);
            }

            if (!string.IsNullOrEmpty(rightText))
            {
                var rightLabel = new TextBlock
                {
                    Text = rightText,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = rightBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(rightLabel, 2);
                grid.Children.Add(rightLabel);
            }

            var border = new Border
            {
                Child = grid,
                Padding = new Thickness(6, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent
            };
            border.MouseEnter += (s, e) => border.Background = (Brush)FindResource("AppItemHover");
            border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;
            return border;
        }

        private void AddPhaseSection(string phaseName, List<PackageInfo> apps)
        {
            if (apps == null || apps.Count == 0) return;

            // Filter out skipped apps — they are not actionable for the user
            var visibleApps = apps.Where(a => !string.Equals(a.State, "Skipped", StringComparison.OrdinalIgnoreCase)).ToList();
            if (visibleApps.Count == 0) return;

            // Phase header
            var header = new TextBlock
            {
                Text = $"{FormatPhaseName(phaseName)} ({visibleApps.Count})",
                FontFamily = new FontFamily("Segoe UI Semibold"),
                FontSize = 12,
                Foreground = (Brush)FindResource("PhaseHeaderText"),
                Margin = new Thickness(0, 8, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            AppListPanel.Children.Add(header);

            foreach (var app in visibleApps)
            {
                AppListPanel.Children.Add(CreateAppItem(app));
            }
        }

        private Border CreateAppItem(PackageInfo app)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Status icon
            var iconText = new TextBlock
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            if (app.IsError)
            {
                iconText.Text = "\uE711"; // X
                iconText.Foreground = (Brush)FindResource("ErrorColor");
            }
            else if (app.IsCompleted)
            {
                iconText.Text = "\uE73E"; // Checkmark
                iconText.Foreground = (Brush)FindResource("SuccessColor");
            }
            else
            {
                iconText.Text = "\uE738"; // Dash/circle
                iconText.Foreground = (Brush)FindResource("SecondaryText");
            }

            Grid.SetColumn(iconText, 0);
            grid.Children.Add(iconText);

            // App name
            var nameText = new TextBlock
            {
                Text = app.AppName ?? "Unknown App",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                Foreground = (Brush)FindResource("PrimaryText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(4, 0, 8, 0)
            };
            Grid.SetColumn(nameText, 1);
            grid.Children.Add(nameText);

            // State label
            if (app.IsError)
            {
                var stateText = new TextBlock
                {
                    Text = "Error",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("ErrorColor"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(stateText, 2);
                grid.Children.Add(stateText);
            }

            var border = new Border
            {
                Child = grid,
                Padding = new Thickness(6, 6, 8, 6),
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 1, 0, 1),
                Background = Brushes.Transparent
            };

            // Hover effect
            border.MouseEnter += (s, e) => border.Background = (Brush)FindResource("AppItemHover");
            border.MouseLeave += (s, e) => border.Background = Brushes.Transparent;

            return border;
        }

        private async System.Threading.Tasks.Task LoadBrandingImageAsync()
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    var bytes = await client.GetByteArrayAsync(App.BrandingImageUrl);
                    var bitmap = new BitmapImage();
                    using (var ms = new System.IO.MemoryStream(bytes))
                    {
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                    }

                    BrandingImage.Source = bitmap;
                    BrandingBanner.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                App.Log($"LoadBrandingImage: skipped ({ex.Message})");
                BrandingBanner.Visibility = Visibility.Collapsed;
            }
        }

        private void StartCountdown()
        {
            _remainingSeconds = App.TimeoutSeconds;
            if (_remainingSeconds <= 0)
            {
                CountdownText.Visibility = Visibility.Collapsed;
                return;
            }

            CountdownText.Text = $"Auto-closing in {_remainingSeconds}s";

            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += (s, e) =>
            {
                _remainingSeconds--;
                if (_remainingSeconds <= 0)
                {
                    _countdownTimer.Stop();
                    Close();
                }
                else
                {
                    CountdownText.Text = $"Auto-closing in {_remainingSeconds}s";
                }
            };
            _countdownTimer.Start();
        }

        private void ApplyDarkTheme()
        {
            Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
            Resources["TitleBarBackground"] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            Resources["BorderColor"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            Resources["PrimaryText"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));
            Resources["SecondaryText"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
            Resources["SuccessColor"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            Resources["SuccessBackground"] = new SolidColorBrush(Color.FromRgb(0x06, 0x4E, 0x3B));
            Resources["ErrorColor"] = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            Resources["ErrorBackground"] = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            Resources["ProgressBackground"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            Resources["ProgressSuccess"] = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            Resources["ProgressError"] = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            Resources["SectionBackground"] = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
            Resources["ButtonBackground"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
            Resources["ButtonHover"] = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
            Resources["ButtonText"] = new SolidColorBrush(Color.FromRgb(0xF9, 0xFA, 0xFB));
            Resources["CloseButtonHover"] = new SolidColorBrush(Color.FromRgb(0x7F, 0x1D, 0x1D));
            Resources["PhaseHeaderText"] = new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB));
            Resources["AppItemHover"] = new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51));
        }

        /// <summary>
        /// Applies a slim, modern scrollbar style matching the current theme.
        /// Completely re-templates the vertical ScrollBar to remove arrow buttons (Win11 style).
        /// </summary>
        private void ApplyScrollBarStyle()
        {
            var trackColor = _isDarkTheme ? "#FF1F2937" : "#FFF9FAFB";
            var thumbColor = _isDarkTheme ? "#FF4B5563" : "#FFD1D5DB";
            var thumbHoverColor = _isDarkTheme ? "#FF6B7280" : "#FF9CA3AF";

            // Build the ScrollBar style via XAML string — this is the most reliable way to
            // re-template Track (which uses CLR properties, not DPs, for its children).
            var xaml = $@"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       TargetType=""{{x:Type ScrollBar}}"">
    <Setter Property=""Width"" Value=""8""/>
    <Setter Property=""MinWidth"" Value=""8""/>
    <Setter Property=""Background"" Value=""Transparent""/>
    <Setter Property=""BorderThickness"" Value=""0""/>
    <Setter Property=""Margin"" Value=""0""/>
    <Setter Property=""Padding"" Value=""0""/>
    <Setter Property=""Template"">
        <Setter.Value>
            <ControlTemplate TargetType=""{{x:Type ScrollBar}}"">
                <Border Background=""{trackColor}"" CornerRadius=""4"">
                    <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
                        <Track.DecreaseRepeatButton>
                            <RepeatButton Command=""ScrollBar.PageUpCommand"" Focusable=""False"">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType=""RepeatButton"">
                                        <Border Background=""Transparent""/>
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.DecreaseRepeatButton>
                        <Track.Thumb>
                            <Thumb MinHeight=""20"">
                                <Thumb.Template>
                                    <ControlTemplate TargetType=""Thumb"">
                                        <Border x:Name=""ThumbBorder"" Background=""{thumbColor}""
                                                CornerRadius=""3"" Margin=""1,2,1,2""/>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                                <Setter TargetName=""ThumbBorder"" Property=""Background"" Value=""{thumbHoverColor}""/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Thumb.Template>
                            </Thumb>
                        </Track.Thumb>
                        <Track.IncreaseRepeatButton>
                            <RepeatButton Command=""ScrollBar.PageDownCommand"" Focusable=""False"">
                                <RepeatButton.Template>
                                    <ControlTemplate TargetType=""RepeatButton"">
                                        <Border Background=""Transparent""/>
                                    </ControlTemplate>
                                </RepeatButton.Template>
                            </RepeatButton>
                        </Track.IncreaseRepeatButton>
                    </Track>
                </Border>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>";

            var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
            Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
        }

        /// <summary>
        /// Tells Windows 11 DWM to round the window corners natively at the compositor level.
        /// Without AllowsTransparency the Win32 window is rectangular — this API makes the
        /// OS clip it to rounded corners, matching our Border's CornerRadius.
        /// Silently ignored on Windows 10 (no visual effect, no error).
        /// </summary>
        private void ApplyRoundedCorners()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
                int preference = 2;
                DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
            }
            catch (Exception ex)
            {
                App.Log($"ApplyRoundedCorners: {ex.Message}");
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Clips the inner content Grid to a rounded rectangle so child elements
        /// (like the title bar) don't bleed past the outer Border's rounded corners.
        /// </summary>
        private void ApplyContentClip()
        {
            // Clip the entire content Border to a rounded rectangle.
            // This prevents child backgrounds from bleeding past the rounded corners.
            const double radius = 12; // matches Border CornerRadius

            void UpdateClip()
            {
                if (ContentBorder.ActualWidth > 0 && ContentBorder.ActualHeight > 0)
                {
                    ContentBorder.Clip = new RectangleGeometry(
                        new Rect(0, 0, ContentBorder.ActualWidth, ContentBorder.ActualHeight),
                        radius, radius);
                }
            }

            ContentBorder.SizeChanged += (s, args) => UpdateClip();
            UpdateClip(); // Set immediately — layout is already complete at Window_Loaded time
        }

        /// <summary>
        /// Generates a green checkmark icon and sets it as the window/taskbar icon.
        /// </summary>
        /// <summary>
        /// Sets the taskbar icon. Success icon is loaded statically via XAML;
        /// this method only overrides it with the error icon when enrollment failed.
        /// </summary>
        private void ApplyTaskbarIcon(bool isSuccess)
        {
            if (!isSuccess)
            {
                try
                {
                    var uri = new Uri("pack://application:,,,/icon-error.ico", UriKind.Absolute);
                    Icon = new BitmapImage(uri);
                }
                catch (Exception ex)
                {
                    App.Log($"ApplyTaskbarIcon: failed to load error icon ({ex.Message}), keeping success icon");
                }
            }
        }

        private static bool DetectDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    var isDark = value is int i && i == 0;
                    App.Log($"DetectDarkTheme: AppsUseLightTheme={value} → isDark={isDark}");
                    return isDark;
                }
            }
            catch (Exception ex)
            {
                App.Log($"DetectDarkTheme: registry read failed ({ex.Message}), defaulting to light");
                return false;
            }
        }

        private static string FormatPhaseName(string phase)
        {
            if (string.IsNullOrEmpty(phase)) return phase;

            // Canonical display names, mirroring EnrollmentEvent.GetPhaseName in Shared.
            // This dialog is deliberately dependency-free (standalone EXE), so the mapping is
            // restated here instead of referencing Shared — the camel-case splitter below is
            // only the fallback for unknown tokens and cannot produce "Apps (Device)".
            switch (phase)
            {
                case "Start": return "Start";
                case "DevicePreparation": return "Device Preparation";
                case "DeviceSetup": return "Device Setup";
                case "AppsDevice": return "Apps (Device)";
                case "AccountSetup": return "Account Setup";
                case "AppsUser": return "Apps (User)";
                case "FinalizingSetup": return "Finalizing Setup";
                case "Complete": return "Complete";
                case "Failed": return "Failed";
            }

            // Fallback: "SomeNewPhase" -> "Some New Phase"
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < phase.Length; i++)
            {
                if (i > 0 && char.IsUpper(phase[i]) && !char.IsUpper(phase[i - 1]))
                    result.Append(' ');
                result.Append(phase[i]);
            }
            return result.ToString();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
                DragMove();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            _countdownTimer?.Stop();
            Close();
        }
    }

    // Extension helper for Parent casting
    internal static class FrameworkElementExtensions
    {
        internal static T As<T>(this DependencyObject obj) where T : class
        {
            return obj as T;
        }
    }
}
