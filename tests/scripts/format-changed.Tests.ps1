# Pester 3.4-compatible unit tests for format-changed.ps1
# Uses a temporary git repository and a fake dotnet on PATH; never invokes the
# real dotnet, never touches the working repository.

$ErrorActionPreference = 'Stop'

$script:RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$script:FormatScript = Join-Path $script:RepoRoot 'scripts\format-changed.ps1'

function New-TempDirectory {
    param([string]$Prefix)

    $root = Join-Path ([System.IO.Path]::GetTempPath()) ($Prefix + [Guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return $root
}

function Invoke-Git {
    param(
        [string]$Directory,
        [string[]]$Arguments
    )

    $output = & git -C $Directory @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed in $Directory : $output"
    }
    return @($output)
}

function New-FixtureFile {
    param(
        [string]$Directory,
        [string]$RelativePath,
        [string]$Content = "// fixture`n"
    )

    $path = Join-Path $Directory $RelativePath
    $parent = Split-Path -Parent $path
    if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Set-Content -LiteralPath $path -Value $Content -NoNewline
}

function New-FixtureRepository {
    $root = New-TempDirectory -Prefix 'sqlharness-format-repo-'

    Invoke-Git -Directory $root -Arguments @('init', '-b', 'main') | Out-Null
    Invoke-Git -Directory $root -Arguments @('config', 'user.email', 'fixture@example.invalid') | Out-Null
    Invoke-Git -Directory $root -Arguments @('config', 'user.name', 'Fixture') | Out-Null
    Invoke-Git -Directory $root -Arguments @('config', 'commit.gpgsign', 'false') | Out-Null

    New-FixtureFile -Directory $root -RelativePath 'src/Alpha.cs'
    New-FixtureFile -Directory $root -RelativePath 'src/Beta.cs'
    New-FixtureFile -Directory $root -RelativePath 'docs/readme.md' -Content "# fixture`n"
    Invoke-Git -Directory $root -Arguments @('add', '-A') | Out-Null
    Invoke-Git -Directory $root -Arguments @('commit', '-m', 'fixture') | Out-Null

    return $root
}

function New-FakeDotnetRoot {
    param([int]$ExitCode = 0)

    $root = New-TempDirectory -Prefix 'sqlharness-fake-dotnet-'
    $argsFile = Join-Path $root 'args.txt'
    $body = @"
@echo off
>"$argsFile" echo %*
exit /b $ExitCode
"@
    Set-Content -LiteralPath (Join-Path $root 'dotnet.cmd') -Value $body -NoNewline
    return [pscustomobject]@{ Root = $root; ArgsFile = $argsFile }
}

function Get-FakeDotnetArgs {
    param([string]$FakeRoot)

    $argsFile = Join-Path $FakeRoot 'args.txt'
    if (-not (Test-Path $argsFile)) { return $null }
    return (Get-Content -LiteralPath $argsFile -Raw).Trim()
}

function Invoke-FormatChanged {
    param(
        [string]$Repository,
        [string]$FakeRoot,
        [string[]]$Arguments = @()
    )

    $previousPath = $env:PATH
    try {
        $env:PATH = $FakeRoot + [IO.Path]::PathSeparator + $previousPath
        $output = & pwsh -NoProfile -File $script:FormatScript -RepoRoot $Repository @Arguments 2>&1 | Out-String
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output   = $output
        }
    }
    finally {
        $env:PATH = $previousPath
    }
}

