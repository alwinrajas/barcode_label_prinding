<#
.SYNOPSIS
    Installs the Barcode Label Printing desktop client on a workstation.

.DESCRIPTION
    The client configuration contains ONLY the API base URL (A-28 / §19.4).
    No database connection string, no credentials of any kind — the client has
    no route to MySQL and nothing worth extracting from its config file.

    Supports silent installation for GPO/Intune:
        powershell -File Install-Client.ps1 -ApiBaseUrl https://server:5001 -Silent

.EXAMPLE
    .\Install-Client.ps1 -ApiBaseUrl https://barcodesrv:5001 -CertificateFile .\lan-ca.cer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApiBaseUrl,
    [string]$InstallPath = "$env:ProgramFiles\Barcode Label Printing",
    [string]$CertificateFile,
    [switch]$Silent,
    [switch]$NoShortcut,

    # The client payload is already in place and belongs to someone else — the
    # MSI. Do the configuration half only: client.json, certificate trust,
    # shortcut, connectivity check. Copying several hundred megabytes on top of
    # files an installer is tracking would break its repair and uninstall.
    [switch]$ConfigureOnly,

    # Add/Remove Programs is the installer's job when there is one.
    [switch]$NoUninstallEntry
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell session."
}

if ($ApiBaseUrl -notmatch '^https?://') {
    throw "-ApiBaseUrl must be a full URL, e.g. https://barcodesrv:5001"
}
if ($ApiBaseUrl -match '^http://' -and $ApiBaseUrl -notmatch 'localhost|127\.0\.0\.1') {
    Write-Warning "Plain HTTP to a remote server sends access tokens in the clear. Use HTTPS outside a local test."
}

if ($ConfigureOnly) {
    if (-not (Test-Path (Join-Path $InstallPath "BarcodePrinter.Wpf.exe"))) {
        throw "-ConfigureOnly was given but BarcodePrinter.Wpf.exe is not in '$InstallPath'."
    }
} else {
    $source = Join-Path $PSScriptRoot "client"
    if (-not (Test-Path (Join-Path $source "BarcodePrinter.Wpf.exe"))) {
        throw "client\BarcodePrinter.Wpf.exe not found. Run this from the folder Publish.ps1 produced."
    }

    # A print run must never be interrupted by an upgrade.
    $running = Get-Process -Name "BarcodePrinter.Wpf" -ErrorAction SilentlyContinue
    if ($running) {
        if ($Silent) {
            throw "The client is running (PID $($running.Id -join ', ')). Retry when the operator has closed it."
        }
        $answer = Read-Host "The client is running. Close it and continue? [y/N]"
        if ($answer -ne 'y') { throw "Cancelled." }
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }

    Write-Host "Installing to $InstallPath..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
    Copy-Item (Join-Path $source "*") $InstallPath -Recurse -Force
}

# This package normally reaches the workstation as a zip that somebody
# downloaded, and every file extracted from it carries a Zone.Identifier
# marking it as internet content. Windows then refuses to load the DLLs, or
# SmartScreen blocks the exe, on a machine where an administrator has just
# deliberately installed it. Strip the mark from what we placed here — not from
# the source folder, which is not ours to modify.
$blocked = Get-ChildItem $InstallPath -Recurse -File |
    Where-Object { Get-Item $_.FullName -Stream Zone.Identifier -ErrorAction SilentlyContinue }
if ($blocked) {
    Write-Host "Clearing the internet mark from $($blocked.Count) extracted files..." -ForegroundColor Cyan
    $blocked | Unblock-File
}

# Machine-wide, so every operator on a shared warehouse terminal reads the same
# server address.
$configDir = Join-Path $env:ProgramData "BarcodePrinter"
New-Item -ItemType Directory -Path $configDir -Force | Out-Null
@{ apiBaseUrl = $ApiBaseUrl } | ConvertTo-Json |
    Set-Content (Join-Path $configDir "client.json") -Encoding UTF8

# Operators are not administrators; the client writes its own log files.
$logDir = Join-Path $configDir "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$acl = Get-Acl $configDir
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Users", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl $configDir $acl

if ($CertificateFile) {
    Write-Host "Trusting the internal CA certificate..." -ForegroundColor Cyan
    Import-Certificate -FilePath $CertificateFile -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
}

if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut(
        (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\Barcode Label Printing.lnk"))
    $shortcut.TargetPath = Join-Path $InstallPath "BarcodePrinter.Wpf.exe"
    $shortcut.WorkingDirectory = $InstallPath
    $shortcut.Description = "Barcode Label Printing"
    $shortcut.Save()
}

$version = (Get-Item (Join-Path $InstallPath "BarcodePrinter.Wpf.exe")).VersionInfo.FileVersion

# Add/Remove Programs, so IT can see what is deployed where. Skipped under an
# installer: two uninstall entries for one product is worse than none, and the
# one that runs Remove-Item would leave the MSI believing it is still installed.
if (-not $NoUninstallEntry) {
$uninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BarcodePrinterClient"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty $uninstallKey -Name "DisplayName"     -Value "Barcode Label Printing"
Set-ItemProperty $uninstallKey -Name "DisplayVersion"  -Value $version
Set-ItemProperty $uninstallKey -Name "Publisher"       -Value "Barcode Label Printing"
Set-ItemProperty $uninstallKey -Name "InstallLocation" -Value $InstallPath
Set-ItemProperty $uninstallKey -Name "NoModify"        -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name "NoRepair"        -Value 1 -Type DWord
Set-ItemProperty $uninstallKey -Name "UninstallString" -Value `
    "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command `"Remove-Item '$InstallPath' -Recurse -Force; Remove-Item '$uninstallKey' -Recurse -Force`""
}

# Fail here rather than at first login: a client that cannot see the server is
# an IT problem to fix now, not a support call from the shop floor tomorrow.
Write-Host "Checking connectivity to $ApiBaseUrl..." -ForegroundColor Cyan
try {
    $health = Invoke-WebRequest "$ApiBaseUrl/health" -TimeoutSec 10 -UseBasicParsing
    if ($health.StatusCode -eq 200) {
        Write-Host "Server reachable and healthy." -ForegroundColor Green
    } else {
        Write-Warning "Server answered with HTTP $($health.StatusCode)."
    }
} catch {
    Write-Warning "Could not reach $ApiBaseUrl/health: $($_.Exception.Message)"
    Write-Warning "Check the firewall (the API port must be open to this subnet) and, for HTTPS, that the CA certificate is trusted."
}

Write-Host ""
Write-Host "Installed version $version." -ForegroundColor Green
Write-Host "  API      : $ApiBaseUrl"
Write-Host "  Config   : $(Join-Path $configDir 'client.json')  (API URL only — no credentials)"
Write-Host "  Logs     : $logDir"
