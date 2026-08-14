<#
.SYNOPSIS
    Installs the Barcode Printer API as a Windows Service, applies the database
    schema, and opens exactly one firewall port.

.DESCRIPTION
    Implements the deployment architecture in blueprint §16:

      * the service runs under a DEDICATED low-privilege account, never
        LocalSystem, and only that account can read the folder holding the
        connection string, the JWT signing key and the Data Protection key ring;
      * the schema is applied by the migrator as an EXPLICIT, logged step, never
        automatically at API startup, so a mis-timed service restart can never
        alter the schema;
      * the LAN firewall opens the API port only. MySQL stays bound to
        127.0.0.1 and 3306 is never opened — the API is its only client.

    Re-runnable: an upgrade is "copy the new api\ folder, run this again".

.EXAMPLE
    # LAN / pilot install, no internal CA available:
    .\Install-Server.ps1 -ServiceAccountPassword (Read-Host -AsSecureString) `
                         -MySqlPassword (Read-Host -AsSecureString) `
                         -GenerateSelfSignedCert

.EXAMPLE
    # Production install with a certificate issued by the internal CA:
    .\Install-Server.ps1 -ServiceAccountPassword (Read-Host -AsSecureString) `
                         -MySqlPassword (Read-Host -AsSecureString) `
                         -CertThumbprint A1B2C3... -LanSubnet 192.168.10.0/24
#>
[CmdletBinding()]
param(
    # Data root: configuration, logs, images, imports, the key ring, backups.
    # ProgramData rather than a hard-coded D:\ — a customer workstation is not
    # guaranteed to have a second volume.
    [string]$InstallRoot   = "$env:ProgramData\BarcodePrinter",

    # Where the API binaries live. Left empty, this script copies api\ from the
    # package into $InstallRoot\api and owns them. The MSI passes its own
    # install directory instead, because an installer that lets a script copy
    # files behind its back cannot repair, patch or cleanly uninstall them.
    [string]$ApiBinPath,

    [string]$ServiceName   = "BarcodePrinter.Api",
    [string]$ServiceAccount = "BarcodePrinterSvc",
    [Parameter(Mandatory)]
    [securestring]$ServiceAccountPassword,
    [string]$MySqlHost     = "127.0.0.1",
    [int]$MySqlPort        = 3306,
    [string]$MySqlDatabase = "barcodeprinter",
    [string]$MySqlUser     = "barcodeprinter",
    [Parameter(Mandatory)]
    [securestring]$MySqlPassword,
    [int]$HttpsPort        = 5001,
    [string]$LanSubnet     = "192.168.0.0/16",

    # Certificate: supply ONE of these. There is deliberately no "no certificate"
    # path — HTTPS is enforced outside Development, so an install without a
    # usable certificate produces a service that cannot start.
    [string]$CertThumbprint,
    [switch]$GenerateSelfSignedCert,
    [string[]]$CertDnsName,

    # Windows service that must be running before the API starts. Auto-detected
    # when MySQL is local; pass "" to install no dependency.
    [string]$MySqlServiceName,

    [switch]$SkipPreflight
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell session."
}

$source = $PSScriptRoot
$managedBinaries = -not $ApiBinPath      # true when this script owns the api\ copy

if (-not (Test-Path (Join-Path $source "migrator\BarcodePrinter.DbMigrator.exe"))) {
    throw "migrator\BarcodePrinter.DbMigrator.exe not found. Run this from the folder Publish.ps1 produced."
}
if ($managedBinaries -and -not (Test-Path (Join-Path $source "api\BarcodePrinter.Api.exe"))) {
    throw "api\BarcodePrinter.Api.exe not found. Run this from the folder Publish.ps1 produced."
}
if (-not $managedBinaries -and -not (Test-Path (Join-Path $ApiBinPath "BarcodePrinter.Api.exe"))) {
    throw "BarcodePrinter.Api.exe not found in -ApiBinPath '$ApiBinPath'."
}

function ConvertFrom-SecureStringPlain([securestring]$Value) {
    [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value))
}

