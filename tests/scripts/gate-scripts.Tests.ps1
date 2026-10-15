# Pester 3.4-compatible unit tests for the gate scripts.
# Every script must parse: nothing else in the suite executes them, so a typo in
# one shows up as a gate that dies with a ParserError instead of a test failure.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:ScriptsDirectory = Join-Path $script:RepoRoot 'scripts'

Describe 'scripts\*.ps1' {
    It 'every script in scripts\ parses without errors' {
        $failures = @()
        foreach ($file in Get-ChildItem -LiteralPath $script:ScriptsDirectory -Filter '*.ps1' -File) {
            $errors = $null
            $tokens = $null
            $null = [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
            foreach ($parseError in @($errors)) {
                $failures += "$($file.Name) line $($parseError.Extent.StartLineNumber): $($parseError.Message)"
            }
        }

        if ($failures.Count -gt 0) {
            throw ('PowerShell scripts do not parse: ' + ($failures -join '; '))
        }
    }
}

Describe 'the local gates' {
    It 'runs the same four .NET stages plus the two UI stages' {
        $gates = @('verify.ps1', 'verify-linux.ps1') | ForEach-Object { [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory $_)) }

        foreach ($gate in $gates) {
            foreach ($verb in @('restore', 'build', 'test', 'format')) {
                if ($gate -notmatch "dotnet $verb ") { throw "a gate is missing the '$verb' stage" }
            }
            foreach ($uiCommand in @('npm run check', 'npm ci')) {
                if ($gate -notmatch [regex]::Escape($uiCommand)) { throw "a gate is missing '$uiCommand'" }
            }
        }
    }

    It 'skips the UI install when the lockfile is unchanged, in both gates' {
        # npm ci took 64s and 54s per run for an unchanged tree.
        $windows = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify.ps1'))
        $linux = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify-linux.ps1'))
        $installer = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'ui-install.ps1'))

        if ($windows -notmatch 'Get-UiInstallState') { throw 'the Windows gate always reinstalls the UI' }
        if ($linux -notmatch '\.sqlharness-install-stamp') { throw 'the Linux gate always reinstalls the UI' }
        if ($installer -notmatch '\.sqlharness-install-stamp') { throw 'the installer does not define the shared stamp name' }
    }
}