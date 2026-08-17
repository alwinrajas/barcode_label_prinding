<#
.SYNOPSIS
    Removes the API service, its firewall rule and the backup schedule.

.DESCRIPTION
    Data is kept by default. The database, images, imports, logs and the Data
    Protection key ring survive unless -RemoveData is given, and even then the
    database itself is never dropped — losing print history is a compliance
    event, not a housekeeping step.

.EXAMPLE
    .\Uninstall-Server.ps1
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    [string]$InstallRoot = "D:\BarcodePrinter",
    [string]$ServiceName = "BarcodePrinter.Api",
    [switch]$RemoveData,
    [switch]$RemoveServiceAccount,
    [string]$ServiceAccount = "BarcodePrinterSvc",

    # Only removes a certificate this installer generated (friendly name
    # "Barcode Printer API"). A certificate issued by your CA is left alone —
    # it may well be in use by something else.
    [switch]$RemoveGeneratedCertificate,
    [int]$HttpsPort = 5001
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell session."
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Stop and remove the service")) {
        if ($service.Status -ne "Stopped") {
            Write-Host "Stopping $ServiceName..." -ForegroundColor Cyan
            Stop-Service -Name $ServiceName -Force
            $service.WaitForStatus("Stopped", "00:01:00")
        }
        & sc.exe delete $ServiceName | Out-Null
        Write-Host "Service removed." -ForegroundColor Green
    }
} else {
    Write-Host "Service $ServiceName is not installed." -ForegroundColor DarkGray
}

Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
Write-Host "Firewall rule removed." -ForegroundColor Green

# Install-Server.ps1 sets this machine-wide. Leaving it behind silently changes
# how every other .NET application on the box resolves its configuration —
# most visibly, a developer's `dotnet run` would load Production settings that
# are not there. Only clear the value this installer set; anything else is
# someone else's and is left alone.
if ([Environment]::GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Machine") -eq "Production") {
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", $null, "Machine")
    Write-Host "Machine-wide ASPNETCORE_ENVIRONMENT cleared." -ForegroundColor Green
}

# Installs made before the certificate fix bound a certificate to http.sys.
# Kestrel never used it, but leaving it behind blocks the port for anything else.
$sslBinding = & netsh http show sslcert ipport=0.0.0.0:$HttpsPort 2>$null
if ($LASTEXITCODE -eq 0 -and $sslBinding -match 'Certificate Hash') {
    & netsh http delete sslcert ipport=0.0.0.0:$HttpsPort | Out-Null
    Write-Host "Removed the stale http.sys certificate binding on port $HttpsPort." -ForegroundColor Green
}

if ($RemoveGeneratedCertificate) {
    $generated = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -eq "Barcode Printer API" }
    foreach ($certificate in $generated) {
        if ($PSCmdlet.ShouldProcess($certificate.Thumbprint, "Remove the generated self-signed certificate")) {
            Remove-Item $certificate.PSPath -Force
            Write-Host "Removed certificate $($certificate.Thumbprint)." -ForegroundColor Green
        }
    }
    if (-not $generated) {
        Write-Host "No installer-generated certificate found." -ForegroundColor DarkGray
    }
}

foreach ($task in @("BarcodePrinter Full Backup", "BarcodePrinter Binlog Copy")) {
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
}
Write-Host "Backup tasks removed." -ForegroundColor Green

if ($RemoveData) {
    Write-Warning "-RemoveData will delete images, imports, logs and THE DATA PROTECTION KEY RING."
    Write-Warning "Without the key ring the stored Oracle password is permanently unrecoverable."
    if ($PSCmdlet.ShouldProcess($InstallRoot, "Delete application data")) {
        Remove-Item $InstallRoot -Recurse -Force
        Write-Host "$InstallRoot deleted." -ForegroundColor Yellow
    }
} else {
    Write-Host "Data kept at $InstallRoot (pass -RemoveData to delete it)." -ForegroundColor DarkGray
}

# The MySQL database is never dropped here. Dropping it is a separate, deliberate
# decision, made with a verified backup in hand and recorded in the runbook.
Write-Host "The MySQL database was NOT dropped. Remove it by hand if that is genuinely intended." -ForegroundColor Yellow

if ($RemoveServiceAccount) {
    if ($PSCmdlet.ShouldProcess($ServiceAccount, "Delete the local service account")) {
        # Revoke "log on as a service" first. Deleting the account leaves its SID
        # behind in the policy as an orphan entry, which shows up as an
        # unresolvable SID in secpol and confuses the next audit.
        $account = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
        if ($account) {
            $rightsDir = Join-Path $env:TEMP "bp-rights-remove"
            New-Item -ItemType Directory -Path $rightsDir -Force | Out-Null
            $exportInf = Join-Path $rightsDir "export.inf"
            $revokeInf = Join-Path $rightsDir "revoke.inf"
            & secedit /export /cfg $exportInf /areas USER_RIGHTS | Out-Null
            $sid = $account.SID.Value
            $lines = Get-Content $exportInf
            if (($lines | Where-Object { $_ -match '^SeServiceLogonRight' }) -match [regex]::Escape($sid)) {
                $lines = $lines | ForEach-Object {
                    if ($_ -match '^SeServiceLogonRight') {
                        ($_ -replace ",\*$sid", "") -replace "=\s*\*$sid,", "= "
                    } else { $_ }
                }
                $lines | Set-Content $revokeInf -Encoding Unicode
                & secedit /configure /db (Join-Path $rightsDir "secedit.sdb") /cfg $revokeInf /areas USER_RIGHTS | Out-Null
                Write-Host "Revoked 'log on as a service'." -ForegroundColor Green
            }
            Remove-Item $rightsDir -Recurse -Force -ErrorAction SilentlyContinue
        }

        Remove-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
        Write-Host "Service account removed." -ForegroundColor Green
    }
}