if (-not $CertThumbprint -and -not $GenerateSelfSignedCert) {
    throw @"
No certificate specified. Pass -CertThumbprint <thumbprint of a certificate in
LocalMachine\My> for a production install, or -GenerateSelfSignedCert for a LAN
pilot. HTTPS is enforced outside Development, so the service cannot start
without one.
"@
}
if ($CertThumbprint -and $GenerateSelfSignedCert) {
    throw "Pass either -CertThumbprint or -GenerateSelfSignedCert, not both."
}

$mysqlPasswordPlain = ConvertFrom-SecureStringPlain $MySqlPassword
$connectionString = "Server=$MySqlHost;Port=$MySqlPort;Database=$MySqlDatabase;" +
    "Uid=$MySqlUser;Pwd=$mysqlPasswordPlain;AllowLoadLocalInfile=true;" +
    "MinimumPoolSize=5;MaximumPoolSize=60;GuidFormat=None"

# ---- 0. Preflight ------------------------------------------------------------------
#
# Runs before anything is created. Every check it makes fails far more
# confusingly later: MariaDB gets as far as a half-applied schema, and a wrong
# ngram_token_size produces a product search that silently matches nothing.

if (-not $SkipPreflight) {
    Write-Host "Checking the MySQL server..." -ForegroundColor Cyan
    # Same reason as the migration step below: the preflight reports its
    # findings on STDERR, and under $ErrorActionPreference = 'Stop' that would
    # terminate the script before its exit code — the verdict — is read.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & (Join-Path $source "migrator\BarcodePrinter.DbMigrator.exe") $connectionString --preflight-only 2>&1 |
            ForEach-Object { Write-Host "  $_" }
        $preflightExit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($preflightExit -ne 0) {
        throw "MySQL preflight failed (see above). Nothing has been installed."
    }
}

# ---- 1. Folder layout (§16) --------------------------------------------------------

Write-Host "Creating $InstallRoot..." -ForegroundColor Cyan
$folders = @{
    # When the MSI owns the binaries, api\ points at ITS directory and nothing
    # is copied there — but appsettings.Production.json still lives beside the
    # exe, because that is where the host looks for it.
    api     = if ($managedBinaries) { Join-Path $InstallRoot "api" } else { $ApiBinPath }
    images  = Join-Path $InstallRoot "images"
    imports = Join-Path $InstallRoot "imports"
    logs    = Join-Path $InstallRoot "logs"
    backup  = Join-Path $InstallRoot "backup"
    keys    = Join-Path $InstallRoot "keys"
}
foreach ($folder in $folders.Values) {
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
}

# ---- 2. Service account ------------------------------------------------------------

