<#
.SYNOPSIS
    Produces artifacts\installer\BarcodePrinterSetup.exe — the single file the
    customer receives.

.DESCRIPTION
    The full pipeline, in order:

        test + publish  (deploy\Publish.ps1 — the release gate)
        stage           (payload laid out the way the MSI expects)
        BA              (the custom bootstrapper application's UI assembly)
        MSI             (application package)
        bundle          (bootstrapper: .NET runtime, VC++ runtime, MySQL, MSI)
        sign            (optional, Authenticode with timestamping)

    Publishing is delegated to Publish.ps1 rather than repeated here, so the
    test gate cannot be bypassed by building the installer instead.

    WHOLE PIPELINE ON WiX 4. The MSI was migrated first and the bundle followed;
    both are now compiled by the local WiX 4.0.6 tool from dotnet-tools.json.
    The globally installed WiX 5 is not used by this script at all and is left
    in place as the rollback path.

.EXAMPLE
    .\Build-Installer.ps1 -Version 1.3.0

.EXAMPLE
    .\Build-Installer.ps1 -Version 1.3.0 -CertThumbprint A1B2C3...

.EXAMPLE
    # Reuse a payload staged elsewhere, without touching artifacts\stage:
    .\Build-Installer.ps1 -Version 1.1.0 -SkipPublish -StagePath D:\snapshot
#>
[CmdletBinding()]
param(
    [string]$Version = "1.3.0",
    [string]$Configuration = "Release",

    # Skip the publish step and reuse what is already staged. For iterating on
    # the installer itself; a release build should never use this.
    [switch]$SkipPublish,

    # Where the published payload lives. Defaults to artifacts\stage, which is
    # what Publish.ps1 writes. Point it somewhere else to build against a frozen
    # snapshot — useful when another build is running from a second terminal and
    # would otherwise delete artifacts\stage out from under this one.
    [string]$StagePath,

    # Where the finished BarcodePrinterSetup.exe and the intermediate files go.
    # Defaults to artifacts\installer and artifacts\installer-obj. Overridable
    # for the same reason as -StagePath: two builds writing the same 400 MB
    # output path produce one corrupt file and two confused people.
    [string]$OutputPath,
    [string]$IntermediatePath,

    # Reuse the MSI already sitting in the intermediate folder and rebuild only
    # the bootstrapper application and the bundle. Compressing a 400 MB payload
    # takes minutes and produces a byte-identical cabinet when nothing in the
    # payload changed; this is for iterating on the installer's own UI. A
    # release build must never use it — the MSI would not match the sources.
    [switch]$SkipMsi,

    # Authenticode. Without it the customer meets SmartScreen's "Windows
    # protected your PC" — see the note printed at the end.
    [string]$CertThumbprint,
    [string]$TimestampUrl = "http://timestamp.digicert.com",
    [switch]$SkipTests,
    [switch]$SkipIntegrationTests
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

$repo       = Resolve-Path (Join-Path $PSScriptRoot "..")
$installer  = $PSScriptRoot
$staging    = if ($StagePath) { $StagePath } else { Join-Path $repo "artifacts\stage" }
$outputDir  = if ($OutputPath)       { $OutputPath }       else { Join-Path $repo "artifacts\installer" }
$obj        = if ($IntermediatePath) { $IntermediatePath } else { Join-Path $repo "artifacts\installer-obj" }

$appIcon = Join-Path $repo "src\client\BarcodePrinter.Wpf\Assets\app.ico"

# The custom bootstrapper application. Its build output is what Bundle.wxs
# carries as BA payloads; see the BA section below.
$baProject = Join-Path $repo "src\installer\BarcodePrinter.BootstrapperApp\BarcodePrinter.BootstrapperApp.csproj"
$baDir     = Join-Path $repo "src\installer\BarcodePrinter.BootstrapperApp\bin\$Configuration\net10.0-windows\win-x64"

# The .NET Windows Desktop runtime the BA needs, carried as a prerequisite
# package. Pinned rather than floating: the version here has to match the
# DetectCondition path in Bundle.wxs, and "whatever aka.ms redirects to today"
# would silently stop matching it.
$NetRuntimeVersion = "10.0.11"
$NetRuntimeUrl     = "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$NetRuntimeVersion/windowsdesktop-runtime-$NetRuntimeVersion-win-x64.exe"

# ---- Toolset ---------------------------------------------------------------
#
# ONE WiX version: 4.0.6, from the local tool manifest (dotnet-tools.json at the
# repository root). Both the MSI and the bundle are built with it.
#
#   MSI     WiX 4 has no <Files> element, so the payload is harvested with heat
#           instead — see "Harvest" below.
#
#   Bundle  Bundle.wxs uses WixToolset.Bal.wixext (WiX 5 renamed the same
#           extension to WixToolset.BootstrapperApplications.wixext) and its
#           bal:WixDotNetCoreBootstrapperApplicationHost, which hosts the custom
#           managed bootstrapper application built below.
#
# The globally installed WiX 5 is deliberately untouched: it is the rollback
# path if any of this has to be reverted in a hurry.

# WiX 4, via the local tool manifest. `dotnet tool run` resolves the manifest by
# walking up from the CURRENT directory, so the repository root is pushed for
# the duration of the call — otherwise building from anywhere else silently
# falls back to the global WiX 5 and the whole point of this is lost.
$WixVersion4 = "4.0.6"
# Always called with an explicit array: PowerShell would try to bind a bare
# -something / --something as a parameter of this function otherwise.
function Invoke-Wix4 {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Push-Location $repo
    try { & dotnet tool run wix -- @Arguments }
    finally { Pop-Location }
}

$wix4Reported = (Invoke-Wix4 @("--version")) -join ""
if ($wix4Reported -notmatch "^$([regex]::Escape($WixVersion4))") {
    throw @"
The local WiX tool reports '$wix4Reported', not $WixVersion4.
Restore it with:

    dotnet tool restore

from $repo (dotnet-tools.json pins wix $WixVersion4).
"@
}
Write-Host "WiX: $wix4Reported (local tool)" -ForegroundColor DarkGray

# %USERPROFILE%\.wix\extensions is shared across WiX versions, and WiX 4
# mis-resolves an unversioned -ext into the 5.0.2 folder sitting beside it —
# which fails as "damaged" rather than as a version mismatch. Every WiX 4
# extension reference in this script is therefore version-pinned.
#
# The v5 entries in that cache report as "damaged" to `wix 4 extension list`.
# That is expected and harmless: v4 cannot read a v5 extension's manifest. It
# also means the -notmatch test below can never be satisfied by a v5 entry,
# which is exactly what we want.
$Wix4Util = "WixToolset.Util.wixext/$WixVersion4"
$Wix4Bal  = "WixToolset.Bal.wixext/$WixVersion4"

$installedExtensions = (Invoke-Wix4 @("extension", "list", "--global") 2>&1) -join "`n"
foreach ($pair in @(@{ Id = "WixToolset.Util.wixext"; Ref = $Wix4Util },
                    @{ Id = "WixToolset.Bal.wixext";  Ref = $Wix4Bal })) {
    if ($installedExtensions -notmatch "$([regex]::Escape($pair.Id)) $([regex]::Escape($WixVersion4))") {
        Write-Host "Adding WiX $WixVersion4 extension $($pair.Id)..." -ForegroundColor DarkGray
        Invoke-Wix4 @("extension", "add", "--global", $pair.Ref) | Out-Null
    }
}

# heat, WiX 4's harvester. It ships only as a NuGet package (there is no
# `dotnet tool` for it in the 4.x line), so it is restored as a binary into
# artifacts\tools rather than installed machine-wide. Version-locked to the
# toolset: a heat from a different major writes authoring the compiler rejects.
$HeatVersion = "4.0.6"
$toolsDir = Join-Path $repo "artifacts\tools"
$heatRoot = Join-Path $toolsDir "wixtoolset.heat.$HeatVersion"
$heat     = Join-Path $heatRoot "tools\net472\x64\heat.exe"
if (-not (Test-Path $heat)) {
    New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null
    $nupkg = Join-Path $toolsDir "wixtoolset.heat.$HeatVersion.nupkg"

    # Prefer a package already in the NuGet cache; only reach for the network if
    # it is not there, so an offline build agent that has restored once still
    # builds.
    $nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE ".nuget\packages" }
    $cached = Join-Path $nugetCache "wixtoolset.heat\$HeatVersion\wixtoolset.heat.$HeatVersion.nupkg"
    if (Test-Path $cached) {
        Copy-Item $cached $nupkg -Force
    } else {
        Write-Host "Downloading WixToolset.Heat $HeatVersion..." -ForegroundColor DarkGray
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/wixtoolset.heat/$HeatVersion/wixtoolset.heat.$HeatVersion.nupkg" `
            -OutFile $nupkg -UseBasicParsing
    }
    Expand-Archive $nupkg -DestinationPath $heatRoot -Force
}
if (-not (Test-Path $heat)) { throw "heat $HeatVersion could not be restored to $heatRoot." }

# MSI ProductVersion only compares the first three fields, and only 0-255 /
# 0-255 / 0-65535 are meaningful. A four-part version here silently makes
# upgrades stop working, which is the sort of bug that surfaces a year later.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "-Version must be three parts (e.g. 1.3.0); MSI ignores anything beyond the third."
}

