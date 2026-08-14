<#
.SYNOPSIS
    Turns a laid-down installation directory into a working Barcode Label
    Printing system: database, schema, API service, HTTPS, client configuration.

.DESCRIPTION
    This is the single entry point the installer calls. It orchestrates the
    existing deployment scripts rather than reimplementing them:

        Install-MySql.ps1     provisions/validates the database engine
        Install-Server.ps1    service account, certificate, ACLs, migrations,
                              Windows service, firewall
        Install-Client.ps1    client.json, certificate trust, shortcuts

    It runs unattended: every credential it needs is generated here, never
    prompted for and never shipped in the package.

    Failure handling: each phase is announced before it runs and its outcome is
    recorded. If a phase throws, the API service is stopped so the machine is
    not left with a service flapping against a half-built database, the failure
    is written to the log with the phase name, and a non-zero exit code is
    returned. The database is never dropped on failure — a half-migrated
    database is recoverable, a deleted one is not.

.EXAMPLE
    .\Install-BarcodePrinter.ps1 -InstallDir "C:\Program Files\Barcode Label Printing"
#>
[CmdletBinding()]
param(
    # Where the installer laid the payload down: api\, client\, migrator\,
    # mysql\ and these scripts.
    [Parameter(Mandatory)]
    [string]$InstallDir,

    # Mutable state: configuration, logs, images, imports, key ring, database.
    [string]$DataDir = "$env:ProgramData\BarcodePrinter",

    [int]$HttpsPort = 5001,

    # Single-machine deployment by default: the API is not exposed to the
    # network at all. -LanSubnet opens it for other workstations.
    [string]$LanSubnet,

    [string]$ServiceAccount = "BarcodePrinterSvc",
    [string]$MySqlZipName   = "mysql-8.4.0-winx64.zip"
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

$logDir  = Join-Path $DataDir "logs"
New-Item -ItemType Directory -Path $logDir -Force | Out-Null
$logPath = Join-Path $logDir ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

$script:phase = "startup"
function Write-Log([string]$Message, [string]$Colour = "Gray") {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    Add-Content -Path $logPath -Value $line
    Write-Host $Message -ForegroundColor $Colour
}
function Enter-Phase([string]$Name) {
    $script:phase = $Name
    Write-Log ""
    Write-Log "=== $Name ===" Cyan
}

Write-Log "Barcode Label Printing installation"
Write-Log "  payload : $InstallDir"
Write-Log "  data    : $DataDir"
Write-Log "  log     : $logPath"

try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required."
    }

    # -----------------------------------------------------------------------
    Enter-Phase "Checking prerequisites"

    foreach ($required in @("api\BarcodePrinter.Api.exe",
                            "client\BarcodePrinter.Wpf.exe",
                            "migrator\BarcodePrinter.DbMigrator.exe",
                            "Install-MySql.ps1", "Install-Server.ps1", "Install-Client.ps1")) {
        if (-not (Test-Path (Join-Path $InstallDir $required))) {
            throw "The installation is incomplete: '$required' is missing from $InstallDir."
        }
    }
    Write-Log "  payload complete." Green

    # The API and client are published self-contained, so no .NET runtime is
    # required. MySQL's binaries are not: they need the VC++ runtime.
    $vcRuntime = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64" -ErrorAction SilentlyContinue
    if ($vcRuntime) {
        Write-Log "  Visual C++ runtime $($vcRuntime.Version) present." Green
    } else {
        Write-Log "  Visual C++ x64 runtime not detected — MySQL may fail to start." Yellow
    }

    # -----------------------------------------------------------------------
    Enter-Phase "Database engine"

    $mysqlZip = Join-Path $InstallDir "mysql\$MySqlZipName"
    $mysql = & (Join-Path $InstallDir "Install-MySql.ps1") `
        -MySqlZip $(if (Test-Path $mysqlZip) { $mysqlZip } else { $null }) `
        -DataRoot (Join-Path $DataDir "mysql") `
        -InstallRoot (Join-Path $InstallDir "mysql-server") `
        -LogPath (Join-Path $logDir "mysql-setup.log")

    if (-not $mysql -or -not $mysql.Port) {
        throw "The database engine did not report a usable endpoint."
    }
    if (-not $mysql.AppPassword) {
        throw "MySQL is present but was not provisioned by this installer, so no application account exists. Create the '$($mysql.AppUser)' account and re-run, or remove the existing MySQL service."
    }
    Write-Log "  $($mysql.Host):$($mysql.Port), service '$($mysql.ServiceName)'." Green

    # -----------------------------------------------------------------------
    Enter-Phase "Application server"

    # Generated per machine, never shipped. The account cannot log on
    # interactively, so nobody ever types this.
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#%*+-_?'
    $bytes = [byte[]]::new(28)
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $servicePassword = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })

    $serverArgs = @{
        InstallRoot            = $DataDir
        ApiBinPath             = (Join-Path $InstallDir "api")
        ServiceAccount         = $ServiceAccount
        ServiceAccountPassword = (ConvertTo-SecureString $servicePassword -AsPlainText -Force)
        MySqlHost              = $mysql.Host
        MySqlPort              = $mysql.Port
        MySqlDatabase          = $mysql.Database
        MySqlUser              = $mysql.AppUser
        MySqlPassword          = (ConvertTo-SecureString $mysql.AppPassword -AsPlainText -Force)
        HttpsPort              = $HttpsPort
        GenerateSelfSignedCert = $true
        MySqlServiceName       = $mysql.ServiceName
    }
    if ($LanSubnet) { $serverArgs.LanSubnet = $LanSubnet }

    # Install-Server.ps1 resolves its payload from its own directory, so it has
    # to be invoked where the installer put it.
    & (Join-Path $InstallDir "Install-Server.ps1") @serverArgs

    # -----------------------------------------------------------------------
    Enter-Phase "Desktop client"

    $publicCert = Join-Path $DataDir "barcodeprinter-lan.cer"
    $apiUrl     = "https://localhost:$HttpsPort"

    & (Join-Path $InstallDir "Install-Client.ps1") `
        -ApiBaseUrl $apiUrl `
        -InstallPath (Join-Path $InstallDir "client") `
        -CertificateFile $(if (Test-Path $publicCert) { $publicCert } else { $null }) `
        -ConfigureOnly -NoUninstallEntry -NoShortcut -Silent

    Write-Log "  client configured against $apiUrl." Green

    # -----------------------------------------------------------------------
    Enter-Phase "Verifying the installation"

    $checks = & (Join-Path $InstallDir "Test-Installation.ps1") `
        -InstallDir $InstallDir -DataDir $DataDir -HttpsPort $HttpsPort `
        -MySqlServiceName $mysql.ServiceName

    $failed = @($checks | Where-Object { -not $_.Ok })
    foreach ($check in $checks) {
        Write-Log ("  [{0}] {1}{2}" -f $(if ($check.Ok) { "ok  " } else { "FAIL" }),
                                       $check.Name,
                                       $(if ($check.Detail) { " — $($check.Detail)" } else { "" })) `
                  $(if ($check.Ok) { "Green" } else { "Red" })
    }
    if ($failed) {
        throw ("Post-installation checks failed: " + (($failed.Name) -join ', '))
    }

    Write-Log ""
    Write-Log "Installation complete and verified." Green
    Write-Log "  API : $apiUrl"
    Write-Log "  Log : $logPath"
    exit 0

} catch {
    Write-Log ""
    Write-Log "INSTALLATION FAILED during: $script:phase" Red
    Write-Log $_.Exception.Message Red
    Add-Content -Path $logPath -Value ($_.ScriptStackTrace | Out-String)

    # Do not leave a service restarting against a database that is not ready.
    # It would fill the event log and mask the real failure.
    $api = Get-Service -Name "BarcodePrinter.Api" -ErrorAction SilentlyContinue
    if ($api -and $api.Status -eq "Running") {
        Write-Log "Stopping the API service so it does not restart against an incomplete installation." Yellow
        Stop-Service -Name "BarcodePrinter.Api" -Force -ErrorAction SilentlyContinue
    }

    Write-Log ""
    Write-Log "Nothing was deleted. The database and its contents are intact." Yellow
    Write-Log "Full log: $logPath" Yellow
    exit 1
}
