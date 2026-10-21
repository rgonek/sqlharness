# SQLHarness developer tasks. Run `just` to list them.

set windows-shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
set shell := ["pwsh", "-NoLogo", "-NoProfile", "-Command"]

# List recipes
default:
    @just --list

# Run the local CI gate (UI check, build, tests, format check of the changed C# files)
verify *args:
    pwsh ./scripts/verify.ps1 {{args}}

# Run the CI gate on Linux inside WSL
verify-linux *args:
    pwsh ./scripts/verify-linux.ps1 {{args}}

# Rewrite the formatting of the C# files this working tree changed (what the gates check)
fix:
    pwsh ./scripts/format-changed.ps1 -Fix

# Test, publish, and install on PATH; use `just publish fast` to skip tests (override with SQLHARNESS_INSTALL_DIR)
publish *args:
    pwsh ./scripts/publish-local.ps1 {{if args == "fast" { "-SkipTests" } else { args }}}

# Publish and install without running the tests first
publish-fast:
    pwsh ./scripts/publish-local.ps1 -SkipTests

# Delete sqlharness.previous-* binaries left by earlier installs (files still in use are kept)
clean-previous:
    $dir = if ($env:SQLHARNESS_INSTALL_DIR) { $env:SQLHARNESS_INSTALL_DIR } else { Split-Path -Parent (Get-Command sqlharness -CommandType Application).Source }; Get-ChildItem $dir -Filter 'sqlharness.previous-*' | ForEach-Object { try { Remove-Item $_.FullName -ErrorAction Stop; "removed $($_.Name)" } catch { "in use: $($_.Name)" } }
