param([string]$DotnetPath = 'dotnet', [switch]$SkipTests, [string]$IsccPath)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This application requires Windows.' }
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
    $version = [string]$props.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
    $buildId = $version + '-' + [Guid]::NewGuid().ToString('N')
    $publishRoot = Join-Path $projectRoot "artifacts\release\$buildId\win-x64"
    $packageRoot = Join-Path $projectRoot 'artifacts\packages'
    New-Item -ItemType Directory -Path $publishRoot,$packageRoot -Force | Out-Null
    $running = @(Get-Process StartupManager,StartupManager.PrivilegedHelper -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ([IO.Path]::GetDirectoryName($_.Path) -eq $publishRoot) })
    if ($running.Count -gt 0) { throw 'Please close the app and its helper before publishing to this directory.' }
    & $DotnetPath build StartupManager.sln -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipTests) {
        & $DotnetPath run --project tests/StartupManager.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    & $DotnetPath publish src/StartupManager.App -c Release -r win-x64 --self-contained true -o $publishRoot -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }
    & $DotnetPath publish src/StartupManager.PrivilegedHelper -c Release -r win-x64 --self-contained true -o $publishRoot -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Helper publish failed' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot -Force
    if (Test-Path -LiteralPath (Join-Path $projectRoot 'VALIDATION.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot 'VALIDATION.md') -Destination $publishRoot -Force
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'CHANGELOG.md') -Destination $publishRoot
    foreach ($required in @('StartupManager.exe', 'StartupManager.PrivilegedHelper.exe', 'StartupManager.runtimeconfig.json', 'StartupManager.PrivilegedHelper.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $required))) { throw "Incomplete package: $required" }
    }
    $zip = Join-Path $packageRoot "StartupManager-$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $zip -Force
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ascii
    if ($IsccPath) {
        & $IsccPath "/DPublishDir=$publishRoot" "/DAppVersion=$version" (Join-Path $projectRoot 'installer/StartupManager.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
        $setup = Join-Path $packageRoot "StartupManager-$version-win-x64-Setup.exe"
        $setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
        "$setupHash  $([IO.Path]::GetFileName($setup))" | Set-Content -LiteralPath ($setup + '.sha256') -Encoding ascii
    }
    Write-Host "Package: $zip"
    Write-Host "Ready: $publishRoot\StartupManager.exe"
} finally { Pop-Location }