$account = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
if (-not $account) {
    Write-Host "Creating local service account $ServiceAccount..." -ForegroundColor Cyan
    # New-LocalUser caps Description at 48 characters and throws on anything
    # longer, which aborts the install before the account exists.
    $account = New-LocalUser -Name $ServiceAccount -Password $ServiceAccountPassword `
        -FullName "Barcode Printer API service" `
        -Description "Barcode Printer API service. Not interactive." `
        -PasswordNeverExpires -UserMayNotChangePassword
} else {
    # The password is generated fresh for every run of this script, and BOTH
    # sides must receive it: this account, and the service registration below
    # (`sc config password=`). When the account already exists — a repair or an
    # upgrade — skipping it here leaves the account on its old password while
    # the service is told the new one, and every start is then refused as a
    # logon failure that reports no error code at all: the service just stays
    # STOPPED. Fresh installs never see this because account creation and
    # service registration happen to agree.
    Write-Host "Service account $ServiceAccount already exists — resetting its password to this run's." -ForegroundColor DarkGray
    Set-LocalUser -Name $ServiceAccount -Password $ServiceAccountPassword
}

# ACL rules are built from the SID, not the name. ".\Account" is a PowerShell
# path convention, not a Windows account name: NTAccount cannot translate it and
# AddAccessRule throws "Some or all identity references could not be translated".
$accountSid = [System.Security.Principal.SecurityIdentifier]::new($account.SID.Value)

# "Log on as a service" is NOT granted automatically. Without it the service
# registers cleanly and then fails to start with error 1069, which reads like a
# bad password and sends you looking in the wrong place.
Write-Host "Granting 'log on as a service' to $ServiceAccount..." -ForegroundColor Cyan
$rightsDir = Join-Path $env:TEMP "bp-rights"
New-Item -ItemType Directory -Path $rightsDir -Force | Out-Null
$exportInf = Join-Path $rightsDir "export.inf"
$grantInf  = Join-Path $rightsDir "grant.inf"
$secDb     = Join-Path $rightsDir "secedit.sdb"

& secedit /export /cfg $exportInf /areas USER_RIGHTS | Out-Null
$policyLines = Get-Content $exportInf
# The .inf format wants the SID as text; keep it in its own variable so it can
# never be confused with the SecurityIdentifier the ACL rules are built from.
# A SID *string* handed to FileSystemAccessRule is read as an account NAME and
# fails to translate.
$accountSidText = $account.SID.Value
$logonLine = $policyLines | Where-Object { $_ -match '^SeServiceLogonRight' }

if ($logonLine -and $logonLine -match [regex]::Escape($accountSidText)) {
    Write-Host "  already granted." -ForegroundColor DarkGray
} else {
    if ($logonLine) {
        $updated = $policyLines -replace '^(SeServiceLogonRight\s*=\s*.*)$', "`$1,*$accountSidText"
    } else {
        $updated = $policyLines -replace '^\[Privilege Rights\]$',
            "[Privilege Rights]`r`nSeServiceLogonRight = *$accountSidText"
    }
    # secedit only reads UTF-16 .inf files; an ASCII one is rejected as malformed.
    $updated | Set-Content $grantInf -Encoding Unicode
    & secedit /configure /db $secDb /cfg $grantInf /areas USER_RIGHTS | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not grant SeServiceLogonRight to $ServiceAccount (secedit exit $LASTEXITCODE)."
    }
    Write-Host "  granted." -ForegroundColor Green
}
Remove-Item $rightsDir -Recurse -Force -ErrorAction SilentlyContinue

# ---- 3. Stop the running service before overwriting its files ----------------------

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping $ServiceName for upgrade..." -ForegroundColor Cyan
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus("Stopped", "00:01:00")
}

# ---- 4. Certificate ----------------------------------------------------------------
#
# Kestrel resolves its certificate from CONFIGURATION, not from an HTTP.SYS
# binding: `netsh http add sslcert` binds a certificate for http.sys, which
# Kestrel does not use, so it has no effect whatsoever on this service. The
# certificate therefore has to be named in appsettings, and it has to live in
# LocalMachine\My — the service account has its own (empty) CurrentUser store,
# so a certificate imported into the installing admin's personal store is
# invisible to the running service.