foreach ($dir in @($outputDir, $obj)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

# ---- 1. Test and publish ---------------------------------------------------

# ---- 0. The shipped scripts must parse on the shell that will run them ------
#
# The MSI invokes them with Windows PowerShell 5.1, because that is the only
# shell guaranteed to exist on a customer machine. 5.1 reads a file with no
# byte-order mark as ANSI, not UTF-8 — so a single em dash in a comment becomes
# mojibake containing a quote character, strings stop terminating, and the whole
# script fails to parse. It fails at install time, on the customer's machine,
# with a parser error about a brace hundreds of lines away from the real cause.
#
# Checking with $PSVersionTable's parser proves nothing here: PowerShell 7 reads
# BOM-less files as UTF-8 and sees no problem at all.

Write-Host "== Checking scripts against Windows PowerShell 5.1 ==" -ForegroundColor Cyan
$deployDir = Join-Path $repo "deploy"

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
foreach ($file in Get-ChildItem $deployDir -Filter "*.ps1") {
    $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if (-not $hasBom) {
        [System.IO.File]::WriteAllText($file.FullName, [System.Text.Encoding]::UTF8.GetString($bytes), $utf8Bom)
        Write-Host "  added a BOM to $($file.Name)" -ForegroundColor Yellow
    }
}

$parseScript = Join-Path $env:TEMP "bp-ps51-parse.ps1"
@'
$failed = @()
Get-ChildItem $args[0] -Filter *.ps1 | ForEach-Object {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errors)
    if ($errors -and $errors.Count) {
        $failed += $_.Name
        Write-Output ("FAIL {0}: line {1}: {2}" -f $_.Name, $errors[0].Extent.StartLineNumber, $errors[0].Message)
    }
}
if ($failed.Count) { exit 1 }
'@ | Set-Content $parseScript -Encoding UTF8

& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -NoProfile -ExecutionPolicy Bypass -File $parseScript $deployDir
if ($LASTEXITCODE -ne 0) {
    throw "One or more deployment scripts do not parse under Windows PowerShell 5.1 (see above). They would fail on the customer's machine."
}
Write-Host "  all deployment scripts parse under 5.1." -ForegroundColor Green

# Parsing is necessary but not sufficient: a parameter that only exists in
# PowerShell 6+ parses fine everywhere and then fails to BIND at runtime, on
# the customer's machine, typically on a code path fresh-install testing never
# reaches (-AsHashtable burned an upgrade path exactly this way). Scan for the
# ones that have actually bitten or are likely to.
$sixPlusOnly = @('-AsHashtable', '\?\?=?', '&&', '\|\|')
$flagged = foreach ($file in Get-ChildItem $deployDir -Filter "*.ps1") {
    foreach ($construct in $sixPlusOnly) {
        Select-String -Path $file.FullName -Pattern $construct -AllMatches |
            # Comments are allowed to mention anything.
            Where-Object { ($_.Line -replace '#.*$', '') -match $construct } |
            ForEach-Object { "$($file.Name):$($_.LineNumber): $($_.Line.Trim())" }
    }
}
if ($flagged) {
    $flagged | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    throw "Deployment scripts use PowerShell 6+ constructs; they run under Windows PowerShell 5.1 on the target machine."
}

