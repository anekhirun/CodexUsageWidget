$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
foreach ($relativePath in @('scripts\watch-codex.ps1', 'Install Codex Watchdog.ps1', 'Uninstall Codex Watchdog.ps1')) {
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repositoryRoot $relativePath), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
    if ($relativePath -eq 'scripts\watch-codex.ps1') {
        $embeddedCode = $ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $node.Value -like '*public static class NativeWindowController*'
        }, $true)
        if ($embeddedCode.Count -ne 1) { throw 'Expected one native lifecycle controller.' }
        Add-Type -TypeDefinition $embeddedCode[0].Value
        if ([CodexUsageWidget.NativeWindowController].GetMethod('Follow')) {
            throw 'The lifecycle watchdog must not move the widget.'
        }
        if ([CodexUsageWidget.NativeWindowController]::RequestClose(-1)) {
            throw 'Closing a nonexistent process must fail safely.'
        }
    }
}
Write-Output 'PowerShell syntax and embedded native lifecycle controller passed. No tasks, startup entries, or running apps were changed.'