if ($GenerateSelfSignedCert) {
    # An upgrade re-runs this script. Issuing a second certificate every time
    # would pile up certificates sharing one subject — which the ambiguity check
    # below rightly refuses — and would invalidate the .cer every workstation
    # has already been told to trust. Reuse the one this installer made, unless
    # it is gone or about to expire.
    $certificate = Get-ChildItem Cert:\LocalMachine\My | Where-Object {
        $_.FriendlyName -eq "Barcode Printer API" -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30)
    } | Sort-Object NotAfter -Descending | Select-Object -First 1

    if ($certificate) {
        Write-Host ("Reusing the certificate this installer generated earlier: $($certificate.Thumbprint) " +
                    "(expires $($certificate.NotAfter.ToString('yyyy-MM-dd'))).") -ForegroundColor DarkGray
        Write-Host "  Delete it from LocalMachine\My first if you want a fresh one." -ForegroundColor DarkGray
    } else {
        if (-not $CertDnsName) {
            # Whatever the clients will actually type in the URL. The FQDN is
            # included so the same certificate keeps working on a domain-joined LAN.
            $CertDnsName = @($env:COMPUTERNAME, "$env:COMPUTERNAME.$env:USERDNSDOMAIN".TrimEnd('.'), "localhost") |
                Where-Object { $_ -and $_ -notmatch '^\.' } | Select-Object -Unique
        }
        Write-Host "Generating a self-signed certificate for: $($CertDnsName -join ', ')" -ForegroundColor Cyan
        $certificate = New-SelfSignedCertificate `
            -DnsName $CertDnsName `
            -CertStoreLocation "Cert:\LocalMachine\My" `
            -FriendlyName "Barcode Printer API" `
            -KeyExportPolicy NonExportable `
            -KeyUsage DigitalSignature, KeyEncipherment `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1") `
            -NotAfter (Get-Date).AddYears(5)
        Write-Host "  thumbprint $($certificate.Thumbprint) (expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))" -ForegroundColor Green
    }
    $CertThumbprint = $certificate.Thumbprint
} else {
    $certificate = Get-Item "Cert:\LocalMachine\My\$CertThumbprint" -ErrorAction SilentlyContinue
    if (-not $certificate) {
        $elsewhere = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $CertThumbprint }
        if ($elsewhere) {
            throw "Certificate $CertThumbprint is in CurrentUser\My, not LocalMachine\My. The service runs as $ServiceAccount and cannot see your personal store — re-import it into the machine store (Local Computer > Personal)."
        }
        throw "Certificate $CertThumbprint was not found in LocalMachine\My."
    }
}

if (-not $certificate.HasPrivateKey) {
    throw "Certificate $CertThumbprint has no private key. Import the .pfx, not the .cer."
}

# Kestrel finds the certificate by subject name, and FindBySubjectName matches on
# a substring — two certificates whose subjects overlap would make the choice
# non-deterministic across restarts. Fail now rather than serve the wrong one.
$certSubjectName = $certificate.GetNameInfo(
    [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
if (-not $certSubjectName) {
    throw "Certificate $CertThumbprint has no subject CN; Kestrel cannot select it by subject."
}
$ambiguous = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object {
    $_.HasPrivateKey -and $_.Thumbprint -ne $CertThumbprint -and
    $_.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -like "*$certSubjectName*"
})
if ($ambiguous) {
    throw ("More than one certificate in LocalMachine\My matches the subject '$certSubjectName' " +
           "($($ambiguous.Thumbprint -join ', ')). Kestrel selects by subject, so remove the stale " +
           "certificates or issue one with a distinct subject.")
}

# The service account must be able to READ the private key file. Without this
# Kestrel throws an access-denied during startup, which surfaces only as a
# service that will not start.
Write-Host "Granting $ServiceAccount read access to the certificate private key..." -ForegroundColor Cyan
$privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
if (-not $privateKey) {
    throw "Could not open the private key of $CertThumbprint. Only RSA certificates are supported."
}
$keyFileName = if ($privateKey -is [System.Security.Cryptography.RSACng]) {
    $privateKey.Key.UniqueName                                  # CNG (New-SelfSignedCertificate default)
} else {
    $privateKey.CspKeyContainerInfo.UniqueKeyContainerName       # legacy CSP
}
# Guard the empty case explicitly: Join-Path with an empty leaf returns the
# CONTAINING FOLDER, Test-Path then succeeds, and the grant below would be
# applied to every private key on the machine instead of this one.
if ([string]::IsNullOrWhiteSpace($keyFileName)) {
    throw "Could not determine the private key container for $CertThumbprint."
}
$keyFile = @(
    (Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyFileName)
    (Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyFileName)
) | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
if (-not $keyFile) {
    throw "The private key file for $CertThumbprint was not found. Is the certificate installed for the machine rather than a user?"
}
$keyAcl = Get-Acl $keyFile
$keyAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    $accountSid, "Read", "Allow")))
Set-Acl $keyFile $keyAcl
Write-Host "  granted on $keyFile" -ForegroundColor Green