# The icon is generated from logo.png rather than committed, so it can never
# drift from the logo.
#
# Regenerated only when it is absent. It used to be rewritten on every build,
# which is correct for a release machine and wrong on a developer's: the icon
# lives under src\client\, and rewriting a tracked file in a tree somebody else
# is editing turns "I built the installer" into a spurious diff in their working
# copy. Delete the .ico to force a regeneration.
if (-not (Test-Path $appIcon)) {
    Write-Host "== Generating the application icon ==" -ForegroundColor Cyan
    & (Join-Path $installer "New-AppIcon.ps1")
    if (-not (Test-Path $appIcon)) { throw "The application icon was not generated." }
} else {
    Write-Host "== Application icon already present ==" -ForegroundColor DarkGray
}

if (-not $SkipPublish) {
    Write-Host "== Publishing (this runs the test gate) ==" -ForegroundColor Cyan
    $pubArgs = @{
        Version = $Version
        Configuration = $Configuration
        OutputPath = $staging
    }
    if ($SkipTests) { $pubArgs.SkipTests = $true }
    if ($SkipIntegrationTests) { $pubArgs.SkipIntegrationTests = $true }
    & (Join-Path $repo "deploy\Publish.ps1") @pubArgs
    if ($LASTEXITCODE -ne 0) { throw "Publish failed; the installer was not built." }
} else {
    Write-Host "== Reusing the existing staged payload (-SkipPublish) ==" -ForegroundColor Yellow
    if (-not (Test-Path (Join-Path $staging "api\BarcodePrinter.Api.exe"))) {
        throw "Nothing staged at $staging. Run without -SkipPublish."
    }
    # The scripts are the part of the payload that changes most while iterating
    # on the installer — and the part -SkipPublish would silently serve stale.
    # An installer rebuilt "with the fix" but carrying last hour's scripts cost
    # a full install/test cycle to notice; refresh them unconditionally.
    Get-ChildItem (Join-Path $repo "deploy") -Filter "*.ps1" |
        Where-Object { $_.Name -notin @("Publish.ps1", "Build-Installer.ps1") } |
        Copy-Item -Destination $staging -Force
    Write-Host "  refreshed the deployment scripts in the stage." -ForegroundColor DarkGray
}

# ---- 2. Stage the pieces the MSI needs -------------------------------------

Write-Host "== Staging installer payload ==" -ForegroundColor Cyan

# The MySQL archive. Not committed to the repository and not downloaded during
# the customer's installation — it is part of the build, so an offline machine
# installs exactly as well as a connected one.
$mysqlStage = Join-Path $staging "mysql"
New-Item -ItemType Directory -Path $mysqlStage -Force | Out-Null
# Large third-party binaries live apart from deploy\mysql\ (which holds the
# my.ini fragment): Publish.ps1 copies that folder into every package, and a
# 247 MB archive has no business travelling with a LAN server deployment.
$mysqlSource = Join-Path $repo "deploy\payload"
New-Item -ItemType Directory -Path $mysqlSource -Force | Out-Null
$mysqlZip = Get-ChildItem $mysqlSource -Filter "mysql-*-winx64.zip" -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $mysqlZip) {
    throw @"
No MySQL archive found in $mysqlSource.
Download the MySQL 8.4 'Windows (x86, 64-bit), ZIP Archive' from
https://dev.mysql.com/downloads/mysql/ and place it there. It is deliberately
not committed to this repository: it is ~247 MB and is redistributed under
MySQL's own licence, which is a decision for whoever ships this product.
"@
}
Copy-Item $mysqlZip.FullName $mysqlStage -Force
Write-Host "  MySQL: $($mysqlZip.Name) ($([math]::Round($mysqlZip.Length/1MB)) MB)"