Describe 'format-changed.ps1' {
    $repo = $null
    $fake = $null

    BeforeEach {
        $repo = New-FixtureRepository
        $fake = New-FakeDotnetRoot
    }

    AfterEach {
        foreach ($path in @($repo, $fake.Root)) {
            if ($path -and (Test-Path $path)) {
                Remove-Item -Recurse -Force $path -ErrorAction SilentlyContinue
            }
        }
    }

    It 'skips dotnet entirely when no C# file changed' {
        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $result.ExitCode | Should Be 0
        $result.Output | Should Match 'skipped'
        Get-FakeDotnetArgs -FakeRoot $fake.Root | Should BeNullOrEmpty
    }

    It 'checks only the C# files that changed in the working tree' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline
        New-FixtureFile -Directory $repo -RelativePath 'src/Added.cs'
        Set-Content -LiteralPath (Join-Path $repo 'docs/readme.md') -Value "# changed`n" -NoNewline

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $result.ExitCode | Should Be 0
        $recorded = Get-FakeDotnetArgs -FakeRoot $fake.Root
        $recorded | Should Match 'format'
        $recorded | Should Match 'SqlHarness.sln'
        $recorded | Should Match '--no-restore'
        $recorded | Should Match '--include'
        $recorded | Should Match 'src[\\/]Alpha\.cs'
        $recorded | Should Match 'src[\\/]Added\.cs'
        $recorded | Should Not Match 'readme\.md'
        $recorded | Should Match '--verify-no-changes'
    }

    It 'reports the changed C# file count so the agent can see the scope' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $result.Output | Should Match '1 changed C# file'
    }

    It 'ignores staged, untracked and generated directories correctly' {
        New-FixtureFile -Directory $repo -RelativePath 'src/obj/Generated.cs'
        New-FixtureFile -Directory $repo -RelativePath 'src/bin/Release/Built.cs'

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $result.ExitCode | Should Be 0
        Get-FakeDotnetArgs -FakeRoot $fake.Root | Should BeNullOrEmpty
    }

    It 'includes a staged C# file that is not modified in the working tree' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// staged`n" -NoNewline
        Invoke-Git -Directory $repo -Arguments @('add', 'src/Alpha.cs') | Out-Null
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// staged then edited`n" -NoNewline

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $recorded = Get-FakeDotnetArgs -FakeRoot $fake.Root
        $recorded | Should Match 'src[\\/]Alpha\.cs'
    }

    It 'rewrites instead of checking when -Fix is given' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root -Arguments @('-Fix')

        $result.ExitCode | Should Be 0
        $recorded = Get-FakeDotnetArgs -FakeRoot $fake.Root
        $recorded | Should Not Match '--verify-no-changes'
        $recorded | Should Match '--include'
    }

    It 'propagates the dotnet format exit code' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline
        Remove-Item -LiteralPath (Join-Path $fake.Root 'dotnet.cmd') -Force
        $failing = New-FakeDotnetRoot -ExitCode 2

        try {
            $result = Invoke-FormatChanged -Repository $repo -FakeRoot $failing.Root

            $result.ExitCode | Should Be 2
        }
        finally {
            Remove-Item -Recurse -Force $failing.Root -ErrorAction SilentlyContinue
        }
    }

    It 'checks the files a branch changed against the default branch, not the working tree' {
        $origin = New-TempDirectory -Prefix 'sqlharness-format-origin-'
        try {
            Invoke-Git -Directory $origin -Arguments @('init', '-b', 'main') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('config', 'user.email', 'fixture@example.invalid') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('config', 'user.name', 'Fixture') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('config', 'commit.gpgsign', 'false') | Out-Null
            New-FixtureFile -Directory $origin -RelativePath 'main-only.cs'
            Invoke-Git -Directory $origin -Arguments @('add', '-A') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('commit', '-m', 'main only') | Out-Null

            $clone = New-TempDirectory -Prefix 'sqlharness-format-clone-'
            try {
                Invoke-Git -Directory $clone -Arguments @('clone', $origin, $clone) | Out-Null
                Invoke-Git -Directory $clone -Arguments @('config', 'user.email', 'fixture@example.invalid') | Out-Null
                Invoke-Git -Directory $clone -Arguments @('config', 'user.name', 'Fixture') | Out-Null
                Invoke-Git -Directory $clone -Arguments @('checkout', '-b', 'feature') | Out-Null
                New-FixtureFile -Directory $clone -RelativePath 'feature-only.cs'
                Invoke-Git -Directory $clone -Arguments @('add', '-A') | Out-Null
                Invoke-Git -Directory $clone -Arguments @('commit', '-m', 'feature') | Out-Null
                # An uncommitted local edit must not widen the branch scope.
                New-FixtureFile -Directory $clone -RelativePath 'uncommitted.cs'

                $result = Invoke-FormatChanged -Repository $clone -FakeRoot $fake.Root -Arguments @('-Mode', 'branch')

                $result.ExitCode | Should Be 0
                $recorded = Get-FakeDotnetArgs -FakeRoot $fake.Root
                $recorded | Should Match 'feature-only\.cs'
                $recorded | Should Not Match 'main-only\.cs'
                $recorded | Should Not Match 'uncommitted\.cs'
            }
            finally {
                Remove-Item -Recurse -Force $clone -ErrorAction SilentlyContinue
            }
        }
        finally {
            Remove-Item -Recurse -Force $origin -ErrorAction SilentlyContinue
        }
    }

    It 'skips the branch check when the branch equals the default branch' {
        $origin = New-TempDirectory -Prefix 'sqlharness-format-origin-main-'
        try {
            Invoke-Git -Directory $origin -Arguments @('init', '-b', 'main') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('config', 'user.email', 'fixture@example.invalid') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('config', 'user.name', 'Fixture') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('config', 'commit.gpgsign', 'false') | Out-Null
            New-FixtureFile -Directory $origin -RelativePath 'on-main.cs'
            Invoke-Git -Directory $origin -Arguments @('add', '-A') | Out-Null
            Invoke-Git -Directory $origin -Arguments @('commit', '-m', 'on main') | Out-Null

            $clone = New-TempDirectory -Prefix 'sqlharness-format-clone-main-'
            try {
                Invoke-Git -Directory $clone -Arguments @('clone', $origin, $clone) | Out-Null

                $result = Invoke-FormatChanged -Repository $clone -FakeRoot $fake.Root -Arguments @('-Mode', 'branch')

                $result.ExitCode | Should Be 0
                Get-FakeDotnetArgs -FakeRoot $fake.Root | Should BeNullOrEmpty
            }
            finally {
                Remove-Item -Recurse -Force $clone -ErrorAction SilentlyContinue
            }
        }
        finally {
            Remove-Item -Recurse -Force $origin -ErrorAction SilentlyContinue
        }
    }

    It 'prints the file list without running dotnet in ListOnly mode' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline
        New-FixtureFile -Directory $repo -RelativePath 'src/Added.cs'

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root -Arguments @('-ListOnly')

        $result.ExitCode | Should Be 0
        Get-FakeDotnetArgs -FakeRoot $fake.Root | Should BeNullOrEmpty
        $result.Output | Should Match 'src[\\/]Alpha\.cs'
        $result.Output | Should Match 'src[\\/]Added\.cs'
    }

    It 'never passes a deleted C# file to dotnet format' {
        Remove-Item -LiteralPath (Join-Path $repo 'src/Beta.cs') -Force
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $result.ExitCode | Should Be 0
        $recorded = Get-FakeDotnetArgs -FakeRoot $fake.Root
        $recorded | Should Match 'src[\\/]Alpha\.cs'
        $recorded | Should Not Match 'Beta\.cs'
    }

    It 'returns the file list on the pipeline so a gate can hand it to dotnet format' {
        # A gate calls the script in-process and pipes the result. Write-Host does
        # not reach the pipeline, so the list has to be output, not printed.
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline

        $files = @(& $script:FormatScript -RepoRoot $repo -ListOnly)

        $files.Count | Should Be 1
        $files[0] | Should Match 'src[\\/]Alpha\.cs'
    }

    It 'never runs a writing dotnet command in check mode' {
        Set-Content -LiteralPath (Join-Path $repo 'src/Alpha.cs') -Value "// changed`n" -NoNewline

        $result = Invoke-FormatChanged -Repository $repo -FakeRoot $fake.Root

        $recorded = Get-FakeDotnetArgs -FakeRoot $fake.Root
        if ($recorded -match '(?i)(whitespace|style|analyzers)\s') {
            throw "check mode must not name a dotnet format subcommand: $recorded"
        }
        $result.ExitCode | Should Be 0
    }
}