#Requires -Version 7.0
<#
.SYNOPSIS
    Provisions the WSL2 Linux gate that scripts/verify-linux.ps1 runs.

.DESCRIPTION
    Checks that a WSL2 distribution is reachable, installs the .NET SDK version
    pinned by global.json into ~/.dotnet, installs the Node version pinned by
    .nvmrc into ~/.node (checksum-verified against nodejs.org SHASUMS256.txt,
    with ~/.node/current pointing at it), puts both on PATH for interactive
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

function Assert-WslDistroVersion2 {
    param(
        [Parameter(Mandatory)][string]$Distro
    )

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = 'wsl.exe'
    $psi.Arguments = '-l -v'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $psi.StandardErrorEncoding = [System.Text.Encoding]::Unicode
    $psi.CreateNoWindow = $true

    $proc = [System.Diagnostics.Process]::Start($psi)
    $stdout = $proc.StandardOutput.ReadToEnd()
    $stderr = $proc.StandardError.ReadToEnd()
    $proc.WaitForExit()

    if ($proc.ExitCode -ne 0) {
        throw "Could not list WSL distributions. Ensure WSL is installed and the '$Distro' distribution is available. Details: $stderr"
    }

    $found = $false
    $lines = $stdout -split "`r?`n" | Where-Object { $_ -match '\S' }
    foreach ($line in $lines) {
        if ($line -match '^\s*NAME\s+STATE\s+VERSION\s*$') { continue }
        $pattern = '^\s*\*?\s*{0}\s+\S+\s+(\d+)\s*$' -f [regex]::Escape($Distro)
        if ($line -match $pattern) {
            if ($matches[1] -ne '2') {
                throw "WSL distribution '$Distro' is version $($matches[1]), but version 2 is required."
            }
            $found = $true
            break
        }
    }

    if (-not $found) {
        throw "WSL distribution '$Distro' was not found. Run 'wsl -l -v' to list distributions, then re-run with -Distro <name>."
    }
}

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
Assert-WslDistroVersion2 -Distro $Distro

$globalJson = Get-Content -LiteralPath $globalJsonPath -Raw | ConvertFrom-Json
$sdkVersion = $globalJson.sdk.version
if ([string]::IsNullOrWhiteSpace($sdkVersion)) {
    throw "global.json does not pin sdk.version."
}

$nvmrcPath = Join-Path $repoRoot '.nvmrc'
$nodeVersion = (Get-Content -LiteralPath $nvmrcPath -Raw).Trim()
if ($nodeVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw ".nvmrc must pin an exact Node version (for example 24.18.0)."
}

$repoRootUnix = $repoRoot -replace '\\', '/'
$windowsRepo = (& wsl -d $Distro -- wslpath -a $repoRootUnix).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($windowsRepo)) {
    throw "Could not translate the repository path '$repoRoot' into a WSL path."
}

$wslHome = (& wsl -d $Distro -- bash -lc 'printf %s "$HOME"').Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslHome)) {
    throw "Could not determine the WSL home directory for distro '$Distro'."
}
$gateClone = "$wslHome/src/sqlharness-gate"

$provision = @'
set -euo pipefail

sdk_version="$1"
bashrc_marker="$2"
windows_repo="$3"
gate_clone="$4"
node_version="$5"
dotnet_dir="$HOME/.dotnet"

missing=()
for tool in curl git; do
  command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
done
dpkg -s ca-certificates >/dev/null 2>&1 || missing+=("ca-certificates")
dpkg -s xz-utils >/dev/null 2>&1 || missing+=("xz-utils")
if [ "${#missing[@]}" -gt 0 ]; then
  if ! sudo -n true 2>/dev/null; then
    echo "sudo needs a password; install these manually and re-run: ${missing[*]}" >&2
    exit 1
  fi
  sudo -n apt-get update
  sudo -n apt-get install -y ca-certificates "${missing[@]}"
fi