# Visual C++ runtime, for MySQL's binaries.
$vcRedist = Join-Path $mysqlSource "vc_redist.x64.exe"
if (-not (Test-Path $vcRedist)) {
    Write-Host "  Downloading the Visual C++ x64 runtime..."
    Invoke-WebRequest "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile $vcRedist -UseBasicParsing
}
Write-Host "  VC++ runtime: $([math]::Round((Get-Item $vcRedist).Length/1MB)) MB"

# .NET Windows Desktop runtime, for the bundle's own user interface. Carried in
# the bundle rather than downloaded on the customer's machine, for the same
# reason as everything else here: a restricted network must install as well as
# an open one.
#
# The URL is versioned rather than the aka.ms alias. The alias follows the
# latest patch, and this file's version has to match the DetectCondition in
# Bundle.wxs — a floating URL would quietly break detection on the next patch
# release, and the symptom would be a 60 MB reinstall on every run.
$netRuntimeExe = Join-Path $mysqlSource "windowsdesktop-runtime-$NetRuntimeVersion-win-x64.exe"
if (-not (Test-Path $netRuntimeExe)) {
    Write-Host "  Downloading the .NET Windows Desktop runtime $NetRuntimeVersion..."
    Invoke-WebRequest $NetRuntimeUrl -OutFile $netRuntimeExe -UseBasicParsing
}
Write-Host "  .NET Desktop runtime: $([math]::Round((Get-Item $netRuntimeExe).Length/1MB)) MB"

# ---- 3. Harvest the payload ------------------------------------------------
#
# WiX 4 has no <Files> element, so the file list is produced here by heat and
# handed to the compiler as generated fragments. This has to run AFTER publish
# (there is nothing to harvest before it) and BEFORE the build.
#
# The fragments are written to the intermediate folder, never into installer\:
# they are build output, they change whenever a dependency does, and a copy of
# them next to the hand-written authoring is a copy someone will eventually edit
# by hand and lose.
#
# The flags, and why each one is there:
#
#   -ag     Auto-generate component GUIDs AT COMPILE TIME — heat writes
#           Guid="*" and the WiX compiler derives the GUID from the component's
#           install path. Stable across rebuilds, and identical to the GUID
#           <Files> produced for the same path, which is what keeps upgrade and
#           repair working against already-installed copies.
#           NOT -gg, which bakes a random GUID in at harvest time and would
#           hand every file a brand-new identity on every single build.
#   -sfrag  One fragment for the whole group, not one per directory.
#   -srd    Do not emit the harvested root as a directory of its own; the files
#           belong directly in the -dr directory, exactly where <Files> put them.
#   -sreg   No registry harvesting.
#   -scom   No COM self-registration harvesting. heat inspects DLLs for COM
#           registration by default and would write Registry rows <Files> never
#           produced — a payload the old MSI did not have and does not know how
#           to remove. Same reasoning for -svb6.
#   -var    Emit File/@Source as $(var.X)\... so the fragment is not pinned to
#           the machine that harvested it; each var is passed with -d below.
$harvest = Join-Path $obj "harvest"
New-Item -ItemType Directory -Path $harvest -Force | Out-Null

