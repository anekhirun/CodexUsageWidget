using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace CodexUsageWidget;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush GreenBrush = Brush("#FF70D88B");
    private static readonly SolidColorBrush AmberBrush = Brush("#FFF59E0B");
    private static readonly SolidColorBrush RedBrush = Brush("#FFEF4444");
    private static readonly SolidColorBrush BlueBrush = Brush("#FF5BAEFF");
    private DateTimeOffset? _lastPulsedEventTime;
    private static readonly SolidColorBrush GrayBrush = Brush("#FF777777");
    private static readonly SolidColorBrush TextBrush = Brush("#FFF3F3F3");
    private static readonly SolidColorBrush MutedTextBrush = Brush("#FFAAAAAA");

    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DispatcherTimer _taskbarTimer = new();
    private readonly DispatcherTimer _refreshFeedbackTimer = new();
    private readonly string _configDirectory;
    private readonly string _stateFile;
    private readonly string _logFile;
    private readonly ContextConfigCoordinator _contextConfigCoordinator;

    private WidgetState _state = new();
    private nint _windowHandle;
    private nint _taskbarHandle;
    private bool _placementReady;
    private System.Windows.Forms.Screen? _selectedScreen;
    private System.Drawing.Rectangle _targetBounds;
    private bool _isDragging;
    private double _screenLeft;
    private double _screenRight = SystemParameters.PrimaryScreenWidth;
    private double _widgetLeft;
    private double _logicalWidgetWidth = 279;
    private double _dipPerPixel = 1;
    private int _dragStartX;
    private double _dragStartLeft;
    private string _lastLogMessage = string.Empty;
    private bool _loadingContextMode;
    private CancellationTokenSource? _contextModeChangeCts;

    public MainWindow()
    {
        InitializeComponent();
        Opacity = 0;

        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CodexUsageWidget");
        Directory.CreateDirectory(_configDirectory);
        _stateFile = Path.Combine(_configDirectory, "widget-state.json");
        _logFile = Path.Combine(_configDirectory, "widget-errors.log");
        _contextConfigCoordinator = ContextConfigCoordinator.CreateDefault(_configDirectory);

        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        Closed += OnClosed;

        Root.MouseLeftButtonDown += OnMouseLeftButtonDown;
        Root.MouseMove += OnMouseMove;
        Root.MouseLeftButtonUp += OnMouseLeftButtonUp;
        Root.LostMouseCapture += (_, _) => FinishDrag();

        MoveLeftMenu.Click += (_, _) => SetTaskbarPosition(_screenLeft);
        PlaceWeatherMenu.Click += (_, _) => SetTaskbarPosition(_screenLeft + 205);
        MoveRightMenu.Click += (_, _) => SetTaskbarPosition(_screenRight - PhysicalWidgetWidth());
        NextDisplayMenu.Click += (_, _) => MoveToNextDisplay();
        RefreshMenu.Click += (_, _) => RefreshUsage(manualRequest: true);
        TaskbarModeSlider.ValueChanged += OnTaskbarModeSliderValueChanged;
        ContextDefaultMenu.Click += async (_, _) => await ApplyContextSelectionAsync(0, false);
        Context300KMenu.Click += async (_, _) => await ApplyContextSelectionAsync(1, false);
        Context600KMenu.Click += async (_, _) => await ApplyContextSelectionAsync(2, false);
        Context1MMenu.Click += async (_, _) => await ApplyContextSelectionAsync(3, false);
        CompactLayoutMenu.Click += (_, _) =>
        {
            _state.CompactLayout = CompactLayoutMenu.IsChecked;
            ApplyLayout();
            SyncDisplayGeometry();
            PositionAnchoredWidget();
            SaveState();
        };
        Refresh30Menu.Click += (_, _) => SetRefreshMode("30 seconds");
        Refresh2MinuteMenu.Click += (_, _) => SetRefreshMode("2 minutes");
        Refresh5MinuteMenu.Click += (_, _) => SetRefreshMode("5 minutes");
        RefreshManualMenu.Click += (_, _) => SetRefreshMode("Manual only");
        LockPositionMenu.Click += (_, _) =>
        {
            _state.Locked = LockPositionMenu.IsChecked;
            Root.Cursor = _state.Locked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeWE;
            SaveState();
        };
        ExitMenu.Click += (_, _) => Close();

        _refreshTimer.Tick += (_, _) =>
        {
            if (_state.RefreshMode != "Manual only") RefreshUsage();
        };
        _taskbarTimer.Interval = TimeSpan.FromSeconds(1);
        _taskbarTimer.Tick += (_, _) =>
        {
            if (!_placementReady || _isDragging || Root.IsMouseCaptureWithin ||
                Root.ContextMenu?.IsOpen == true) return;
            SyncDisplayGeometry();
            PositionAnchoredWidget();
        };
        _refreshFeedbackTimer.Interval = TimeSpan.FromSeconds(6);
        _refreshFeedbackTimer.Tick += (_, _) =>
        {
            _refreshFeedbackTimer.Stop();
            RefreshMenu.Header = "Refresh usage";
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowHandle = new WindowInteropHelper(this).Handle;
        if (double.IsFinite(Width) && Width > 1) _logicalWidgetWidth = Width;
        _dipPerPixel = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        LoadState();
        _widgetLeft = _state.Left;
        ApplyStateToMenus();
        ApplyLayout();
        ReloadTaskbarContextMode();
        SetRefreshTimerInterval();
        SyncDisplayGeometry();
        RefreshUsage();
        _refreshTimer.Start();
        _taskbarTimer.Start();
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _placementReady = true;
        Topmost = true;
        SyncDisplayGeometry();
        PositionAnchoredWidget();
        Opacity = 1;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _taskbarTimer.Stop();
        _refreshFeedbackTimer.Stop();
        _contextModeChangeCts?.Cancel();
        _contextModeChangeCts = null;
        SaveState();
        System.Windows.Application.Current.Shutdown();
    }

    private void ReloadTaskbarContextMode()
    {
        ContextConfigState state = CodexConfigService.ReadState(CodexConfigService.GlobalConfigPath);
        _loadingContextMode = true;
        TaskbarModeSlider.Value = state.IsCustom ? CustomSliderPosition(state) : state.Mode!.Index;
        TaskbarModeText.Text = state.IsCustom ? "CUSTOM" : CompactModeLabel(state.Mode!);
        TaskbarModeSlider.IsEnabled = true;
        _loadingContextMode = false;
        ContextDefaultMenu.IsChecked = state.Mode?.Index == 0;
        Context300KMenu.IsChecked = state.Mode?.Index == 1;
        Context600KMenu.IsChecked = state.Mode?.Index == 2;
        Context1MMenu.IsChecked = state.Mode?.Index == 3;
        if (state.IsCustom)
            UpdateCustomContextToolTip(state);
        else
            UpdateContextToolTip(state.Mode!);
    }

    private async void OnTaskbarModeSliderValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (_loadingContextMode) return;
        await ApplyContextSelectionAsync((int)Math.Round(TaskbarModeSlider.Value), true);
    }

    private async Task ApplyContextSelectionAsync(int index, bool waitForDrag)
    {
        ContextMode selectedMode = CodexConfigService.Modes[index];
        TaskbarModeText.Text = CompactModeLabel(selectedMode);
        UpdateContextToolTip(selectedMode, "Applying selection…");

        var cancellation = new CancellationTokenSource();
        CancellationTokenSource? previous = _contextModeChangeCts;
        _contextModeChangeCts = cancellation;
        previous?.Cancel();

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellation.Token);
            while (waitForDrag && TaskbarModeSlider.IsMouseCaptureWithin)
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellation.Token);
            ContextMode mode = selectedMode;
            TaskbarModeSlider.IsEnabled = false;
            ContextModeMenu.IsEnabled = false;

            ConfigWriteResult result = await _contextConfigCoordinator.ApplyGlobalAsync(mode, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                ReloadTaskbarContextMode();
                TaskbarModeText.Text = "ERROR";
                string currentState = TaskbarModeSlider.ToolTip as string ?? "Current global setting preserved";
                TaskbarModeSlider.ToolTip = $"{result.Message}\n{currentState}";
                ContextModeMenu.ToolTip = TaskbarModeSlider.ToolTip;
                return;
            }

            ReloadTaskbarContextMode();
            UpdateContextToolTip(mode, result.Message);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReloadTaskbarContextMode();
            TaskbarModeText.Text = "ERROR";
            TaskbarModeSlider.ToolTip = $"Context update failed: {exception.Message}";
            ContextModeMenu.ToolTip = TaskbarModeSlider.ToolTip;
            WriteLog($"Context update failed: {exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_contextModeChangeCts, cancellation))
            {
                _contextModeChangeCts = null;
                TaskbarModeSlider.IsEnabled = true;
                ContextModeMenu.IsEnabled = true;
            }
            cancellation.Dispose();
        }
    }

    private void UpdateContextToolTip(ContextMode mode, string? resultMessage = null)
    {
        string summary = $"{mode.Name} · {mode.ContextLabel} · Global Codex setting";
        TaskbarModeSlider.ToolTip = resultMessage is null ? summary : $"{resultMessage}\n{summary}";
        ContextModeMenu.Header = $"Context mode ({CompactModeLabel(mode)})";
        ContextModeMenu.ToolTip = TaskbarModeSlider.ToolTip;
    }

    private void UpdateCustomContextToolTip(ContextConfigState state)
    {
        string detail = state.ReadFailed
            ? "Could not read the current context settings"
            : $"{FormatTokenCount(state.ContextWindow)} context · {FormatTokenCount(state.CompactLimit)} auto-compaction";
        TaskbarModeSlider.ToolTip =
            $"Custom · {detail} · config.toml is preserved until you select a preset";
        ContextModeMenu.Header = "Context mode (Custom)";
        ContextModeMenu.ToolTip = TaskbarModeSlider.ToolTip;
    }

    private static double CustomSliderPosition(ContextConfigState state)
    {
        if (state.ContextWindow is null) return 0.5;

        ContextMode nearest = CodexConfigService.Modes
            .Where(mode => !mode.IsDefault)
            .OrderBy(mode => Math.Abs((long)mode.ContextWindow!.Value - state.ContextWindow.Value) +
                             (state.CompactLimit is null
                                 ? 0
                                 : Math.Abs((long)mode.CompactLimit!.Value - state.CompactLimit.Value) / 2))
            .First();
        return nearest.Index >= 3 ? 2.5 : nearest.Index + 0.5;
    }

    private static string FormatTokenCount(int? value)
    {
        if (value is null) return "model default";
        return value.Value >= 1_000_000 && value.Value % 1_000_000 == 0
            ? $"{value.Value / 1_000_000}M"
            : value.Value % 1_000 == 0
                ? $"{value.Value / 1_000}K"
                : value.Value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string CompactModeLabel(ContextMode mode) => mode.IsDefault
        ? "DEFAULT"
        : mode.ContextLabel
            .Replace(" context", string.Empty, StringComparison.Ordinal)
            .Replace("~", string.Empty, StringComparison.Ordinal);

    private void LoadState()
    {
        if (!File.Exists(_stateFile)) return;
        try
        {
            WidgetState? saved = JsonSerializer.Deserialize<WidgetState>(File.ReadAllText(_stateFile));
            if (saved is not null) _state = saved;
        }
        catch (Exception exception)
        {
            WriteLog($"Could not load settings: {exception.Message}");
        }

        if (_state.RefreshMode is not ("30 seconds" or "2 minutes" or "5 minutes" or "Manual only"))
            _state.RefreshMode = "30 seconds";
        if (_state.LayoutVersion < 2)
        {
            _state.Left = 0;
            _state.LayoutVersion = 2;
        }
    }

    private void SaveState()
    {
        try
        {
            string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFile, json);
        }
        catch (Exception exception)
        {
            WriteLog($"Could not save settings: {exception.Message}");
        }
    }

    private void WriteLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message == _lastLogMessage) return;
        _lastLogMessage = message;
        try
        {
            if (File.Exists(_logFile) && new FileInfo(_logFile).Length > 262_144)
                File.Move(_logFile, _logFile + ".old", true);
            File.AppendAllText(_logFile, $"{DateTime.UtcNow:u} {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void ApplyStateToMenus()
    {
        LockPositionMenu.IsChecked = _state.Locked;
        Root.Cursor = _state.Locked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeWE;
        Refresh30Menu.IsChecked = _state.RefreshMode == "30 seconds";
        Refresh2MinuteMenu.IsChecked = _state.RefreshMode == "2 minutes";
        Refresh5MinuteMenu.IsChecked = _state.RefreshMode == "5 minutes";
        RefreshManualMenu.IsChecked = _state.RefreshMode == "Manual only";
    }

    private void ApplyLayout()
    {
        _logicalWidgetWidth = _state.CompactLayout ? 279 : 385;
        Width = _logicalWidgetWidth;
        CompactLayoutMenu.IsChecked = _state.CompactLayout;
        CompactLayoutPanel.Visibility = _state.CompactLayout ? Visibility.Visible : Visibility.Collapsed;
        OverviewLayoutPanel.Visibility = _state.CompactLayout ? Visibility.Collapsed : Visibility.Visible;
        if (_state.CompactLayout)
        {
            Root.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
            Root.ClearValue(System.Windows.Controls.Border.BorderBrushProperty);
        }
        else
        {
            Root.Background = System.Windows.Media.Brushes.Transparent;
            Root.BorderBrush = Brush("#28FFFFFF");
        }
    }

    private void SetRefreshMode(string mode)
    {
        _state.RefreshMode = mode;
        ApplyStateToMenus();
        SetRefreshTimerInterval();
        SaveState();
    }

    private void SetRefreshTimerInterval()
    {
        int seconds = _state.RefreshMode switch
        {
            "2 minutes" => 120,
            "5 minutes" => 300,
            "Manual only" => 3600,
            _ => 30
        };
        _refreshTimer.Interval = TimeSpan.FromSeconds(seconds);
    }

    private void RefreshUsage(bool manualRequest = false)
    {
        DateTimeOffset checkedAt = DateTimeOffset.Now;
        UsageSnapshot? snapshot = UsageReader.GetLatestSnapshot();
        if (snapshot is null)
        {
            OverviewPercentText.Text = "--%";
            OverviewTimeText.Text = "WEEKLY · Unavailable";
            OverviewRemainingBar.Width = 0;
            OverviewRemainingBar.Background = GrayBrush;
            OverviewPercentText.Foreground = MutedTextBrush;
            SetShortUsageUnavailable();
            PercentText.Text = "--%";
            TimeText.Text = "Weekly Reset unavailable";
            RemainingBar.Width = 0;
            Root.ToolTip = "ChatGPT Codex - No usage data found";
            WriteLog("No Codex usage data found");
            if (manualRequest) ShowRefreshFeedback(checkedAt, hasUsageData: false);
            return;
        }

        double left = Math.Clamp(Math.Round(100 - snapshot.Long.UsedPercent), 0, 100);
        OverviewPercentText.Text = $"{left:0}%";
        OverviewRemainingBar.Width = 112 * left / 100;

        TimeSpan age = DateTimeOffset.Now - snapshot.EventTime;
        bool stale = age.TotalMinutes > 5;
        string freshness = stale ? "Data may be stale" : "Live";
        if (stale)
        {
            OverviewRemainingBar.Background = GrayBrush;
            OverviewPercentText.Foreground = MutedTextBrush;
        }
        else
        {
            OverviewRemainingBar.Background = left <= 10 ? RedBrush : left <= 30 ? AmberBrush : GreenBrush;
            OverviewPercentText.Foreground = TextBrush;
        }

        string resetText = FormatResetCountdown(snapshot.Long.ResetsAt);
        if (snapshot.Long.ResetsAt is long resetUnix)
        {
            DateTimeOffset reset = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
            OverviewTimeText.Text = $"WEEKLY · Resets {reset.ToString("MMM d", CultureInfo.InvariantCulture)}";
        }
        else
        {
            OverviewTimeText.Text = "WEEKLY · Reset unavailable";
        }

        double? shortLeft = UpdateShortUsage(snapshot.Short, stale);
        string shortText = shortLeft is null ? "--" : shortLeft.Value.ToString("0", CultureInfo.InvariantCulture);
        string shortResetText = snapshot.Short is null ? "unavailable" : FormatResetCountdown(snapshot.Short.ResetsAt);
        string tooltip = $"ChatGPT Codex - 7D {left:0}% left; resets {resetText}; 5H {shortText}% left; resets {shortResetText}; " +
                         $"{freshness}; Codex data {snapshot.EventTime:HH:mm:ss}; checked {checkedAt:HH:mm:ss}";
        if (!IsCodexRunning()) tooltip += "; Codex is not running";
        Root.ToolTip = tooltip;
        PercentText.Text = OverviewPercentText.Text;
        PercentText.Foreground = OverviewPercentText.Foreground;
        RemainingBar.Width = 108 * left / 100;
        RemainingBar.Background = OverviewRemainingBar.Background;
        TimeText.Text = OverviewTimeText.Text.Replace("WEEKLY · ", "Weekly ", StringComparison.Ordinal);
        PulseStatusForNewEvent(snapshot.EventTime);
        if (manualRequest) ShowRefreshFeedback(checkedAt, hasUsageData: true);
    }

    private double? UpdateShortUsage(RateLimit? shortLimit, bool stale)
    {
        if (shortLimit is null)
        {
            SetShortUsageUnavailable();
            return null;
        }

        double left = Math.Clamp(Math.Round(100 - shortLimit.UsedPercent), 0, 100);
        ShortPercentText.Text = $"{left:0}%";
        ShortRemainingBar.Width = 126 * left / 100;
        ShortTimeText.Text = $"5 HOURS · {FormatResetCountdown(shortLimit.ResetsAt)}";
        if (stale)
        {
            ShortRemainingBar.Background = GrayBrush;
            ShortPercentText.Foreground = MutedTextBrush;
        }
        else
        {
            ShortRemainingBar.Background = left <= 10 ? RedBrush : left <= 30 ? AmberBrush : BlueBrush;
            ShortPercentText.Foreground = TextBrush;
        }
        return left;
    }

    private void SetShortUsageUnavailable()
    {
        ShortTimeText.Text = "5 HOURS · Unavailable";
        ShortPercentText.Text = "--%";
        ShortPercentText.Foreground = MutedTextBrush;
        ShortRemainingBar.Width = 0;
        ShortRemainingBar.Background = GrayBrush;
    }

    private static string FormatResetCountdown(long? resetUnix)
    {
        if (resetUnix is not long value) return "Reset unavailable";
        TimeSpan remaining = DateTimeOffset.FromUnixTimeSeconds(value).ToLocalTime() - DateTimeOffset.Now;
        if (remaining.TotalSeconds <= 0) return "Ready to reset";
        return remaining.TotalDays >= 1
            ? $"Resets in {Math.Floor(remaining.TotalDays):0}d {remaining.Hours}h"
            : $"Resets in {Math.Floor(remaining.TotalHours):0}h {remaining.Minutes}m";
    }

    private void PulseStatusForNewEvent(DateTimeOffset eventTime)
    {
        if (_lastPulsedEventTime == eventTime) return;
        _lastPulsedEventTime = eventTime;
        StatusPulse.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0.72, 0, TimeSpan.FromMilliseconds(760)) { AutoReverse = false });
    }

    private void ShowRefreshFeedback(DateTimeOffset checkedAt, bool hasUsageData)
    {
        RefreshMenu.Header = hasUsageData
            ? $"Usage checked {checkedAt:HH:mm:ss}"
            : $"Checked {checkedAt:HH:mm:ss} - no data";
        _refreshFeedbackTimer.Stop();
        _refreshFeedbackTimer.Start();
    }

    private static bool IsCodexRunning()
    {
        try
        {
            return Process.GetProcessesByName("codex").Length > 0 ||
                   Process.GetProcessesByName("ChatGPT").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_state.Locked || e.ChangedButton != MouseButton.Left ||
            TaskbarModeSlider.IsMouseOver) return;
        // Refresh the monitor and DPI before taking the drag origin.
        SyncDisplayGeometry();
        PositionAnchoredWidget();
        _dragStartX = System.Windows.Forms.Cursor.Position.X;
        _dragStartLeft = _widgetLeft;
        _isDragging = Root.CaptureMouse();
        e.Handled = _isDragging;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag();
            return;
        }
        int deltaPixels = System.Windows.Forms.Cursor.Position.X - _dragStartX;
        RememberPosition(_dragStartLeft + deltaPixels);
        SyncDisplayGeometry();
        PositionAnchoredWidget();
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
        SaveState();
    }

    private void MoveToNextDisplay()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        int current = Array.FindIndex(screens, screen => screen.DeviceName == _selectedScreen?.DeviceName);
        var next = screens[(current + 1) % screens.Length];
        _state.ScreenDeviceName = next.DeviceName;
        _state.HorizontalOffset = 0;
        SyncDisplayGeometry();
        SetTaskbarPosition(_screenLeft);
    }

    private void RememberPosition(double left)
    {
        if (_selectedScreen is null) return;
        _widgetLeft = Math.Clamp(double.IsFinite(left) ? left : _screenLeft,
            _screenLeft, Math.Max(_screenLeft, _screenRight - PhysicalWidgetWidth()));
        _state.Left = _widgetLeft;
        _state.ScreenDeviceName = _selectedScreen.DeviceName;
        _state.ScreenIndex = Array.FindIndex(System.Windows.Forms.Screen.AllScreens,
            screen => screen.DeviceName == _selectedScreen.DeviceName);
        _state.HorizontalOffset = (_widgetLeft - _screenLeft) * _dipPerPixel;
    }

    private void SetTaskbarPosition(double left)
    {
        RememberPosition(left);
        SyncDisplayGeometry();
        PositionAnchoredWidget();
        SaveState();
    }

    private double PhysicalWidgetWidth() => _logicalWidgetWidth / _dipPerPixel;

    private void PositionAnchoredWidget()
    {
        if (!_placementReady || _windowHandle == 0 || _selectedScreen is null) return;
        var target = _targetBounds;
        // Keep WPF's layout size consistent with the native window size.
        Height = target.Height * _dipPerPixel;
        uint flags = NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow;
        if (NativeMethods.GetWindowRect(_windowHandle, out NativeMethods.Rect current))
        {
            if (current.Left == target.Left && current.Top == target.Top)
                flags |= NativeMethods.SwpNoMove;
            if (current.Right - current.Left == target.Width && current.Bottom - current.Top == target.Height)
                flags |= NativeMethods.SwpNoSize;
        }
        if (!NativeMethods.SetWindowPos(_windowHandle, NativeMethods.HwndTopmost,
            target.Left, target.Top, target.Width, target.Height, flags))
            WriteLog($"Could not position widget: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    private void SyncDisplayGeometry()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var preferred = Array.Find(screens, screen => screen.DeviceName == _state.ScreenDeviceName);
        var screen = preferred ??
            (_state.ScreenDeviceName is null && _state.ScreenIndex >= 0 && _state.ScreenIndex < screens.Length
                ? screens[_state.ScreenIndex]
                : Array.Find(screens, item => item.Primary) ?? screens[0]);
        _selectedScreen = screen;
        _dipPerPixel = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        if (!double.IsFinite(_dipPerPixel) || _dipPerPixel <= 0) _dipPerPixel = 1;
        _screenLeft = screen.Bounds.Left;
        _screenRight = screen.Bounds.Right;

        if (_state.ScreenDeviceName is null || _state.HorizontalOffset is null)
        {
            // Migrate absolute coordinates once. Do not persist temporary monitor fallbacks.
            _state.ScreenDeviceName = screen.DeviceName;
            _state.HorizontalOffset = WidgetPlacement.MigrateLegacyOffset(_state.Left, _screenLeft, _dipPerPixel);
        }

        _taskbarHandle = NativeMethods.FindTaskbarForBounds(new NativeMethods.Rect
        {
            Left = screen.Bounds.Left, Top = screen.Bounds.Top,
            Right = screen.Bounds.Right, Bottom = screen.Bounds.Bottom
        });
        System.Drawing.Rectangle? taskbar = null;
        if (_taskbarHandle != 0 && NativeMethods.GetWindowRect(_taskbarHandle, out NativeMethods.Rect rect))
            taskbar = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        _targetBounds = WidgetPlacement.Calculate(screen.Bounds, screen.WorkingArea, taskbar,
            _state.HorizontalOffset ?? 0, 1 / _dipPerPixel, _logicalWidgetWidth);
        _widgetLeft = _targetBounds.Left;
    }

    private static SolidColorBrush Brush(string color) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
}