# Hand the clients something to trust. Only the public certificate is exported;
# the private key never leaves this machine.
$publicCertPath = Join-Path $InstallRoot "barcodeprinter-lan.cer"
Export-Certificate -Cert $certificate -FilePath $publicCertPath -Force | Out-Null

# ---- 5. Files ----------------------------------------------------------------------

if ($managedBinaries) {
    Write-Host "Copying application files..." -ForegroundColor Cyan
    Copy-Item (Join-Path $source "api\*") $folders.api -Recurse -Force
} else {
    Write-Host "Using the installer-managed binaries at $($folders.api)." -ForegroundColor DarkGray
}

# AllowInvalid governs how Kestrel LOADS this certificate out of the machine
# store — it does not weaken anything for the clients, which still validate the
# chain normally. Without it a self-signed or not-yet-trusted LAN certificate is
# skipped at startup and the endpoint comes up with no certificate at all.
$kestrelSection = @{
    Endpoints = @{
        Https = @{
            Url = "https://0.0.0.0:$HttpsPort"
            Certificate = @{
                Subject      = $certSubjectName
                Store        = "My"
                Location     = "LocalMachine"
                AllowInvalid = $true
            }
        }
    }
}

$settingsPath = Join-Path $folders.api "appsettings.Production.json"
if (Test-Path $settingsPath) {
    # An upgrade must not rotate the signing key (it would log every user out)
    # or re-prompt for the database password. The Kestrel block is refreshed
    # regardless: installs made before the certificate fix carry an Https
    # endpoint with no certificate, which cannot start.
    Write-Host "Keeping the existing appsettings.Production.json (refreshing the HTTPS binding)." -ForegroundColor DarkGray
    # No -AsHashtable: that parameter is PowerShell 6+, and this script runs
    # under Windows PowerShell 5.1 when the installer invokes it — where it is
    # a runtime binding error that only surfaces on the UPGRADE path, never in
    # fresh-install testing. Add-Member -Force replaces the property on the
    # PSCustomObject that 5.1's ConvertFrom-Json produces.
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName Kestrel -NotePropertyValue $kestrelSection -Force
    $settings | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
} else {
    # A fresh 512-bit signing key per installation. Reusing a key across sites would
    # let a token minted at one site authenticate at another.
    $signingKeyBytes = [byte[]]::new(64)
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($signingKeyBytes)
    $signingKey = [Convert]::ToBase64String($signingKeyBytes)

    @{
        ConnectionStrings = @{ BarcodePrinter = $connectionString }
        Jwt = @{
            Issuer             = "BarcodePrinter"
            Audience           = "BarcodePrinter"
            SigningKey         = $signingKey
            AccessTokenMinutes = 15
        }
        DataProtection = @{ KeyRingPath = $folders.keys }
        Storage = @{
            ImagePath  = $folders.images
            ImportPath = $folders.imports
        }
        Backup = @{ StatusFile = Join-Path $folders.backup "backup-status.json" }
        Serilog = @{
            MinimumLevel = @{ Default = "Information" }
            WriteTo = @(
                @{
                    Name = "File"
                    Args = @{
                        path                   = (Join-Path $folders.logs "api-.log")
                        rollingInterval        = "Day"
                        retainedFileCountLimit = 30
                    }
                }
            )
        }
        MinimumClientVersion = "1.0.0"
        Kestrel = $kestrelSection
    } | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8
    Write-Host "Wrote appsettings.Production.json with a new signing key." -ForegroundColor Green
}

# ---- 6. ACLs — secrets are readable by the service account and admins only ---------