# heat harvests a DIRECTORY; it cannot take a file mask. Two of the five groups
# are subsets of a folder — the scripts sit in the root of the stage alongside
# build-info.json and the published trees, and the MySQL folder holds the my.ini
# fragment next to the archive. Both get a filtered mirror to harvest instead,
# which is also what keeps a stray file dropped into the stage out of the MSI.
#
# Mirrored by hard link where the filesystem allows it: the MySQL archive is
# ~247 MB and copying it on every build is 247 MB of pointless I/O.
function New-HarvestMirror {
    param([string]$Name, [string[]]$SourceFiles)
    $dir = Join-Path $harvest $Name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    foreach ($file in $SourceFiles) {
        $target = Join-Path $dir (Split-Path $file -Leaf)
        try { New-Item -ItemType HardLink -Path $target -Value $file -ErrorAction Stop | Out-Null }
        catch { Copy-Item $file $target -Force }
    }
    return $dir
}

Write-Host "== Harvesting the payload (heat $HeatVersion) ==" -ForegroundColor Cyan

# ScriptPayload: the deployment scripts and the runbook, at the root of the
# install folder. Root only — no subdirectories, and nothing else in the stage.
$scriptFiles = @(
    Get-ChildItem $staging -File -Filter "*.ps1"
    Get-ChildItem $staging -File -Filter "RUNBOOK.md"
) | Select-Object -ExpandProperty FullName
if (-not $scriptFiles) { throw "No deployment scripts found in $staging to harvest." }
$scriptsHarvestDir = New-HarvestMirror -Name "scripts" -SourceFiles $scriptFiles

# MySqlPayload: the archive only. barcodeprinter.cnf lives in the same staged
# folder but is delivered by the deployment scripts, not by the MSI.
$mysqlFiles = Get-ChildItem $mysqlStage -File -Filter "*.zip" | Select-Object -ExpandProperty FullName
if (-not $mysqlFiles) { throw "No MySQL archive found in $mysqlStage to harvest." }
$mysqlHarvestDir = New-HarvestMirror -Name "mysql" -SourceFiles $mysqlFiles

$harvestGroups = @(
    @{ Group = "ApiPayload";      DirRef = "ApiDir";        Var = "ApiDir";            Source = "$staging\api" }
    @{ Group = "ClientPayload";   DirRef = "ClientDir";     Var = "ClientDir";         Source = "$staging\client" }
    @{ Group = "MigratorPayload"; DirRef = "MigratorDir";   Var = "MigratorDir";       Source = "$staging\migrator" }
    @{ Group = "ScriptPayload";   DirRef = "INSTALLFOLDER"; Var = "ScriptsHarvestDir"; Source = $scriptsHarvestDir }
    @{ Group = "MySqlPayload";    DirRef = "MySqlDir";      Var = "MySqlHarvestDir";   Source = $mysqlHarvestDir }
)

$fragments = @()
foreach ($g in $harvestGroups) {
    $fragment = Join-Path $harvest "$($g.Group).wxs"
    & $heat dir $g.Source `
        -cg $g.Group `
        -dr $g.DirRef `
        -var "var.$($g.Var)" `
        -ag -sfrag -srd -sreg -scom -svb6 `
        -nologo `
        -out $fragment
    if ($LASTEXITCODE -ne 0) { throw "heat failed to harvest $($g.Source) (exit $LASTEXITCODE)." }
    if (-not (Test-Path $fragment)) { throw "heat reported success but produced no $fragment." }

    # A harvest that quietly produced nothing is a build that quietly ships an
    # empty feature, and the first sign of it is a customer with no application.
    $harvested = ([xml](Get-Content $fragment)).Wix.Fragment.ComponentGroup.ComponentRef.Count
    if (-not $harvested) { throw "heat harvested no components into $($g.Group) from $($g.Source)." }

    # -gg would have written literal GUIDs; -ag must leave them for the
    # compiler. Assert it rather than trust it — this is the single property
    # the whole upgrade path rests on.
    $literalGuids = Select-String -Path $fragment -Pattern 'Guid="\{' -AllMatches
    if ($literalGuids) {
        throw "$fragment contains baked-in component GUIDs. heat must be run with -ag, not -gg, or every build changes component identity."
    }

    Write-Host ("  {0,-16} {1,4} components -> {2}" -f $g.Group, $harvested, (Split-Path $fragment -Leaf))
    $fragments += $fragment
}

