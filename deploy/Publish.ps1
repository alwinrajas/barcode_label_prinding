<#
.SYNOPSIS
    Produces the three deployable artifacts: the API service, the database
    migrator, and the WPF client.

.DESCRIPTION
    Everything is published self-contained win-x64, so neither the server nor
    the 20 workstations need a matching .NET runtime installed and a runtime
    update can never change the behaviour of a released build.

    This script only builds. It installs nothing and touches no live system —
    run Install-Server.ps1 on the server with the output.

.EXAMPLE
    .\Publish.ps1 -Version 1.0.0 -OutputPath D:\BarcodePrinter\stage
#>
[CmdletBinding()]
param(
    [string]$Version = "1.0.0",
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\artifacts"),
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    # Authenticode signing. Without it the client exe is unsigned, and every
    # workstation gets Defender SmartScreen's "Windows protected your PC" the
    # first time it runs — which is not a bug to fix in the code, it is the
    # absence of a signature. Pass the thumbprint of a code-signing certificate
    # in CurrentUser\My or LocalMachine\My.
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipTests,
    [switch]$SkipIntegrationTests
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..")

# WPF compiles its XAML through a generated <Project>_wpftmp.csproj. A persistent
# MSBuild node left behind by an earlier build — an IDE design-time build, or a
# previous run of this script — keeps handles on that project's obj\ files, and
# the next build dies with MSB3061 "being used by another process". It surfaces
# as a FAILING TEST SUITE, which sends you looking for a broken test that does
# not exist. A release build runs once and exits, so it gains nothing from node
# reuse; start from a clean slate instead.
$env:MSBUILDDISABLENODEREUSE = "1"
& dotnet build-server shutdown 2>&1 | Out-Null

if (Test-Path $OutputPath) {
    Remove-Item $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
$OutputPath = (Resolve-Path $OutputPath).Path

function Publish-Component {
    param([string]$Project, [string]$Name, [switch]$SelfContained)

    Write-Host "Publishing $Name..." -ForegroundColor Cyan
    $target = Join-Path $OutputPath $Name

    $arguments = @(
        "publish", (Join-Path $repo $Project),
        "-c", $Configuration,
        "-r", "win-x64",
        "-o", $target,
        "/p:Version=$Version",
        "/p:SelfContained=$(if ($SelfContained) { 'true' } else { 'false' })"
    )
    # Out-Host, not the pipeline: a PowerShell function returns EVERYTHING it
    # emits, so leaving dotnet's console output uncaptured would prepend it to
    # the path this function returns.
    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed to publish (exit $LASTEXITCODE)."
    }
    return $target
}

# The test suite is the release gate. A build that cannot prove itself is not
# a release candidate, so this runs before anything is published.
#
# Each test project runs as its OWN dotnet test invocation, in sequence:
#
#  * a whole-solution run reports one exit code for everything, so a failure
#    tells you only that "something" broke — and its output scrolls past the
#    console without being kept, which is worthless once the build is running
#    unattended. Per-project runs give a per-suite verdict and a log per suite.
#  * sequential invocation is also what keeps the integration suite honest: it
#    owns a MySQL Testcontainer, and assemblies running concurrently put enough
#    load on the machine to time its connections out mid-suite.
#
# Nothing is skipped and no suite is excluded. A project that contains no tests
# is REPORTED rather than silently counted as a pass — `dotnet test` exits 0 for
# an empty assembly, so without this an empty suite looks identical to a green
# one.
if ($SkipTests) {
    Write-Host "Skipping test suite (-SkipTests specified)..." -ForegroundColor Yellow
} else {
    Write-Host "Running the test suite..." -ForegroundColor Cyan

    # Inside the output folder: the evidence for a release travels with it.
    $testLogDir = Join-Path $OutputPath "test-logs"
    New-Item -ItemType Directory -Path $testLogDir -Force | Out-Null
    $runSettings = Join-Path $repo "tests\test.runsettings"

    $testProjects = Get-ChildItem (Join-Path $repo "tests") -Filter "*.csproj" -Recurse |
        Sort-Object Name
    if (-not $testProjects) {
        throw "No test projects found under tests\. Refusing to publish an unverified build."
    }

    $dockerAvailable = try { (& docker info 2>&1) -match "Server Version" } catch { $false }

    $suiteResults = [System.Collections.Generic.List[object]]::new()

    foreach ($project in $testProjects) {
        $suite = $project.BaseName

        if ($suite -match 'Integration' -and ($SkipIntegrationTests -or -not $dockerAvailable)) {
            Write-Host "  $suite : SKIPPED (Docker daemon not running)" -ForegroundColor Yellow
            $suiteResults.Add([pscustomobject]@{
                Suite = $suite; Status = "SKIPPED"; Passed = 0; Failed = 0
            })
            continue
        }

        Write-Host "  $suite ..." -ForegroundColor DarkGray -NoNewline

        $output = & dotnet test $project.FullName -c $Configuration --settings $runSettings --nologo 2>&1
        $exitCode = $LASTEXITCODE
        $text = $output | Out-String
        $text | Set-Content (Join-Path $testLogDir "$suite.log") -Encoding UTF8

    # "no tests" is not a failure of this run, but it is not a pass either.
    $isEmpty = $text -match 'No test is available'
    $counts = [regex]::Match($text, 'Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+)')

    $status = if ($exitCode -ne 0) { "FAILED" } elseif ($isEmpty) { "EMPTY" } else { "PASSED" }
    $passed = if ($counts.Success) { [int]$counts.Groups[2].Value } else { 0 }
    $failed = if ($counts.Success) { [int]$counts.Groups[1].Value } else { 0 }

    $suiteResults.Add([pscustomobject]@{
        Suite = $suite; Status = $status; Passed = $passed; Failed = $failed
    })

    switch ($status) {
        "PASSED" { Write-Host "`r  $suite : $passed passed" -ForegroundColor Green }
        "EMPTY"  { Write-Host "`r  $suite : contains no tests" -ForegroundColor Yellow }
        "FAILED" {
            Write-Host "`r  $suite : FAILED" -ForegroundColor Red
            # Show the failures now. A gate that hides why it closed wastes the
            # next hour of whoever is trying to release.
            $text -split "`n" | Where-Object { $_ -match 'error|Failed|Assert' } |
                Select-Object -First 40 | ForEach-Object { Write-Host "      $_" -ForegroundColor Red }
        }
    }
}

Write-Host ""
$suiteResults | Format-Table -AutoSize | Out-Host

$brokenSuites = @($suiteResults | Where-Object Status -eq "FAILED")
if ($brokenSuites) {
    throw ("Tests failed — nothing published. Failing suites: " +
           ($brokenSuites.Suite -join ', ') + ". Full output in $testLogDir")
}

$emptySuites = @($suiteResults | Where-Object Status -eq "EMPTY")
if ($emptySuites) {
    Write-Warning ("These test projects contain no tests and verified nothing: " +
                   ($emptySuites.Suite -join ', ') + ".")
}

$totalPassed = ($suiteResults | Measure-Object -Property Passed -Sum).Sum
Write-Host "$totalPassed tests passed across $($suiteResults.Count) suites." -ForegroundColor Green
}

# Between the phases, not just at the start. Running the tests BUILDS the WPF
# project, which starts a Roslyn compiler server (VBCSCompiler) that outlives
# the test run and keeps handles on src\client\BarcodePrinter.Wpf\obj. The
# client publish then cannot delete its own generated _wpftmp files and fails
# with MSB3061. MSBUILDDISABLENODEREUSE governs MSBUILD nodes only — it does
# nothing about the compiler server, so it has to be shut down explicitly.
& dotnet build-server shutdown 2>&1 | Out-Null

$api = Publish-Component "src\server\BarcodePrinter.Api\BarcodePrinter.Api.csproj" "api" -SelfContained
$migrator = Publish-Component "src\tools\BarcodePrinter.DbMigrator\BarcodePrinter.DbMigrator.csproj" "migrator" -SelfContained
$client = Publish-Component "src\client\BarcodePrinter.Wpf\BarcodePrinter.Wpf.csproj" "client" -SelfContained

# appsettings.Development.json carries a dev connection string and a dev signing
# key. Shipping it would put both on the server, where a stray ASPNETCORE_ENVIRONMENT
# could load them.
Get-ChildItem $api -Filter "appsettings.Development.json" | Remove-Item -Force

# ---- Signing -----------------------------------------------------------------------

if ($CertThumbprint) {
    $certificate = @(
        Get-Item "Cert:\CurrentUser\My\$CertThumbprint"  -ErrorAction SilentlyContinue
        Get-Item "Cert:\LocalMachine\My\$CertThumbprint" -ErrorAction SilentlyContinue
    ) | Select-Object -First 1
    if (-not $certificate) {
        throw "Code-signing certificate $CertThumbprint was not found in CurrentUser\My or LocalMachine\My."
    }

    # Only OUR binaries. Signing the whole output would re-sign several hundred
    # Microsoft runtime files that already carry valid signatures.
    $toSign = Get-ChildItem $OutputPath -Recurse -Include "BarcodePrinter.*.exe", "BarcodePrinter.*.dll"
    Write-Host "Signing $($toSign.Count) files with $($certificate.Subject)..." -ForegroundColor Cyan

    # A timestamp is what keeps these binaries valid after the certificate
    # expires; without one every installed client starts failing signature
    # checks on the day the certificate lapses.
    $signed = Set-AuthenticodeSignature -FilePath $toSign.FullName -Certificate $certificate `
        -TimestampServer $TimestampUrl -HashAlgorithm SHA256
    $badSignatures = @($signed | Where-Object Status -ne "Valid")
    if ($badSignatures) {
        throw ("Signing failed for: " + ($badSignatures.Path -join ', '))
    }
    Write-Host "All binaries signed and timestamped." -ForegroundColor Green
} else {
    Write-Warning ("Not signed (-CertThumbprint was not supplied). Workstations will see " +
                   "Defender SmartScreen's 'Windows protected your PC' the first time they run the client.")
}

# Every deployment script, by pattern rather than by name. Listing them
# individually meant a new script silently failed to reach the package — which
# is how the installer came to invoke a file that was never shipped.
# Publish.ps1 itself is a build tool and has no business on a target machine.
Get-ChildItem $PSScriptRoot -Filter "*.ps1" |
    Where-Object { $_.Name -ne "Publish.ps1" -and $_.Name -ne "Build-Installer.ps1" } |
    Copy-Item -Destination $OutputPath

Copy-Item (Join-Path $PSScriptRoot "RUNBOOK.md") $OutputPath
Copy-Item (Join-Path $PSScriptRoot "mysql") $OutputPath -Recurse

@{
    version   = $Version
    builtAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    builtBy   = "$env:USERDOMAIN\$env:USERNAME"
    builtOn   = $env:COMPUTERNAME
    # Optional: not every deployment is built from a git checkout, and git's
    # exit code must not become the script's.
    commit    = $(try { (& git -C $repo rev-parse --short HEAD 2>$null) } catch { $null })
} | ConvertTo-Json | Set-Content (Join-Path $OutputPath "build-info.json") -Encoding UTF8

$global:LASTEXITCODE = 0

Write-Host ""
Write-Host "Published $Version to $OutputPath" -ForegroundColor Green
Write-Host "  api\       -> copy to the server, then run Install-Server.ps1"
Write-Host "  migrator\  -> run by Install-Server.ps1 as an explicit deployment step"
Write-Host "  client\    -> run Install-Client.ps1 on each workstation"
Write-Host ""
Write-Host "Install-Server.ps1 now REQUIRES a certificate: -CertThumbprint for a CA-issued"
Write-Host "one, or -GenerateSelfSignedCert for a LAN pilot. Kestrel reads it from"
Write-Host "appsettings, so 'netsh http add sslcert' has no effect on this service."
