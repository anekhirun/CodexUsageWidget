using System.Runtime.InteropServices;
using System.Text;

namespace CodexUsageWidget;

internal static class NativeMethods
{
    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    internal static readonly nint HwndTopmost = new(-1);
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    internal static nint FindTaskbarForBounds(Rect screenBounds)
    {
        nint bestWindow = 0;
        long bestOverlap = 0;
        EnumWindows((window, _) =>
        {
            var className = new StringBuilder(64);
            if (GetClassName(window, className, className.Capacity) == 0) return true;
            string value = className.ToString();
            if (value is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return true;
            if (!GetWindowRect(window, out Rect taskbarRect)) return true;

            int overlapWidth = Math.Max(0, Math.Min(screenBounds.Right, taskbarRect.Right) -
                                           Math.Max(screenBounds.Left, taskbarRect.Left));
            int overlapHeight = Math.Max(0, Math.Min(screenBounds.Bottom, taskbarRect.Bottom) -
                                            Math.Max(screenBounds.Top, taskbarRect.Top));
            long overlap = (long)overlapWidth * overlapHeight;
            if (overlap <= bestOverlap) return true;
            bestOverlap = overlap;
            bestWindow = window;
            return true;
        }, 0);

        // A different monitor's primary taskbar must never supply our Y coordinate.
        return bestWindow;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint window, out Rect rect);
}
