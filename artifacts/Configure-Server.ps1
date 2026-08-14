<#
.SYNOPSIS
    Changes the settings of an installed Barcode Printer API service without
    reinstalling it.

.DESCRIPTION
    Install-Server.ps1 is for putting the service on the box; this is for the
    things that change afterwards — the database password rotated, the LAN was
    renumbered, the pilot certificate was replaced with a real one.

    It deliberately cannot change the JWT signing key or the Data Protection key
    ring. Rotating the first logs every user out mid-shift; losing the second
    makes the stored Oracle password permanently undecryptable.

    Every change is validated, the service is restarted, and /health is polled.
    If the new configuration does not come up, the previous appsettings is put
    back and the service is restarted on it — a bad password should not take the
    line down until someone notices.

.EXAMPLE
    # See what is configured now (secrets redacted):
    .\Configure-Server.ps1 -Show

.EXAMPLE
    # Rotate the database password:
    .\Configure-Server.ps1 -MySqlPassword (Read-Host "New password" -AsSecureString)

.EXAMPLE
    # Replace the pilot self-signed certificate with the one from the internal CA:
    .\Configure-Server.ps1 -CertThumbprint A1B2C3...
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$InstallRoot = "D:\BarcodePrinter",
    [string]$ServiceName = "BarcodePrinter.Api",
    [string]$ServiceAccount = "BarcodePrinterSvc",

    [switch]$Show,

    # Database
    [string]$MySqlHost,
    [int]$MySqlPort,
    [string]$MySqlDatabase,
    [string]$MySqlUser,
    [securestring]$MySqlPassword,

    # HTTPS
    [int]$HttpsPort,
    [string]$CertThumbprint,

    # Firewall
    [string]$LanSubnet,

    # Operations
    [ValidateSet("Verbose", "Debug", "Information", "Warning", "Error")]
    [string]$LogLevel,
    [string]$MinimumClientVersion
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell session."
}

$settingsPath = Join-Path $InstallRoot "api\appsettings.Production.json"
if (-not (Test-Path $settingsPath)) {
    throw "$settingsPath not found. Is the server installed at -InstallRoot $InstallRoot?"
}

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json -AsHashtable

function Get-ConnectionBuilder {
    # MySqlConnector ships with the migrator, and parsing a connection string by
    # hand is how a stray semicolon in a password turns into a broken install.
    $dll = Join-Path $PSScriptRoot "migrator\MySqlConnector.dll"
    if (-not (Test-Path $dll)) {
        $dll = Join-Path $InstallRoot "api\MySqlConnector.dll"
    }
    if (-not (Test-Path $dll)) {
        throw "MySqlConnector.dll not found next to this script or in the install folder."
    }
    Add-Type -Path $dll
    New-Object MySqlConnector.MySqlConnectionStringBuilder($settings.ConnectionStrings.BarcodePrinter)
}

# ---- Show --------------------------------------------------------------------------

if ($Show) {
    $builder = Get-ConnectionBuilder
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    $certSubject = $settings.Kestrel.Endpoints.Https.Certificate.Subject
    $cert = Get-ChildItem Cert:\LocalMachine\My -ErrorAction SilentlyContinue |
        Where-Object { $_.HasPrivateKey -and $_.GetNameInfo('SimpleName', $false) -eq $certSubject } |
        Select-Object -First 1

    [pscustomobject]@{
        Service              = "$ServiceName ($(if ($service) { $service.Status } else { 'not installed' }))"
        StartType            = if ($service) { (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartMode } else { "-" }
        Account              = if ($service) { (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").StartName } else { "-" }
        Url                  = $settings.Kestrel.Endpoints.Https.Url
        CertificateSubject   = $certSubject
        CertificateThumbprint= if ($cert) { $cert.Thumbprint } else { "NOT FOUND IN LocalMachine\My" }
        CertificateExpires   = if ($cert) { $cert.NotAfter.ToString('yyyy-MM-dd') } else { "-" }
        MySqlServer          = "$($builder.Server):$($builder.Port)"
        MySqlDatabase        = $builder.Database
        MySqlUser            = $builder.UserID
        MySqlPassword        = "***"
        LogLevel             = $settings.Serilog.MinimumLevel.Default
        MinimumClientVersion = $settings.MinimumClientVersion
        Firewall             = (Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue |
                                 Get-NetFirewallAddressFilter).RemoteAddress -join ', '
    } | Format-List
    return
}

$changed = @()

# ---- Database ----------------------------------------------------------------------

if ($MySqlHost -or $MySqlPort -or $MySqlDatabase -or $MySqlUser -or $MySqlPassword) {
    $builder = Get-ConnectionBuilder
    if ($MySqlHost)     { $builder.Server   = $MySqlHost;     $changed += "MySQL host -> $MySqlHost" }
    if ($MySqlPort)     { $builder.Port     = $MySqlPort;     $changed += "MySQL port -> $MySqlPort" }
    if ($MySqlDatabase) { $builder.Database = $MySqlDatabase; $changed += "database -> $MySqlDatabase" }
    if ($MySqlUser)     { $builder.UserID   = $MySqlUser;     $changed += "user -> $MySqlUser" }
    if ($MySqlPassword) {
        $builder.Password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($MySqlPassword))
        $changed += "password rotated"
    }

    # Prove the new credentials work against the real server before they are the
    # only ones the service has.
    Write-Host "Verifying the new database settings..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "migrator\BarcodePrinter.DbMigrator.exe") $builder.ConnectionString --preflight-only
    if ($LASTEXITCODE -ne 0) {
        throw "The new database settings did not pass preflight. Nothing was changed."
    }
    $settings.ConnectionStrings.BarcodePrinter = $builder.ConnectionString
}

# ---- Certificate -------------------------------------------------------------------

if ($CertThumbprint) {
    $certificate = Get-Item "Cert:\LocalMachine\My\$CertThumbprint" -ErrorAction SilentlyContinue
    if (-not $certificate) {
        throw "Certificate $CertThumbprint was not found in LocalMachine\My. The service account cannot read your personal store — import it for the machine."
    }
    if (-not $certificate.HasPrivateKey) {
        throw "Certificate $CertThumbprint has no private key. Import the .pfx, not the .cer."
    }
    $subject = $certificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)

    # Same private-key grant the installer makes: without it Kestrel fails to
    # start with an access-denied that looks like a bad certificate.
    $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    $keyFileName = if ($privateKey -is [System.Security.Cryptography.RSACng]) {
        $privateKey.Key.UniqueName
    } else {
        $privateKey.CspKeyContainerInfo.UniqueKeyContainerName
    }
    $keyFile = @(
        (Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyFileName)
        (Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyFileName)
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $keyFile) {
        throw "The private key file for $CertThumbprint was not found."
    }
    # By SID: ".\Account" is a PowerShell path convention, not a Windows account
    # name, and NTAccount cannot translate it.
    $svcAccount = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
    if (-not $svcAccount) {
        throw "Local account $ServiceAccount not found; cannot grant it access to the certificate key."
    }
    $keyAcl = Get-Acl $keyFile
    $keyAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        [System.Security.Principal.SecurityIdentifier]::new($svcAccount.SID.Value), "Read", "Allow")))
    Set-Acl $keyFile $keyAcl

    $settings.Kestrel.Endpoints.Https.Certificate.Subject = $subject
    $changed += "certificate -> $subject ($CertThumbprint, expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))"

    Export-Certificate -Cert $certificate -FilePath (Join-Path $InstallRoot "barcodeprinter-lan.cer") -Force | Out-Null
}

