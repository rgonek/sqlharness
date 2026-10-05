#Requires -Version 7.0
<#
.SYNOPSIS
    Provisions the WSL2 Linux gate that scripts/verify-linux.ps1 runs.

.DESCRIPTION
    Checks that a WSL2 distribution is reachable, installs the .NET SDK version
    pinned by global.json into ~/.dotnet, puts ~/.dotnet on PATH for interactive
    shells, and clones this repository into ~/src/sqlharness-gate.

    The clone lives on the WSL ext4 filesystem on purpose: /mnt/d is
    case-insensitive, so a gate run from there could not observe the
    case-sensitivity failures the gate exists to catch. The script proves the
    filesystem is case-sensitive and fails closed when it is not.

    Idempotent: a second run reports what already exists and changes nothing.

    Fail-closed: never blocks on interactive input, never writes ~/.sqlharness,
    never connects to a database.

.NOTES
    Requires WSL2. Override the distribution with -Distro.
#>
[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$globalJsonPath = Join-Path $repoRoot 'global.json'
$bashrcMarker = 'sqlharness-linux-gate-path'

if (-not (Get-Command wsl -ErrorAction SilentlyContinue)) {
    throw "wsl was not found. Install WSL2 with 'wsl --install' and re-run this script."
}

& wsl -d $Distro -- true 2>$null
if ($LASTEXITCODE -ne 0) {
    throw "WSL distribution '$Distro' is unavailable. Run 'wsl --list --verbose' to list distributions, then re-run with -Distro <name>."
}

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw "global.json does not pin sdk.version."
}

$repoRootUnix = $repoRoot -replace '\\', '/'
$windowsRepo = (& wsl -d $Distro -- wslpath -a $repoRootUnix).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsRepo)) {
    throw "Could not translate the repository path '$repoRoot' into a WSL path."
}

$wslHome = (& wsl -d $Distro -- bash -lc 'printf %s "$HOME"').Trim()
$gateClone = "$wslHome/src/sqlharness-gate"

$provision = @'
set -euo pipefail

sdk_version="$1"
bashrc_marker="$2"
windows_repo="$3"
gate_clone="$4"
dotnet_dir="$HOME/.dotnet"

missing=()
for tool in curl git; do
  command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done
if [ "${#missing[@]}" -gt 0 ]; then
  if ! sudo -n true 2>/dev/null; then
    echo "sudo needs a password; install these manually and re-run: ${missing[*]}" >&2
    exit 1
  fi
  sudo apt-get update
  sudo apt-get install -y ca-certificates "${missing[@]}"
fi

if [ -x "$dotnet_dir/dotnet" ]; then
  echo "==> sdk already provisioned"
else
  echo "==> sdk $sdk_version"
  installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --version "$sdk_version" --install-dir "$dotnet_dir" --no-path
  rm -f "$installer"
fi

if grep -qF "$bashrc_marker" "$HOME/.bashrc" 2>/dev/null; then
  echo "==> path already configured"
else
  echo "==> path"
  {
    echo ""
    echo "# $bashrc_marker"
    echo 'export PATH="$HOME/.dotnet:$PATH"'
  } >> "$HOME/.bashrc"
fi

if [ -d "$gate_clone/.git" ]; then
  echo "==> clone already present at $gate_clone"
else
  echo "==> clone $windows_repo -> $gate_clone"
  mkdir -p "$(dirname "$gate_clone")"
  git clone "$windows_repo" "$gate_clone"
fi
git -C "$gate_clone" config core.autocrlf false

probe="$gate_clone/.sqlharness-case-probe"
: > "$probe"
if [ -e "${probe^^}" ]; then
  rm -f "$probe"
  echo "The gate clone is on a case-insensitive filesystem ($gate_clone). Move it onto the WSL ext4 filesystem." >&2
  exit 1
fi
rm -f "$probe"

"$dotnet_dir/dotnet" --version
'@

$provisionFile = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($provisionFile.FullName, ($provision -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
    $provisionWslPath = (& wsl -d $Distro -- wslpath -a ($provisionFile.FullName -replace '\\', '/')).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($provisionWslPath)) {
        throw "Could not translate the temporary provision file path '$($provisionFile.FullName)' into a WSL path."
    }
    & wsl -d $Distro -- bash $provisionWslPath $sdkVersion $bashrcMarker $windowsRepo $gateClone
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $provisionFile.FullName -ErrorAction SilentlyContinue
}
if ($exitCode -ne 0) {
    throw "Linux gate provisioning failed with exit code $exitCode."
}

Write-Host "setup-linux-gate: OK ($gateClone)"
