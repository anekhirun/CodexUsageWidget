param(
    [Parameter()]
    [string]$WidgetPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'CodexTaskbarWidget.exe'),

    [Parameter()]
    [ValidateRange(0, [int]::MaxValue)]
    [int]$CodexPid = 0,

    [Parameter()]
    [ValidateRange(1, 60)]
    [int]$InitialWaitSeconds = 15,

    [Parameter()]
    [ValidateRange(1, 60)]
    [int]$CloseWaitSeconds = 15
)

$ErrorActionPreference = 'Stop'
$watchdogMutex = $null
$ownsWatchdogMutex = $false
$logDirectory = Join-Path $env:APPDATA 'CodexUsageWidget'
$logPath = Join-Path $logDirectory 'watchdog.log'

function Write-WatchdogLog {
    param([Parameter(Mandatory)][string]$Message)

    try {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        Add-Content -LiteralPath $logPath -Value "$(Get-Date -Format o) $Message"
    }
    catch {
    }
}

function Get-CodexDesktopProcess {
    param(
        [Parameter(Mandatory)][int]$RequestedPid,
        [Parameter(Mandatory)][int]$WaitSeconds
    )

    if ($RequestedPid -gt 0) {
        return Get-Process -Id $RequestedPid -ErrorAction Stop
    }

    $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $candidate = Get-CimInstance Win32_Process -Filter "Name = 'ChatGPT.exe'" |
            Where-Object {
                $_.ExecutablePath -match '(?i)\\WindowsApps\\OpenAI\.Codex_' -and
                ([string]::IsNullOrWhiteSpace($_.CommandLine) -or
                 $_.CommandLine -notmatch '(?i)(^|\s)--type=')
            } |
            Sort-Object CreationDate |
            Select-Object -First 1

        if ($null -ne $candidate) {
            return Get-Process -Id $candidate.ProcessId -ErrorAction Stop
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    return $null
}

if (-not ('CodexUsageWidget.NativeWindowController' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexUsageWidget
{
    public static class NativeWindowController
    {
        private const uint WmClose = 0x0010;

        private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(
            IntPtr window,
            StringBuilder text,
            int maximumCount);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(
            IntPtr window,
            uint message,
            IntPtr wParam,
            IntPtr lParam);

        public static bool RequestClose(int widgetProcessId)
        {
            IntPtr widgetWindow = FindWidgetWindow(widgetProcessId);
            return widgetWindow != IntPtr.Zero &&
                   PostMessage(widgetWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        public static bool IsSafeTopLevelWidget(int widgetProcessId)
        {
            IntPtr widgetWindow = FindWidgetWindow(widgetProcessId);
            return widgetWindow == IntPtr.Zero || GetParent(widgetWindow) == IntPtr.Zero;
        }

        private static IntPtr FindWidgetWindow(int expectedProcessId)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows((window, parameter) =>
            {
                uint processId;
                GetWindowThreadProcessId(window, out processId);
                if (processId != (uint)expectedProcessId) return true;

                var title = new StringBuilder(256);
                GetWindowText(window, title, title.Capacity);
                if (String.Equals(
                    title.ToString(),
                    "ChatGPT Codex Usage Widget",
                    StringComparison.Ordinal))
                {
                    result = window;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return result;
        }


    }
}
'@
}

function Request-WidgetClose {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process]$WidgetProcess,
        [Parameter(Mandatory)][int]$WaitSeconds
    )

    $posted = [CodexUsageWidget.NativeWindowController]::RequestClose($WidgetProcess.Id)
    if ($posted -and $WidgetProcess.WaitForExit($WaitSeconds * 1000)) {
        Write-WatchdogLog "Widget PID $($WidgetProcess.Id) closed cleanly."
        return $true
    }

    if (-not [CodexUsageWidget.NativeWindowController]::IsSafeTopLevelWidget($WidgetProcess.Id)) {
        Write-WatchdogLog "Widget PID $($WidgetProcess.Id) is not top-level; it was left running."
        return $false
    }

    Stop-Process -Id $WidgetProcess.Id -ErrorAction Stop
    if (-not $WidgetProcess.WaitForExit(5000)) {
        Write-WatchdogLog "Widget PID $($WidgetProcess.Id) could not be stopped."
        return $false
    }
    Write-WatchdogLog "Widget PID $($WidgetProcess.Id) was stopped after its clean-close timeout."
    return $true
}

try {
    $resolvedWidgetPath = [IO.Path]::GetFullPath($WidgetPath)
    if (-not (Test-Path -LiteralPath $resolvedWidgetPath -PathType Leaf)) {
        throw "Widget executable was not found at $resolvedWidgetPath"
    }

    $watchdogMutex = [Threading.Mutex]::new(
        $true,
        'Local\CodexUsageWidgetWatchdog',
        [ref]$ownsWatchdogMutex)
    if (-not $ownsWatchdogMutex) {
        Write-WatchdogLog 'Another Codex widget watchdog is already active.'
        exit 0
    }

    $codexProcess = Get-CodexDesktopProcess -RequestedPid $CodexPid -WaitSeconds $InitialWaitSeconds
    if ($null -eq $codexProcess) {
        Write-WatchdogLog 'The main Codex desktop process was not found; the widget was not started.'
        exit 0
    }

    $widgetProcess = Get-Process -Name 'CodexTaskbarWidget' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $resolvedWidgetPath } |
        Sort-Object StartTime |
        Select-Object -First 1
    if ($null -eq $widgetProcess) {
        $widgetProcess = Start-Process -FilePath $resolvedWidgetPath `
            -WorkingDirectory (Split-Path -Parent $resolvedWidgetPath) `
            -PassThru
        Write-WatchdogLog "Started widget PID $($widgetProcess.Id) for Codex PID $($codexProcess.Id)."
    }
    else {
        Write-WatchdogLog "Using widget PID $($widgetProcess.Id) for Codex PID $($codexProcess.Id)."
    }

    # Placement belongs exclusively to the widget. Moving Codex must not move it.
    while (-not $codexProcess.HasExited -and -not $widgetProcess.HasExited) {
        $codexProcess.Refresh()
        $null = $codexProcess.WaitForExit(500)
    }

    if ($widgetProcess.HasExited) {
        Write-WatchdogLog "Widget PID $($widgetProcess.Id) exited while Codex PID $($codexProcess.Id) was active."
        exit 0
    }

    Write-WatchdogLog "Codex PID $($codexProcess.Id) exited; requesting widget shutdown."
    $null = Request-WidgetClose -WidgetProcess $widgetProcess -WaitSeconds $CloseWaitSeconds
}
catch {
    Write-WatchdogLog "Watchdog failed: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($ownsWatchdogMutex -and $null -ne $watchdogMutex) {
        try {
            $watchdogMutex.ReleaseMutex()
        }
        catch {
        }
    }
    if ($null -ne $watchdogMutex) {
        $watchdogMutex.Dispose()
    }
}
