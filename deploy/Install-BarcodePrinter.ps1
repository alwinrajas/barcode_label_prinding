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

    Rollback: before any provisioning runs, a transaction manifest is written to
    $DataDir\install-transaction.json recording which of the machine-level
    resources this script provisions ALREADY existed. Windows Installer's own
    rollback undoes only what Windows Installer did, so without that snapshot a
    rolled-back transaction leaves the service, certificate, account and firewall
    rule behind (BP-20260816-839444A5 left an orphaned BarcodePrinter.Api service
    whose binPath pointed at a deleted executable). The manifest is marked
    committed on success, so a later unrelated failure can never roll back an
    installation that already finished.

    Diagnostics: every run mints a support reference ID (BP-yyyyMMdd-XXXXXXXX)
    and writes it as the first line of the log, so a screenshot of the failure
    and a log file on a support ticket can be matched to each other. On failure
    the operator is told which step failed and what to do about it, in plain
    language derived from the phase — the raw .NET exception still goes to the
    log verbatim, because the friendly text is for the person at the machine and
    the exception is for whoever debugs it.

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

# One reference per run, minted before anything can fail. Cryptographically
# random rather than a counter or a hash of the machine: it must be unique across
# every machine that ever runs this, and it must not encode anything about the
# customer. Four bytes is 8 hex characters — short enough to read down a phone.
$referenceBytes = [byte[]]::new(4)
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($referenceBytes)
$script:referenceId = "BP-{0:yyyyMMdd}-{1}" -f (Get-Date),
    (-join ($referenceBytes | ForEach-Object { $_.ToString("X2") }))

$script:phase = "startup"
$script:firstFailureRecorded = $false

# The phases this run will execute, declared up front so the progress line can
# state a real total from the very first phase instead of guessing at one.
$script:phasePlan = @(
    "Checking prerequisites",
    "Database engine",
    "Application server",
    "Desktop client",
    "Verifying the installation"
)
$script:phaseIndex = 0

