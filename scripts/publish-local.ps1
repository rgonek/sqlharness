#Requires -Version 7.0
<#
.SYNOPSIS
    Publishes a self-contained single-file sqlharness and installs it on this machine.

.DESCRIPTION
    Builds the same binary as the release workflow (self-contained, single file,
    untrimmed) for the current OS and architecture, then copies it into the install
    directory. The install directory is, in order: -InstallDir, the
    SQLHARNESS_INSTALL_DIR environment variable, the directory of the sqlharness
    currently on PATH, or ~/.sqlharness/bin.

    A running sqlharness (for example an MCP server or the dashboard) keeps its
    binary open, so the installed binary is renamed to
    sqlharness.previous-<timestamp> before the new one is copied in. Running
    processes keep using the old file; new invocations use the new one.

.PARAMETER InstallDir
    Directory to install into; overrides SQLHARNESS_INSTALL_DIR and PATH discovery.

.PARAMETER Runtime
    .NET runtime identifier (for example win-x64, linux-x64, osx-arm64); defaults to this machine.

.PARAMETER SkipTests
    Skip the unit tests that run before publishing.
#>
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$Runtime,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $Runtime) {
    $os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Runtime = "$os-$arch"
}
$exeName = if ($Runtime.StartsWith('win-')) { 'sqlharness.exe' } else { 'sqlharness' }

if (-not $InstallDir) {
    $InstallDir = $env:SQLHARNESS_INSTALL_DIR
}
if (-not $InstallDir) {
    $current = Get-Command sqlharness -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($current) {
        $InstallDir = Split-Path -Parent $current.Source
    }
}
if (-not $InstallDir) {
    $InstallDir = Join-Path $HOME '.sqlharness/bin'
}

if (-not $SkipTests) {
    Write-Host '==> test'
    dotnet test (Join-Path $repo 'SqlHarness.sln') -c Release --filter 'FullyQualifiedName!~Integration'
    if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)." }
}

Write-Host "==> publish $Runtime"
$out = Join-Path $repo 'artifacts/publish-single'
if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}
dotnet publish (Join-Path $repo 'src/SqlHarness.Cli') -c Release -r $Runtime `
    --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

Write-Host "==> install $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
$target = Join-Path $InstallDir $exeName
if (Test-Path $target) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $previous = Join-Path $InstallDir ("sqlharness.previous-$stamp" + [IO.Path]::GetExtension($exeName))
    Move-Item -Path $target -Destination $previous
    Write-Host "Previous binary kept as $previous"
}
Copy-Item -Path (Join-Path $out $exeName) -Destination $target
if (-not $IsWindows) {
    chmod +x $target
}

& $target --version
if ($LASTEXITCODE -ne 0) { throw "The installed binary did not start (exit code $LASTEXITCODE)." }
Write-Host "Installed $target"
if (-not (($env:PATH -split [IO.Path]::PathSeparator) -contains $InstallDir)) {
    Write-Warning "$InstallDir is not on PATH."
}
