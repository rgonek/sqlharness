# SQLHarness developer tasks. Run `just` to list them.

set windows-shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
set shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]

# List recipes
default:
    @just --list

# Run the local CI gate (UI check, build, tests, format check)
verify:
    pwsh ./scripts/verify.ps1

# Run the CI gate on Linux inside WSL
verify-linux:
    pwsh ./scripts/verify-linux.ps1

# Test, publish a single-file binary, and install it where sqlharness is on PATH (override with SQLHARNESS_INSTALL_DIR)
publish *args:
    pwsh ./scripts/publish-local.ps1 {{args}}

# Publish and install without running the tests first
publish-fast:
    pwsh ./scripts/publish-local.ps1 -SkipTests

# Delete sqlharness.previous-* binaries left by earlier installs (files still in use are kept)
clean-previous:
    $dir = if ($env:SQLHARNESS_INSTALL_DIR) { $env:SQLHARNESS_INSTALL_DIR } else { Split-Path -Parent (Get-Command sqlharness -CommandType Application).Source }; Get-ChildItem $dir -Filter 'sqlharness.previous-*' | ForEach-Object { try { Remove-Item $_.FullName -ErrorAction Stop; "removed $($_.Name)" } catch { "in use: $($_.Name)" } }