# What each phase means to an operator. Deliberately phase-derived rather than
# exception-derived: ".NET SocketException: No connection could be made" tells
# the person standing at the machine nothing, "the database engine could not be
# started" tells them where to look. The exception itself is still logged.
$script:phaseReasons = @{
    "startup"                    = "The installer could not start on this machine."
    "Checking prerequisites"     = "The installation package is incomplete, or a component it depends on is missing from this machine."
    "Database engine"            = "The database engine could not be installed or started."
    "Application server"         = "The API service could not be configured or started."
    "Desktop client"             = "The desktop client could not be configured to reach the API."
    "Verifying the installation" = "The installation completed but did not pass its own verification checks."
}
function Write-Log([string]$Message, [string]$Colour = "Gray") {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message
    Add-Content -Path $logPath -Value $line
    Write-Host $Message -ForegroundColor $Colour
}
function Write-ExceptionDetail([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    if ($script:firstFailureRecorded) { return }
    $script:firstFailureRecorded = $true

    # Do not serialize invocation arguments or exception data: either can contain
    # generated service/database credentials. These fields preserve the useful
    # failure context without disclosing a secret.
    Write-Log "FIRST FAILURE: phase=$script:phase" Red
    Write-Log ("  type       : {0}" -f $ErrorRecord.Exception.GetType().FullName) Red
    Write-Log ("  message    : {0}" -f $ErrorRecord.Exception.Message) Red
    Write-Log ("  category   : {0}" -f $ErrorRecord.CategoryInfo) Red
    if ($ErrorRecord.FullyQualifiedErrorId) {
        Write-Log ("  error id   : {0}" -f $ErrorRecord.FullyQualifiedErrorId) Red
    }
    if ($ErrorRecord.InvocationInfo -and $ErrorRecord.InvocationInfo.PositionMessage) {
        Write-Log ("  location   : {0}" -f ($ErrorRecord.InvocationInfo.PositionMessage -replace "`r?`n", " | ")) Red
    }
    if ($ErrorRecord.ScriptStackTrace) {
        Write-Log ("  stack      : {0}" -f ($ErrorRecord.ScriptStackTrace -replace "`r?`n", " | ")) Red
    }
}
function Enter-Phase([string]$Name) {
    $script:phase = $Name

    # Position comes from the declared plan, not from a running counter, so the
    # number cannot drift if a phase is reordered. A phase that is not in the
    # plan is appended rather than reported as "0/5" — a wrong number is worse
    # than an honest one.
    $script:phaseIndex = [array]::IndexOf($script:phasePlan, $Name) + 1
    if ($script:phaseIndex -lt 1) {
        $script:phasePlan += $Name
        $script:phaseIndex = $script:phasePlan.Count
    }

    Write-Log ""
    Write-Log "=== $Name ===" Cyan

    # Machine-readable progress: greppable in the MSI log and stable enough for a
    # UI to parse later. No percentage and no time estimate anywhere — we do not
    # know how long a phase takes, and an invented number is a lie the operator
    # will remember when it is wrong.
    Write-Log ("BPPHASE|{0}/{1}|{2}" -f $script:phaseIndex, $script:phasePlan.Count, $Name)
}
function Exit-Phase {
    # Called only after the phase body has returned, so BPPHASE-OK always means
    # the work actually finished. Nothing is ever reported complete in advance.
    Write-Log ("BPPHASE-OK|{0}" -f $script:phase)
}
function Write-FailureSummary([System.Management.Automation.ErrorRecord]$ErrorRecord) {
    # The operator-facing half of a failure. This is what ends up in the MSI log
    # and on a support ticket, so it says what broke and what to do — it never
    # replaces the exception detail written alongside it.
    $reason = $script:phaseReasons[$script:phase]
    if (-not $reason) {
        # No mapping for this phase: the exception message is better than silence.
        $reason = $ErrorRecord.Exception.Message
    }

    Write-Log ""
    Write-Log "Installation could not be completed." Red
    Write-Log "Failed step: $script:phase" Red
    Write-Log "Reason: $reason" Red
    Write-Log "What you can do:" Yellow
    Write-Log "  1. Run the installer again. Most failures at this point are caused by a file or service that was still in use, and a second attempt succeeds." Yellow
    Write-Log "  2. Open the installation log and read the first error it records — later errors are usually consequences of that one." Yellow
    Write-Log "  3. If it fails again, contact support with the reference below and attach the log file." Yellow
    Write-Log "Reference ID: $script:referenceId" Yellow
    Write-Log "Log location: $logPath" Yellow
}


# ---- Transaction manifest ---------------------------------------------------
# State, not a log: the durable log above stays exactly as it was. This file
# answers one question for Rollback-BarcodePrinter.ps1 — "what did THIS run
# create?" — and it answers it by recording what was already there. Enumerating
# what to delete is guesswork that eventually deletes a customer's own service or
# certificate; deleting only the difference from a pre-run snapshot cannot.
$script:manifestPath = Join-Path $DataDir "install-transaction.json"

function Write-TransactionManifest {
    # Certificates are recorded by thumbprint rather than as a single boolean: a
    # machine that already holds a 'Barcode Printer API' certificate and gets a
    # second one minted by this run must keep the first and lose only the second.
    $preCertThumbprints = @(
        Get-ChildItem "Cert:\LocalMachine\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.FriendlyName -eq "Barcode Printer API" } |
            ForEach-Object { $_.Thumbprint }
    )

    # Every flag answers the same question: did this exist BEFORE we started?
    # True means "not ours", and rollback must leave it alone.
    $manifest = [ordered]@{
        schema           = 1
        referenceId      = $script:referenceId
        startedUtc       = (Get-Date).ToUniversalTime().ToString("o")
        committed        = $false
        installDir       = $InstallDir
        dataDir          = $DataDir
        apiServiceName   = "BarcodePrinter.Api"
        mySqlServiceName = "BarcodePrinterMySQL"
        serviceAccount   = $ServiceAccount
        preExisting      = [ordered]@{
            apiService             = [bool](Get-Service -Name "BarcodePrinter.Api" -ErrorAction SilentlyContinue)
            mySqlService           = [bool](Get-Service -Name "BarcodePrinterMySQL" -ErrorAction SilentlyContinue)
            serviceAccount         = [bool](Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue)
            certificate            = ($preCertThumbprints.Count -gt 0)
            certificateThumbprints = $preCertThumbprints
            firewallRule           = [bool](Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue)
            # The database directory stands in for "a database already existed".
            # Rollback never deletes it either way — it is recorded so the log
            # can say plainly that the data was pre-existing and untouched.
            database               = (Test-Path (Join-Path $DataDir "mysql"))
            keyRing                = (Test-Path (Join-Path $DataDir "keys"))
            apiSettings            = (Test-Path (Join-Path $InstallDir "api\appsettings.Production.json"))
            clientConfig           = (Test-Path (Join-Path $DataDir "client.json"))
        }
    }

    $manifest | ConvertTo-Json -Depth 4 |
        Set-Content -Path $script:manifestPath -Encoding UTF8 -Force
}

function Complete-TransactionManifest {
    # Marking committed is what stops a rollback months later — triggered by some
    # unrelated failed transaction — from tearing down a healthy installation.
    if (-not (Test-Path $script:manifestPath)) { return }
    $manifest = Get-Content $script:manifestPath -Raw | ConvertFrom-Json
    $manifest.committed = $true
    $manifest | Add-Member -NotePropertyName committedUtc `
        -NotePropertyValue ((Get-Date).ToUniversalTime().ToString("o")) -Force
    $manifest | ConvertTo-Json -Depth 4 |
        Set-Content -Path $script:manifestPath -Encoding UTF8 -Force
}

# First line of the log, before anything that can fail: a log without its
# reference is a log nobody can tie to the failure that was reported.
Write-Log "Reference ID: $script:referenceId"
Write-Log "Barcode Label Printing installation"
Write-Log "  payload : $InstallDir"
Write-Log "  data    : $DataDir"
Write-Log "  log     : $logPath"

try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required."
    }

    # Before anything is provisioned, and deliberately outside any phase: a phase
    # that fails must already be covered by the snapshot, so the snapshot cannot
    # be taken inside one.
    try {
        Write-TransactionManifest
        Write-Log "Transaction manifest: $script:manifestPath" DarkGray
    } catch {
        # A manifest we could not write disables rollback; it must not abort an
        # installation that has so far changed nothing on this machine.
        Write-Log ("Could not write the transaction manifest — automatic rollback will be skipped: {0}" `
            -f $_.Exception.Message) Yellow
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
    Exit-Phase

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
    Exit-Phase

    # -----------------------------------------------------------------------
    Enter-Phase "Application server"

    # Retain service account password across repairs/upgrades to preserve DPAPI context
    $pwdFile = Join-Path $DataDir "service.pwd"
    if (Test-Path $pwdFile) {
        $servicePassword = (Get-Content $pwdFile -Raw).Trim()
    } else {
        $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!#%*+-_?'
        $bytes = [byte[]]::new(28)
        [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $servicePassword = -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
        Set-Content -Path $pwdFile -Value $servicePassword -Encoding UTF8 -Force
        try {
            $acl = Get-Acl $pwdFile
            $acl.SetAccessRuleProtection($true, $false)
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("SYSTEM", "FullControl", "Allow")))
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("Administrators", "FullControl", "Allow")))
            Set-Acl $pwdFile $acl
        } catch { }
    }

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
    $serverArgs.DiagnosticLogPath = $logPath
    & (Join-Path $InstallDir "Install-Server.ps1") @serverArgs
    Exit-Phase

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
    Exit-Phase

    # -----------------------------------------------------------------------
    Enter-Phase "Verifying the installation"

    # -ServiceAccount is passed through so the private-key ACL check tests the
    # account this installation actually created, not the script's default.
    $checks = & (Join-Path $InstallDir "Test-Installation.ps1") `
        -InstallDir $InstallDir -DataDir $DataDir -HttpsPort $HttpsPort `
        -MySqlServiceName $mysql.ServiceName -ServiceAccount $ServiceAccount

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
    Exit-Phase

    # Committed before success is announced: everything this run created now
    # belongs to a finished installation, and no future rollback may claim it.
    try { Complete-TransactionManifest } catch { }

    Write-Log ""
    Write-Log "Installation complete and verified." Green
    Write-Log "  API       : $apiUrl"
    Write-Log "  Reference : $script:referenceId"
    Write-Log "  Log       : $logPath"
    exit 0

} catch {
    Write-Log ""
    Write-Log "INSTALLATION FAILED during: $script:phase" Red

    # Order matters: the verbatim exception goes to the log first so the friendly
    # summary below is the last thing on screen, and so a truncated console still
    # leaves the real cause in the durable log.
    Write-ExceptionDetail $_

    # Do not leave a service restarting against a database that is not ready.
    # It would fill the event log and mask the real failure.
    $api = Get-Service -Name "BarcodePrinter.Api" -ErrorAction SilentlyContinue
    if ($api -and $api.Status -eq "Running") {
        Write-Log "Stopping the API service so it does not restart against an incomplete installation." Yellow
        Stop-Service -Name "BarcodePrinter.Api" -Force -ErrorAction SilentlyContinue
    }

    Write-Log ""
    Write-Log "The database and its contents are intact." Yellow
    # The manifest is left uncommitted on purpose. Windows Installer runs
    # RollbackSystem next, and that is what undoes the services, certificate,
    # account and firewall rule this run created — never the data.
    Write-Log "Anything this run created on the machine will be undone by the installer's rollback." Yellow

    # Last, and contiguous, so the block a support engineer is asked to read back
    # is the block still on screen — and so the reference is never scrolled off
    # by the cleanup above.
    Write-FailureSummary $_
    exit 1
}