# ---- 3b. Build the bootstrapper application --------------------------------
#
# The custom BA is an ordinary .NET library, but it is not loaded like one: Burn
# starts dnchost.dll, dnchost starts the runtime through hostfxr, and hostfxr
# loads this assembly. Three of the five files below exist only because of that
# hosting arrangement, and the failure mode when one is missing is a bundle that
# builds perfectly and then shows NO WINDOW AT ALL — no error, no log entry, no
# process left behind to look at. They are asserted here rather than discovered
# on a customer's machine.

Write-Host "== Building the bootstrapper application ==" -ForegroundColor Cyan
& dotnet build $baProject -c $Configuration -v minimal
if ($LASTEXITCODE -ne 0) { throw "The bootstrapper application failed to build (exit $LASTEXITCODE)." }

$baRequired = @(
    "BarcodePrinter.BootstrapperApp.dll"              # the BA itself
    "BarcodePrinter.BootstrapperApp.runtimeconfig.json" # which runtime hostfxr should start
    "BarcodePrinter.BootstrapperApp.deps.json"          # what to resolve alongside it
    "WixToolset.Mba.Core.dll"                           # the managed BA API
    "mbanative.dll"                                     # its native marshalling shim
)
foreach ($file in $baRequired) {
    $path = Join-Path $baDir $file
    if (-not (Test-Path $path)) {
        throw "The bootstrapper application built but $file is missing from $baDir. The bundle would start with no user interface."
    }
}
Write-Host "  $($baRequired.Count) BA payloads present in $baDir" -ForegroundColor Green

# ---- 4. Build the MSI ------------------------------------------------------

$msiPath = Join-Path $obj "BarcodePrinter.msi"

if ($SkipMsi) {
    if (-not (Test-Path $msiPath)) {
        throw "-SkipMsi was given but there is no MSI at $msiPath to reuse."
    }
    Write-Host "== Reusing the existing application package (-SkipMsi) ==" -ForegroundColor Yellow
    Write-Host "  $([math]::Round((Get-Item $msiPath).Length/1MB)) MB, built $((Get-Item $msiPath).LastWriteTime)" -ForegroundColor DarkGray
} else {

Write-Host "== Building the application package ==" -ForegroundColor Cyan

# -d paths are absolute. A relative one resolves against installer\ (the
# directory holding the .wxs being compiled), not against the working
# directory, and lands somewhere that does not exist.
# -ext is version-pinned: see the note on the shared extension cache above.
Invoke-Wix4 @(
    "build"
    (Join-Path $installer "Package.wxs")
    $fragments
    "-arch"; "x64"
    "-ext"; $Wix4Util
    "-loc"; (Join-Path $installer "en-us.wxl")
    "-d"; "ProductVersion=$Version"
    "-d"; "ApiDir=$staging\api"
    "-d"; "ClientDir=$staging\client"
    "-d"; "MigratorDir=$staging\migrator"
    "-d"; "ScriptsHarvestDir=$scriptsHarvestDir"
    "-d"; "MySqlHarvestDir=$mysqlHarvestDir"
    "-intermediateFolder"; $obj
    "-o"; $msiPath
)
if ($LASTEXITCODE -ne 0) { throw "The MSI failed to build (exit $LASTEXITCODE)." }
Write-Host "  $([math]::Round((Get-Item $msiPath).Length/1MB)) MB" -ForegroundColor Green

}

# ---- 5. Sign the MSI before it goes into the bundle ------------------------
# Order matters: the bundle records a hash of every payload, so signing the MSI
# afterwards would invalidate it and the bundle would refuse to install it.

