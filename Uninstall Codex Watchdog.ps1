param(
    [Parameter()]
    [string]$TaskName = 'Codex Usage Widget Watchdog'
)

$ErrorActionPreference = 'Stop'

$codexIsRunning = Get-CimInstance Win32_Process -Filter "Name = 'ChatGPT.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $_.ExecutablePath -match '(?i)\\WindowsApps\\OpenAI\.Codex_' -and
        ([string]::IsNullOrWhiteSpace($_.CommandLine) -or
         $_.CommandLine -notmatch '(?i)(^|\s)--type=')
    } |
    Select-Object -First 1
if ($null -ne $codexIsRunning) {
    throw 'Close the Codex desktop app before uninstalling its widget watchdog.'
}

$widgetProcess = Get-Process -Name 'CodexTaskbarWidget' -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($null -ne $widgetProcess) {
    throw 'Close the Codex usage widget before uninstalling its watchdog.'
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    Disable-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue | Out-Null
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$runtimeParent = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'CodexUsageWidget'))
$runtimeRoot = [IO.Path]::GetFullPath(
    (Join-Path $runtimeParent 'watchdog'))
$requiredPrefix = $runtimeParent.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $runtimeRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove an unexpected runtime path: $runtimeRoot"
}

if (Test-Path -LiteralPath $runtimeRoot -PathType Container) {
    Remove-Item -LiteralPath $runtimeRoot -Recurse -Force
}

Write-Host "Removed scheduled watchdog: $TaskName"
Write-Host 'The widget executable and its preferences were not changed.'
