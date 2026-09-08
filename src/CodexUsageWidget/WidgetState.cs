namespace CodexUsageWidget;

internal sealed class WidgetState
{
    public double Left { get; set; }
    public bool CompactLayout { get; set; }
    public bool Locked { get; set; }
    public int ScreenIndex { get; set; } = -1;
    public string? ScreenDeviceName { get; set; }
    public double? HorizontalOffset { get; set; }
    public string RefreshMode { get; set; } = "30 seconds";
    public int LayoutVersion { get; set; } = 2;
}