# ---- Port --------------------------------------------------------------------------

if ($HttpsPort) {
    $settings.Kestrel.Endpoints.Https.Url = "https://0.0.0.0:$HttpsPort"
    $changed += "HTTPS port -> $HttpsPort"
}

# ---- Operations --------------------------------------------------------------------

if ($LogLevel) {
    $settings.Serilog.MinimumLevel.Default = $LogLevel
    $changed += "log level -> $LogLevel"
}
if ($MinimumClientVersion) {
    $settings.MinimumClientVersion = $MinimumClientVersion
    $changed += "minimum client version -> $MinimumClientVersion"
}

# ---- Firewall ----------------------------------------------------------------------

$currentPort = ([uri]$settings.Kestrel.Endpoints.Https.Url).Port
if ($LanSubnet -or $HttpsPort) {
    $rule = Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue
    $subnet = if ($LanSubnet) { $LanSubnet }
              elseif ($rule)  { ($rule | Get-NetFirewallAddressFilter).RemoteAddress }
              else            { "192.168.0.0/16" }
    $rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "Barcode Printer API" -Direction Inbound `
        -Protocol TCP -LocalPort $currentPort -RemoteAddress $subnet -Action Allow `
        -Profile Domain, Private | Out-Null
    $changed += "firewall -> port $currentPort from $subnet"
}

if (-not $changed) {
    Write-Host "Nothing to change. Pass -Show to see the current configuration." -ForegroundColor Yellow
    return
}

Write-Host ""
Write-Host "Changes to apply:" -ForegroundColor Cyan
$changed | ForEach-Object { Write-Host "  - $_" }

if (-not $PSCmdlet.ShouldProcess($ServiceName, "Apply the changes above and restart")) {
    return
}

# ---- Apply, restart, verify — and roll back if it does not come up ------------------

$backupPath = "$settingsPath.bak"
Copy-Item $settingsPath $backupPath -Force
$settings | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8

function Test-Healthy([int]$Port) {
    foreach ($attempt in 1..20) {
        try {
            if ((Invoke-WebRequest "https://localhost:$Port/health" -SkipCertificateCheck -TimeoutSec 5).StatusCode -eq 200) {
                return $true
            }
        } catch { Start-Sleep -Seconds 2 }
    }
    return $false
}

Write-Host "Restarting $ServiceName..." -ForegroundColor Cyan
Restart-Service -Name $ServiceName -Force
(Get-Service $ServiceName).WaitForStatus("Running", "00:01:00")

if (Test-Healthy $currentPort) {
    Remove-Item $backupPath -Force
    Write-Host ""
    Write-Host "Applied. https://$($env:COMPUTERNAME):$currentPort is healthy." -ForegroundColor Green
    if ($CertThumbprint) {
        Write-Host "Re-run Install-Client.ps1 with the new $InstallRoot\barcodeprinter-lan.cer on each workstation." -ForegroundColor Yellow
    }
} else {
    Write-Warning "The service did not come up healthy. Rolling back to the previous configuration."
    Move-Item $backupPath $settingsPath -Force
    Restart-Service -Name $ServiceName -Force
    if (Test-Healthy $currentPort) {
        throw "The change was rejected and the previous configuration is running again. Check $InstallRoot\logs for why the new one failed."
    }
    throw "The change failed AND the rollback did not come up healthy. The service is down — check $InstallRoot\logs now."
}