function Invoke-Signing([string[]]$Paths) {
    if (-not $CertThumbprint) { return }
    $certificate = @(
        Get-Item "Cert:\CurrentUser\My\$CertThumbprint"  -ErrorAction SilentlyContinue
        Get-Item "Cert:\LocalMachine\My\$CertThumbprint" -ErrorAction SilentlyContinue
    ) | Select-Object -First 1
    if (-not $certificate) { throw "Code-signing certificate $CertThumbprint was not found." }

    # Timestamping is what keeps the signature valid after the certificate
    # expires. Without it, every installed copy starts failing signature checks
    # on the day the certificate lapses.
    $signed = Set-AuthenticodeSignature -FilePath $Paths -Certificate $certificate `
        -TimestampServer $TimestampUrl -HashAlgorithm SHA256
    $bad = @($signed | Where-Object Status -ne "Valid")
    if ($bad) { throw "Signing failed for: $($bad.Path -join ', ')" }
    Write-Host "  signed $($Paths.Count) file(s)" -ForegroundColor Green
}

if ($CertThumbprint) {
    Write-Host "== Signing the package ==" -ForegroundColor Cyan
    Invoke-Signing @($msiPath)
}

# ---- 6. Build the bundle ---------------------------------------------------

Write-Host "== Building the bootstrapper ==" -ForegroundColor Cyan
$setupPath = Join-Path $outputDir "BarcodePrinterSetup.exe"
$setupBuilt = Join-Path $obj "BarcodePrinterSetup.exe"

# Built into the intermediate folder and moved separately, rather than letting
# wix write straight to the output path. Real-time antivirus opens a freshly
# written 400 MB executable to scan it, and wix's own move fails outright with
# "used by another process" — a build failure caused by nothing being wrong.
#
# Every -d value is passed as ONE array element. The repository path contains
# spaces, and PowerShell splits an unquoted -d BaDir=$baDir across several
# arguments; wix reports that as a bare exit code 104 with no message at all.
Invoke-Wix4 @(
    "build"
    (Join-Path $installer "Bundle.wxs")
    "-arch"; "x64"
    "-ext"; $Wix4Util
    "-ext"; $Wix4Bal
    "-loc"; (Join-Path $installer "en-us.wxl")
    "-d"; "ProductVersion=$Version"
    "-d"; "MsiPath=$msiPath"
    "-d"; "VCRedistExe=$vcRedist"
    "-d"; "NetRuntimeExe=$netRuntimeExe"
    "-d"; "NetRuntimeVersion=$NetRuntimeVersion"
    "-d"; "AppIcon=$appIcon"
    "-d"; "BaDir=$baDir"
    "-intermediateFolder"; $obj
    "-o"; $setupBuilt
)
if ($LASTEXITCODE -ne 0) { throw "The bundle failed to build (exit $LASTEXITCODE)." }

Remove-Item $setupPath -Force -ErrorAction SilentlyContinue
$moved = $false
foreach ($attempt in 1..10) {
    try { Move-Item $setupBuilt $setupPath -Force -ErrorAction Stop; $moved = $true; break }
    catch { Start-Sleep -Seconds 3 }
}
if (-not $moved) {
    throw "Built the bundle but could not move it to $setupPath - something is holding the file (real-time antivirus is the usual culprit)."
}

# ---- 7. Sign the bundle ----------------------------------------------------
# A bundle carries a detached signature over its container, so it must be signed
# through wix burn's own detach/reattach dance if the engine is to stay valid.
# Set-AuthenticodeSignature on the finished exe is correct for Burn v4+.

if ($CertThumbprint) {
    Write-Host "== Signing the bootstrapper ==" -ForegroundColor Cyan
    Invoke-Signing @($setupPath)
} else {
    Write-Warning "Not signed. The customer will see Microsoft Defender SmartScreen's 'Windows protected your PC' the first time they run this, and will have to choose More info > Run anyway. Only an Authenticode certificate removes that; re-run with -CertThumbprint once one is available."
}

$size = [math]::Round((Get-Item $setupPath).Length / 1MB)
Write-Host ""
Write-Host "Built $setupPath ($size MB)" -ForegroundColor Green
Write-Host "This single file is what the customer receives."