Write-Host "Restricting access to configuration and keys..." -ForegroundColor Cyan
foreach ($path in @($settingsPath, $folders.keys)) {
    $acl = Get-Acl $path
    $acl.SetAccessRuleProtection($true, $false)   # stop inheriting Users
    @($acl.Access) | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
    $identities = @(
        [System.Security.Principal.NTAccount]::new("BUILTIN\Administrators")
        [System.Security.Principal.NTAccount]::new("NT AUTHORITY\SYSTEM")
        $accountSid
    )
    # Inheritance flags describe what a rule passes to CHILDREN, so they are only
    # valid on a directory. appsettings.Production.json is a file: the same rule
    # that works on keys\ throws "No flags can be set" on it.
    $isContainer = Test-Path $path -PathType Container
    foreach ($identity in $identities) {
        $rule = if ($isContainer) {
            New-Object System.Security.AccessControl.FileSystemAccessRule(
                $identity, "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
        } else {
            New-Object System.Security.AccessControl.FileSystemAccessRule(
                $identity, "FullControl", "Allow")
        }
        $acl.AddAccessRule($rule)
    }
    Set-Acl $path $acl
}

foreach ($writable in @($folders.images, $folders.imports, $folders.logs, $folders.backup)) {
    $acl = Get-Acl $writable
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $accountSid, "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")))
    Set-Acl $writable $acl
}

# ---- 7. Schema — an explicit, logged deployment step (§16) -------------------------

Write-Host "Applying database migrations..." -ForegroundColor Cyan
$migrationLog = Join-Path $folders.logs ("migration-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

# $ErrorActionPreference is Stop for this script, and under Stop a native
# program's STDERR arriving through `2>&1` becomes a TERMINATING error — the
# first diagnostic line the migrator writes would abort the install before its
# exit code is ever examined. The exit code is the verdict; the text is a log.
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    & (Join-Path $source "migrator\BarcodePrinter.DbMigrator.exe") $connectionString 2>&1 |
        Tee-Object -FilePath $migrationLog
    $migrationExit = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousPreference
}
if ($migrationExit -ne 0) {
    throw "Migration failed (exit $migrationExit). The service was NOT started. See $migrationLog"
}

# ---- 8. Service --------------------------------------------------------------------

$credential = New-Object System.Management.Automation.PSCredential(
    ".\$ServiceAccount", $ServiceAccountPassword)
$exe = Join-Path $folders.api "BarcodePrinter.Api.exe"

if ($existing) {
    Write-Host "Updating the $ServiceName service..." -ForegroundColor Cyan
    & sc.exe config $ServiceName binPath= "`"$exe`"" obj= ".\$ServiceAccount" `
        password= (ConvertFrom-SecureStringPlain $ServiceAccountPassword) | Out-Null
} else {
    Write-Host "Registering the $ServiceName service..." -ForegroundColor Cyan
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
        -DisplayName "Barcode Label Printing API" `
        -Description "Serves the Barcode Label Printing desktop clients and dispatches network print jobs." `
        -StartupType Automatic -Credential $credential | Out-Null
}

# Start after MySQL. Without the dependency the API wins the race at boot on a
# fast machine, fails to connect, and is only rescued by the restart actions
# below — which works, but logs a failed start every single morning.
if (-not $PSBoundParameters.ContainsKey('MySqlServiceName')) {
    $MySqlServiceName = if ($MySqlHost -in @("127.0.0.1", "localhost", "::1", $env:COMPUTERNAME)) {
        Get-Service -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^(MySQL|MySQL\d+)$' } |
            Select-Object -ExpandProperty Name -First 1
    } else { "" }
}
if ($MySqlServiceName) {
    Write-Host "Making $ServiceName depend on the $MySqlServiceName service..." -ForegroundColor Cyan
    & sc.exe config $ServiceName depend= $MySqlServiceName | Out-Null
} else {
    Write-Host "No local MySQL service dependency configured." -ForegroundColor DarkGray
}

# Delayed start: at boot MySQL needs a moment to finish crash recovery before it
# accepts connections, and "Running" on the MySQL service does not mean "ready".
& sc.exe config $ServiceName start= delayed-auto | Out-Null

# Restart on failure; reset the counter daily so a slow leak still gets noticed.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

[Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production", "Machine")

# ---- 9. Firewall — the API port, and nothing else ----------------------------------

Write-Host "Configuring the firewall..." -ForegroundColor Cyan
Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
New-NetFirewallRule -DisplayName "Barcode Printer API" -Direction Inbound `
    -Protocol TCP -LocalPort $HttpsPort -RemoteAddress $LanSubnet -Action Allow `
    -Profile Domain, Private | Out-Null

# Deliberate: no rule for 3306. MySQL binds to 127.0.0.1 and the API is its only
# client, so the database is not reachable from the LAN at all.
$mysqlExposed = Get-NetFirewallPortFilter -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalPort -eq 3306 -and $_.Protocol -eq 'TCP' } |
    Get-NetFirewallRule -ErrorAction SilentlyContinue |
    Where-Object { $_.Enabled -eq 'True' -and $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' }
if ($mysqlExposed) {
    Write-Warning "An inbound rule for port 3306 exists. MySQL must NOT be reachable from the LAN — remove it."
}

# ---- 10. Start and verify ----------------------------------------------------------

Write-Host "Starting $ServiceName..." -ForegroundColor Cyan

# Retried, because the service control manager can transiently refuse a start
# right after an upgrade or repair has touched the binaries — the Restart
# Manager session or a pending control from the stop above has not fully
# released the service yet. One refusal is not a broken installation; a refusal
# that survives half a minute of retries is, and gets reported with the state
# the SCM is actually in rather than a bare "cannot start".
$started = $false
foreach ($attempt in 1..6) {
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        $started = $true
        break
    } catch {
        if ($attempt -eq 6) {
            $state = (& sc.exe query $ServiceName | Out-String).Trim()
            throw "Could not start $ServiceName after $attempt attempts: $($_.Exception.Message)`nService state:`n$state"
        }
        Write-Host "  start refused (attempt $attempt), retrying..." -ForegroundColor Yellow
        Start-Sleep -Seconds 5
    }
}
(Get-Service $ServiceName).WaitForStatus("Running", "00:01:00")

# The probe must accept the just-generated certificate before anything has
# trusted it, and it must do so on BOTH shells this script runs under.
# Invoke-WebRequest -SkipCertificateCheck exists only in PowerShell 7; under
# Windows PowerShell 5.1 — which is what the installer's custom action host
# runs — that parameter is a binding error, the catch below swallows it, and a
# perfectly healthy service "fails" its health check thirty times in a row.
# HttpWebRequest with a per-request validation callback works identically on
# both, without touching the process-wide ServicePointManager callback.
function Test-HealthEndpoint([string]$Url) {
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Timeout = 5000
    $request.ServerCertificateValidationCallback = { $true }
    try {
        $response = $request.GetResponse()
        try { return ([int]$response.StatusCode -eq 200) } finally { $response.Close() }
    } catch {
        return $false
    }
}

$healthy = $false
foreach ($attempt in 1..30) {
    if (Test-HealthEndpoint "https://localhost:$HttpsPort/health") { $healthy = $true; break }
    Start-Sleep -Seconds 2
}

if (-not $healthy) {
    # "Running" only means the process launched. Surface the reason instead of a
    # bare timeout: a certificate or connection-string fault is in the log.
    $lastLog = Get-ChildItem $folders.logs -Filter "api-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($lastLog) {
        Write-Host "--- last 20 log lines -------------------------------" -ForegroundColor Yellow
        Get-Content $lastLog.FullName -Tail 20 | Write-Host
        Write-Host "-----------------------------------------------------" -ForegroundColor Yellow
    }
    throw "The service started but /health did not report healthy. See $($folders.logs)."
}

Write-Host ""
Write-Host "Installed. https://$($env:COMPUTERNAME):$HttpsPort is healthy." -ForegroundColor Green
Write-Host "Next:"
Write-Host "  1. .\Register-BackupTasks.ps1   (nightly dump, hourly binlogs, image mirror)"
Write-Host "  2. .\Test-Recovery.ps1          (rehearse the restore BEFORE go-live)"
Write-Host "  3. Copy $publicCertPath to each workstation, then:"
Write-Host "     .\Install-Client.ps1 -ApiBaseUrl https://$($env:COMPUTERNAME):$HttpsPort -CertificateFile .\barcodeprinter-lan.cer"
