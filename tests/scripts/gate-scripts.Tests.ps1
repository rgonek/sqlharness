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
    It 'runs the same three .NET stages plus the two UI stages' {
        $gates = @('verify.ps1', 'verify-linux.ps1') | ForEach-Object { [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory $_)) }

        foreach ($gate in $gates) {
            foreach ($verb in @('build', 'test', 'format')) {
                if ($gate -notmatch "dotnet $verb ") { throw "a gate is missing the '$verb' stage" }
            }
            # dotnet build restores implicitly, so a separate restore stage is duplicated work.
            if ($gate -match 'dotnet restore') { throw 'a gate still runs a standalone dotnet restore' }
            if ($gate -notmatch [regex]::Escape('npm ci')) { throw "a gate is missing 'npm ci'" }
        }
    }

    It 'runs the UI check in both gates' {
        # verify.ps1 starts the check as a background process, so the command arrives as
        # a FilePath plus an argument vector rather than as one literal line. Both
        # spellings name the same check against the same directory; only the surface
        # form differs.
        $windows = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify.ps1'))
        $linux = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify-linux.ps1'))

        if ($windows -notmatch [regex]::Escape("-FilePath 'npm'")) { throw 'verify.ps1 does not start npm for the UI check' }
        if ($windows -notmatch [regex]::Escape("@('run', 'check', '--prefix', 'src/SqlHarness.Dashboard/ui')")) {
            throw 'verify.ps1 does not check the dashboard UI with npm run check'
        }
        if ($linux -notmatch [regex]::Escape('npm run check --prefix src/SqlHarness.Dashboard/ui')) {
            throw 'verify-linux.ps1 is missing the UI check'
        }
    }

    It 'overlaps the UI check with the build in the Windows gate and joins it before the tests' {
        # The UI check needs no .NET artifacts, so it runs beside the build instead of
        # in front of it. The join must precede the test stage: that is the memory-hungry
        # one, and the box has 8 logical cores.
        $windows = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify.ps1'))

        $start = $windows.IndexOf("Start-BackgroundGateStage -Name 'ui-check'")
        $build = $windows.IndexOf("Add-Stage -Name 'build'")
        $join = $windows.IndexOf('Complete-BackgroundGateStage')
        $test = $windows.IndexOf("Add-Stage -Name 'test'")

        foreach ($pair in @(@('start ui-check in the background', $start), @('run the build', $build), @('join ui-check', $join), @('run the tests', $test))) {
            if ($pair[1] -lt 0) { throw "verify.ps1 does not $($pair[0])" }
        }
        if (-not ($start -lt $build -and $build -lt $join -and $join -lt $test)) {
            throw 'verify.ps1 must start ui-check, then build, then join ui-check, then run the tests'
        }
    }

    It 'reports both overlapped stages before either one stops the Windows gate' {
        # A UI failure must never hide behind a build failure: Add-Stage used to exit on
        # the spot, so the overlapped stage has to be joined and printed before the first
        # Stop-Gate runs. The build is stopped first because it is the stage that ran
        # first, so its exit code is the one the gate returns.
        $windows = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify.ps1'))

        $join = $windows.IndexOf('Complete-BackgroundGateStage')
        $stopBuild = $windows.IndexOf("Stop-Gate -Name 'build'")
        $stopUi = $windows.IndexOf("Stop-Gate -Name 'ui-check'")

        foreach ($pair in @(@('join the UI check', $join), @('stop on the build', $stopBuild), @('stop on the UI check', $stopUi))) {
            if ($pair[1] -lt 0) { throw "verify.ps1 does not $($pair[0])" }
        }
        if (-not ($join -lt $stopBuild -and $stopBuild -lt $stopUi)) {
            throw 'verify.ps1 must join ui-check before it stops the gate, and must report the build before the UI check'
        }
    }

    It 'couples the UI check into the build stage of the Linux gate' {
        # Every Linux stage is one wsl.exe run of one bash script, so a background
        # `npm run check &` inside the build script is the only way to overlap them
        # without two WSL launches racing each other over the gate clone. That is why
        # the Linux gate's stage lines stay sequential.
        $linux = [IO.File]::ReadAllText((Join-Path $script:ScriptsDirectory 'verify-linux.ps1'))

        $buildStage = $linux.IndexOf("Invoke-LinuxStage -Name 'build'")
        if ($buildStage -lt 0) { throw 'verify-linux.ps1 does not run a build stage' }
        $buildLines = $linux.Substring($buildStage)
        $nextStage = $buildLines.IndexOf('Invoke-LinuxStage -Name', 1)

        foreach ($fragment in @('npm run check --prefix src/SqlHarness.Dashboard/ui &', 'ui_check_pid=$!', 'wait "$ui_check_pid"')) {
            if ($buildLines.IndexOf($fragment) -lt 0) { throw "the Linux build stage does not contain '$fragment'" }
        }
        if ($nextStage -ge 0 -and $buildLines.IndexOf('wait "$ui_check_pid"') -gt $nextStage) {
            throw 'the Linux UI check is no longer joined inside the build stage'
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