if [ -x "$dotnet_dir/dotnet" ]; then
  installed_version="$("$dotnet_dir/dotnet" --version)"
  if [ "$installed_version" = "$sdk_version" ]; then
    echo "==> sdk already provisioned ($sdk_version)"
  else
    echo "==> sdk $sdk_version (installed $installed_version differs; installing side by side)"
    installer="$(mktemp)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --version "$sdk_version" --install-dir "$dotnet_dir" --no-path
    rm -f "$installer"
  fi
else
  echo "==> sdk $sdk_version"
  installer="$(mktemp)"
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
  bash "$installer" --version "$sdk_version" --install-dir "$dotnet_dir" --no-path
  rm -f "$installer"
fi

node_root="$HOME/.node"
node_dir="$node_root/v$node_version"
if [ -x "$node_dir/bin/node" ]; then
  echo "==> node already provisioned ($node_version)"
else
  echo "==> node $node_version"
  case "$(uname -m)" in
    x86_64) node_arch=x64 ;;
    aarch64) node_arch=arm64 ;;
    *) echo "Unsupported architecture $(uname -m) for Node." >&2; exit 1 ;;
  esac
  tarball="node-v$node_version-linux-$node_arch.tar.xz"
  work="$(mktemp -d)"
  curl -fsSL "https://nodejs.org/dist/v$node_version/$tarball" -o "$work/$tarball"
  curl -fsSL "https://nodejs.org/dist/v$node_version/SHASUMS256.txt" -o "$work/SHASUMS256.txt"
  (cd "$work" && grep " $tarball\$" SHASUMS256.txt | sha256sum -c -)
  # Extract beside the download and move into place only when complete, so a failed
  # extract never leaves a bin/node that the next run mistakes for a finished install.
  mkdir -p "$work/node" "$node_root"
  tar -xJf "$work/$tarball" -C "$work/node" --strip-components=1
  rm -rf "$node_dir"
  mv "$work/node" "$node_dir"
  rm -rf "$work"
fi
ln -sfn "$node_dir" "$node_root/current"

if grep -qxF "# $bashrc_marker" "$HOME/.bashrc" 2>/dev/null; then
  echo "==> path already configured"
else
  echo "==> path"
  {
    echo ""
    echo "# $bashrc_marker"
    echo 'export PATH="$HOME/.dotnet:$PATH"'
  } >> "$HOME/.bashrc"
fi

node_marker="$bashrc_marker (node)"
if grep -qxF "# $node_marker" "$HOME/.bashrc" 2>/dev/null; then
  echo "==> node path already configured"
else
  echo "==> node path"
  {
    echo ""
    echo "# $node_marker"
    echo 'export PATH="$HOME/.node/current/bin:$PATH"'
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

fstype="$(findmnt -T "$gate_clone" -no FSTYPE 2>/dev/null || df -T "$gate_clone" | awk 'NR==2 {print $2}')"
case "$fstype" in
  ext4|xfs) echo "==> filesystem $fstype" ;;
  *)
    echo "The gate clone is on an unsupported filesystem ($fstype at $gate_clone). Only ext4 and xfs are accepted." >&2
    exit 1
    ;;
esac

"$dotnet_dir/dotnet" --version
'@

$provisionFile = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($provisionFile.FullName, ($provision -replace "`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
    $provisionWslPath = (& wsl -d $Distro -- wslpath -a ($provisionFile.FullName -replace '\\', '/')).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($provisionWslPath)) {
        throw "Could not translate the temporary provision file path '$($provisionFile.FullName)' into a WSL path."
    }
    [array]$provisionOutput = & wsl -d $Distro -- bash $provisionWslPath $sdkVersion $bashrcMarker $windowsRepo $gateClone $nodeVersion
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $provisionFile.FullName -ErrorAction SilentlyContinue
}
if ($exitCode -ne 0) {
    throw "Linux gate provisioning failed with exit code $exitCode."
}

$provisionOutput | ForEach-Object { Write-Host $_ }
$fstype = ($provisionOutput | Select-String '^==> filesystem (\S+)$' | Select-Object -Last 1).Matches.Groups[1].Value
if ([string]::IsNullOrWhiteSpace($fstype)) {
    $fstype = 'unknown'
}
Write-Host "setup-linux-gate: OK ($gateClone, fstype: $fstype)"