param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'))

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $sourceCommit = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
    if (git status --porcelain) { throw 'Release packaging requires a clean checkout of the source commit.' }
    $sdkVersion = (dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Install the .NET SDK pinned in global.json.' }
    $outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
    $packageDirectory = Join-Path $outputRoot ('package-' + $sourceCommit.Substring(0, 12))
    if (Test-Path -LiteralPath $packageDirectory) { throw "Use a fresh output directory: $packageDirectory" }
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
    dotnet restore src\CodexUsageWidget\CodexUsageWidget.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    dotnet publish src\CodexUsageWidget\CodexUsageWidget.csproj -c Release --no-restore `
        -p:SourceRevisionId=$sourceCommit -o $packageDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    foreach ($name in @('README.md', 'CHANGELOG.md', 'VERSION.txt', 'Install Codex Watchdog.ps1',
        'Uninstall Codex Watchdog.ps1', 'Install Automatic Startup.vbs', 'Install Taskbar Startup.ps1',
        'Start Taskbar Delayed.vbs', 'Uninstall Taskbar Startup.ps1')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination $packageDirectory
    }
    New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'scripts') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'watch-codex.ps1') -Destination (Join-Path $packageDirectory 'scripts')
    foreach ($folder in @('docs', 'screenshots')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $folder) -Destination $packageDirectory -Recurse
    }
    $executable = Join-Path $packageDirectory 'CodexTaskbarWidget.exe'
    $executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    [ordered]@{
        sourceCommit = $sourceCommit
        sourceDirty = $false
        sdkVersion = $sdkVersion
        targetFramework = 'net8.0-windows'
        runtimeIdentifier = 'win-x64'
        productVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
        executableSha256 = $executableHash
        buildCommand = 'scripts/Build-Release.ps1'
        os = [Environment]::OSVersion.VersionString
        builtAtUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'build-provenance.json') -Encoding UTF8
    $archive = Join-Path $outputRoot ('CodexUsageWidget-win-x64-' + $sourceCommit.Substring(0, 12) + '.zip')
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archive
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    "$archiveHash  $([IO.Path]::GetFileName($archive))" |
        Set-Content -LiteralPath ($archive + '.sha256') -Encoding ASCII
    Write-Output "Package: $archive"
    Write-Output "Executable SHA-256: $executableHash"
}
finally { Pop-Location }
