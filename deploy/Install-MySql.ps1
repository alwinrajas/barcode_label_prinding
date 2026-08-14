<#
.SYNOPSIS
    Provisions the MySQL server the application runs on, without Docker and
    without the MySQL Installer.

.DESCRIPTION
    Single-machine deployments cannot assume MySQL is present, and cannot assume
    the administrator will configure it by hand. This script gets a machine from
    "nothing" to "a MySQL 8 service with the settings this application requires,
    an application database, and a least-privilege account for it".

    It is idempotent. Re-running it against a provisioned machine verifies and
    repairs; it never re-initialises a data directory that already holds data.

    Design decisions worth knowing:

      * The server is provisioned from the MySQL *noinstall* zip rather than the
        MySQL Installer MSI. This application will not run correctly on default
        settings — it needs ngram_token_size=2 (baked into the FULLTEXT index at
        creation), local_infile=1 (the bulk import) and READ-COMMITTED (carton
        allocation deadlocks otherwise). Authoring my.ini before the server is
        ever started is the only way to guarantee those hold from the first byte
        written; the MSI route means installing on defaults, editing, restarting,
        and hoping nothing was created in between.

      * Our service is named BarcodePrinterMySQL, NOT MySQL. A service called
        "MySQL" very often already exists and is XAMPP's MariaDB. Taking that
        name would collide, and trusting it would be worse.

      * Root credentials are generated here, never shipped, and never placed in
        application configuration. The application authenticates as a restricted
        account scoped to its own schema. Root is written to an ACL'd option
        file for backup/maintenance use only.

.EXAMPLE
    .\Install-MySql.ps1 -MySqlZip .\mysql\mysql-8.4.0-winx64.zip
#>
[CmdletBinding()]
param(
    # The noinstall archive to provision from. Not needed when a usable MySQL 8
    # is already present.
    [string]$MySqlZip,

    [string]$InstallRoot = "$env:ProgramFiles\Barcode Label Printing\MySQL",
    [string]$DataRoot    = "$env:ProgramData\BarcodePrinter\mysql",
    [string]$ServiceName = "BarcodePrinterMySQL",

    [string]$Database    = "barcodeprinter",
    [string]$AppUser     = "barcodeprinter",

    # Preferred port. If it is taken, the next free port is used and reported —
    # a machine already running MySQL or XAMPP on 3306 is normal.
    [int]$PreferredPort  = 3306,

    # Use a MySQL 8 that is already installed, if one is found. Pass -Force to
    # provision our own regardless.
    [switch]$Force,

    [string]$LogPath = "$env:ProgramData\BarcodePrinter\logs\mysql-setup.log"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Path (Split-Path $LogPath) -Force | Out-Null
function Write-Step([string]$Message, [string]$Colour = "Cyan") {
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message
    Add-Content -Path $LogPath -Value $line
    Write-Host $Message -ForegroundColor $Colour
}

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell session."
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

<#
    Runs a native executable, captures everything it says into the log, and
    returns its exit code.

    This exists because of a PowerShell trap that is easy to walk into and hard
    to read afterwards: with $ErrorActionPreference = 'Stop', piping a native
    program's output through `2>&1` turns every line it wrote to STDERR into a
    terminating error. mysqld writes its ordinary progress messages to stderr —
    "MySQL Server Initialization - start." is not a failure — so the very first
    thing a healthy server says would abort the installation, and the error
    reported to the user is that innocent sentence.

    The exit code is the only thing that decides success here. Callers check it.
#>
function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string[]]$Arguments = @(),
        [string]$StandardInput,
        [string]$Label = "output"
    )
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if ($PSBoundParameters.ContainsKey('StandardInput')) {
            $output = $StandardInput | & $Exe @Arguments 2>&1
        } else {
            $output = & $Exe @Arguments 2>&1
        }
        $exitCode = $LASTEXITCODE
        foreach ($line in $output) {
            Add-Content -Path $LogPath -Value ("    {0}: {1}" -f $Label, $line)
        }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = ($output | Out-String) }
    } finally {
        $ErrorActionPreference = $previous
    }
}

function New-StrongPassword([int]$Length = 28) {
    # Deliberately excludes characters that are special somewhere this password
    # travels: ; = ' " ` \ and whitespace (connection strings, SQL, option
    # files) — and #, because in a MySQL option file # starts a comment EVEN IN
    # MID-LINE, so `password=abc#def` silently reads back as `abc`. ALTER USER
    # sets the full value, the option file returns a truncated one, and the
    # installer fails with "Access denied for root" only on the runs where the
    # random draw happened to include a #.
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!%*+-_?'
    $bytes = [byte[]]::new($Length)
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $alphabet[$_ % $alphabet.Length] })
}

function Test-PortFree([int]$Port) {
    -not (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
}

function Get-FreePort([int]$Preferred) {
    foreach ($candidate in @($Preferred) + (3307..3320)) {
        if (Test-PortFree $candidate) { return $candidate }
    }
    throw "No free TCP port found for MySQL between $Preferred and 3320."
}

<#
    Reports what a mysqld binary actually is. The service NAME is not evidence:
    XAMPP and WAMP both register MariaDB as a service called "MySQL", and
    MariaDB cannot run this schema (no utf8mb4_0900_ai_ci, no ngram FULLTEXT, no
    MySQL-8 partitioning). Ask the binary, and believe it.
#>
function Get-MysqldFlavour([string]$MysqldPath) {
    if (-not (Test-Path $MysqldPath)) { return $null }
    try {
        $banner = & $MysqldPath --version 2>&1 | Out-String
    } catch {
        return $null
    }
    $version = if ($banner -match 'Ver\s+(\d+\.\d+\.\d+)') { $matches[1] } else { $null }
    [pscustomobject]@{
        Path      = $MysqldPath
        Banner    = $banner.Trim()
        IsMariaDb = $banner -match 'MariaDB'
        Version   = if ($version) { [version]$version } else { $null }
    }
}

function Find-UsableMySql {
    foreach ($service in Get-CimInstance Win32_Service) {
        if ($service.PathName -notmatch 'mysqld') { continue }

        # PathName is "C:\path\mysqld.exe" MySQL — arguments and quoting vary.
        $exe = if ($service.PathName -match '^\s*"([^"]+)"') { $matches[1] }
               else { ($service.PathName -split '\s+')[0] }
        if ($exe -notmatch '\.exe$') { $exe = "$exe.exe" }

        $flavour = Get-MysqldFlavour $exe
        if (-not $flavour) { continue }

        $verdict = if ($flavour.IsMariaDb) { "MariaDB — unusable for this schema" }
                   elseif (-not $flavour.Version) { "version not recognised" }
                   elseif ($flavour.Version.Major -lt 8) { "MySQL $($flavour.Version) — 8.0+ required" }
                   else { "usable" }

        Write-Step ("  service '{0}': {1} ({2})" -f $service.Name, $flavour.Banner, $verdict) DarkGray

        if ($verdict -eq "usable") {
            return [pscustomobject]@{
                ServiceName = $service.Name
                Exe         = $exe
                Version     = $flavour.Version
                State       = $service.State
            }
        }
    }
    return $null
}

# ---------------------------------------------------------------------------
# 1. Is a usable MySQL already here?
# ---------------------------------------------------------------------------

Write-Step "Looking for an existing MySQL server..."
$existing = if ($Force) { $null } else { Find-UsableMySql }

$ourService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$binDir     = Join-Path $InstallRoot "bin"
$dataDir    = Join-Path $DataRoot "data"
$iniPath    = Join-Path $DataRoot "my.ini"
$rootCnf    = Join-Path $DataRoot "root.cnf"

if ($existing -and -not $ourService) {
    # Someone else's MySQL 8. We can use it, but we did not configure it and we
    # must not silently rewrite another product's my.ini.
    Write-Step "Found usable MySQL $($existing.Version) as service '$($existing.ServiceName)'." Green
    Write-Step "Using it. Its settings are verified by the migrator's preflight before any schema is applied." DarkGray
    $useExisting = $true
} else {
    $useExisting = $false
}

# ---------------------------------------------------------------------------
# 2. Provision our own server when needed
# ---------------------------------------------------------------------------

if (-not $useExisting) {

    $alreadyInitialised = Test-Path (Join-Path $dataDir "mysql")

    if (-not (Test-Path (Join-Path $binDir "mysqld.exe"))) {
        if (-not $MySqlZip -or -not (Test-Path $MySqlZip)) {
            throw "No usable MySQL was found and -MySqlZip was not supplied. The installer bundles this archive; run the installer rather than this script directly."
        }
        Write-Step "Extracting MySQL from $(Split-Path $MySqlZip -Leaf)..."
        New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null
        $staging = Join-Path $env:TEMP "bp-mysql-extract"
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive -Path $MySqlZip -DestinationPath $staging -Force

        # The archive contains a single mysql-<version>-winx64\ root; lift its
        # contents so bin\ sits directly under InstallRoot.
        $root = Get-ChildItem $staging -Directory | Select-Object -First 1
        Copy-Item (Join-Path $root.FullName "*") $InstallRoot -Recurse -Force
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
        Write-Step "  extracted to $InstallRoot" Green
    } else {
        Write-Step "MySQL binaries already present at $InstallRoot." DarkGray
    }

    $mysqld  = Join-Path $binDir "mysqld.exe"
    $mysql   = Join-Path $binDir "mysql.exe"
    $admin   = Join-Path $binDir "mysqladmin.exe"

    $flavour = Get-MysqldFlavour $mysqld
    if (-not $flavour -or $flavour.IsMariaDb -or $flavour.Version.Major -lt 8) {
        throw "The bundled archive is not MySQL 8+: $($flavour.Banner)"
    }
    Write-Step "Provisioning $($flavour.Banner)"

    # ---- Port -------------------------------------------------------------
    # Reuse the port already recorded for our own service; only pick a new one
    # on a first install, so an upgrade never moves the port out from under an
    # installed client.
    if ($alreadyInitialised -and (Test-Path $iniPath) -and
        ((Get-Content $iniPath -Raw) -match '(?m)^\s*port\s*=\s*(\d+)')) {
        $port = [int]$matches[1]
        Write-Step "Keeping the existing port $port." DarkGray
    } else {
        $port = Get-FreePort $PreferredPort
        if ($port -ne $PreferredPort) {
            Write-Step "Port $PreferredPort is in use; MySQL will listen on $port." Yellow
        }
    }

    # ---- my.ini -----------------------------------------------------------
    # Written before first start. Every setting below is load-bearing and the
    # reasons are in the runbook; the migrator refuses to run if they are wrong.
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    $logDir = Join-Path $DataRoot "log"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null

    $iniContent = @"
# Generated by the Barcode Label Printing installer. Managed file — the
# installer rewrites the [mysqld] block it owns on upgrade.
[mysqld]
basedir                 = $InstallRoot
datadir                 = $dataDir
port                    = $port

# Loopback only. The API is this server's only client and it runs on this
# machine; nothing on the network has any business reaching the database.
bind-address            = 127.0.0.1

character-set-server    = utf8mb4
collation-server        = utf8mb4_0900_ai_ci

# Required by the application, not preferences:
#   READ-COMMITTED  concurrent carton-number allocation deadlocks under
#                   REPEATABLE-READ.
#   local_infile    the bulk product import runs on LOAD DATA LOCAL INFILE.
#   ngram_token_size is baked into the FULLTEXT index when it is built; changing
#                   it later silently breaks mid-code product search.
transaction-isolation   = READ-COMMITTED
local_infile            = 1
ngram_token_size        = 2

log-error               = $logDir\mysql-error.log

# Binary log, for point-in-time recovery between nightly dumps.
#
# binlog_expire_logs_seconds, NOT expire_logs_days: the latter was deprecated in
# MySQL 8.0 and REMOVED in 8.4, and an unknown variable is fatal — mysqld refuses
# to start, --initialize aborts, and the error names only the variable, not the
# fact that it no longer exists. 1209600 = 14 days.
#
# binlog_format is deliberately absent: ROW has been the default since 8.0 and
# the setting itself is deprecated in 8.4, so naming it only produces a warning
# on every start.
log-bin                    = $logDir\mysql-bin
binlog_expire_logs_seconds = 1209600

max_connections         = 151
innodb_buffer_pool_size = 1G
innodb_flush_log_at_trx_commit = 1

[client]
port                    = $port
"@
    Set-Content -Path $iniPath -Value $iniContent -Encoding ASCII
    Write-Step "Wrote $iniPath (port $port)."

    # Ask the server whether it accepts this configuration before asking it to
    # do anything with it. An unknown or removed variable is fatal to mysqld,
    # and discovering that during --initialize leaves a half-written data
    # directory behind and reports "the designated data directory is unusable"
    # — which sends you looking at permissions and disk space rather than at the
    # one line in my.ini that this build of MySQL no longer recognises.
    $validation = Invoke-Native -Exe $mysqld -Label "validate" -Arguments @(
        "--defaults-file=$iniPath", "--validate-config")
    if ($validation.ExitCode -ne 0) {
        throw ("MySQL rejected the generated configuration:`n" + $validation.Output.Trim() +
               "`nThe file is at $iniPath.")
    }
    Write-Step "  configuration accepted by MySQL $($flavour.Version)." Green

    # ---- Initialise the data directory ------------------------------------
    $rootPassword = $null
    if ($alreadyInitialised -and -not (Test-Path $rootCnf)) {
        # A data directory with no root.cnf is either debris from an install
        # that died partway through, or a real server whose credentials have
        # been lost. Those need opposite treatment, and the application's own
        # database is what tells them apart: an interrupted initialise never got
        # far enough to create it.
        if (Test-Path (Join-Path $dataDir $Database)) {
            throw "The data directory at $dataDir contains the '$Database' database, but $rootCnf is missing, so its root password is unknown. Refusing to touch it. Restore root.cnf from backup, or move the data directory aside if you accept losing its contents."
        }
        Write-Step "Found an incomplete data directory from a previous failed attempt; starting it again." Yellow
        Remove-Item $dataDir -Recurse -Force
        $alreadyInitialised = $false
    }

    if ($alreadyInitialised) {
        Write-Step "Data directory already initialised — leaving it alone." DarkGray
    } else {
        Write-Step "Initialising the data directory (this takes a moment)..."
        New-Item -ItemType Directory -Path $dataDir -Force | Out-Null

        # --initialize-insecure creates root@localhost with no password. The
        # alternative writes a temporary password into the error log, which then
        # has to be scraped and lives on disk in a file we do not control. The
        # server is not started until the password below is set, and it is bound
        # to loopback when it is.
        $init = Invoke-Native -Exe $mysqld -Label "mysqld" -Arguments @(
            "--defaults-file=$iniPath", "--initialize-insecure", "--console")
        if ($init.ExitCode -ne 0) {
            throw "MySQL failed to initialise its data directory (exit $($init.ExitCode)). See $LogPath and $logDir\mysql-error.log"
        }
        $rootPassword = New-StrongPassword
        Write-Step "  initialised." Green
    }

    # ---- Service ----------------------------------------------------------
    if (-not $ourService) {
        Write-Step "Registering the $ServiceName service..."
        & $mysqld --install $ServiceName --defaults-file=$iniPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "mysqld --install failed (exit $LASTEXITCODE)." }
        & sc.exe config $ServiceName start= auto | Out-Null
        & sc.exe description $ServiceName "Database engine for Barcode Label Printing." | Out-Null
        & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    } else {
        Write-Step "Service $ServiceName already registered." DarkGray
        # Point it at the current binaries and ini in case this is an upgrade.
        & sc.exe config $ServiceName binPath= "`"$mysqld`" --defaults-file=`"$iniPath`" $ServiceName" start= auto | Out-Null
    }

    Write-Step "Starting $ServiceName..."
    Start-Service -Name $ServiceName
    (Get-Service $ServiceName).WaitForStatus("Running", "00:02:00")

    # "Running" is the service control manager's opinion. Ask the server.
    # Which credentials can ping depends on which run this is. Fresh install:
    # root has NO password until we secure it below, so the empty form is the
    # one that works. Repair or upgrade: the password was set long ago and lives
    # in root.cnf, so that is the one that works — and note the defaults file
    # must be the FIRST argument or the client rejects it. Every candidate is
    # tried on every attempt; pinging with only the wrong one (the original bug
    # here) waits out the whole loop and reports a healthy server as dead.
    $credentialForms = [System.Collections.Generic.List[string[]]]::new()
    if (Test-Path $rootCnf) {
        $credentialForms.Add(@("--defaults-extra-file=$rootCnf", "ping"))
    }
    $credentialForms.Add(@("--host=127.0.0.1", "--port=$port", "--user=root", "--password=", "ping"))

    $ready = $false
    foreach ($attempt in 1..60) {
        foreach ($form in $credentialForms) {
            $ping = Invoke-Native -Exe $admin -Label "mysqladmin" -Arguments $form
            if ($ping.Output -match 'mysqld is alive') { $ready = $true; break }
        }
        if ($ready) { break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) {
        throw "MySQL started but never became reachable on 127.0.0.1:$port. See $logDir\mysql-error.log"
    }
    Write-Step "  server is answering on 127.0.0.1:$port." Green

    # ---- Root password ----------------------------------------------------
    if ($rootPassword) {
        Write-Step "Securing the root account..."
        $secureSql = @"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$rootPassword';
DELETE FROM mysql.user WHERE User='';
DROP DATABASE IF EXISTS test;
FLUSH PRIVILEGES;
"@
        $secure = Invoke-Native -Exe $mysql -Label "mysql" -StandardInput $secureSql -Arguments @(
            "--host=127.0.0.1", "--port=$port", "--user=root", "--password=", "--batch")
        if ($secure.ExitCode -ne 0) {
            throw "Could not set the MySQL root password (exit $($secure.ExitCode)). See $LogPath"
        }

        # An option file, not a command line: process arguments are readable by
        # every user on the machine, so a password passed as an argument is a
        # password disclosed.
        # The password is quoted as well as generated from a safe alphabet —
        # belt and braces against option-file parsing (quoting is the documented
        # way to carry values containing # or whitespace).
        @"
[client]
user=root
password="$rootPassword"
host=127.0.0.1
port=$port
"@ | Set-Content -Path $rootCnf -Encoding ASCII

        $acl = Get-Acl $rootCnf
        $acl.SetAccessRuleProtection($true, $false)
        @($acl.Access) | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
        foreach ($id in @("BUILTIN\Administrators", "NT AUTHORITY\SYSTEM")) {
            $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
                ([System.Security.Principal.NTAccount]::new($id)), "FullControl", "Allow")))
        }
        Set-Acl $rootCnf $acl
        Write-Step "  root credentials written to $rootCnf (Administrators and SYSTEM only)." Green
    }

    $mysqlExe   = $mysql
    $mysqlPort  = $port
    $defaultsArg = "--defaults-extra-file=$rootCnf"

} else {
    # Using someone else's server: we have no root credentials for it, so the
    # database and account have to already exist, or an administrator has to
    # create them. Report precisely rather than failing vaguely later.
    $mysqlExe   = Join-Path (Split-Path $existing.Exe) "mysql.exe"
    $mysqlPort  = 3306
    $defaultsArg = $null
}

# ---------------------------------------------------------------------------
# 3. Database and least-privilege application account
# ---------------------------------------------------------------------------

if (-not $useExisting) {
    Write-Step "Creating the application database and account..."

    $appPasswordFile = Join-Path $DataRoot "app.pwd"
    if (Test-Path $appPasswordFile) {
        # An upgrade must not rotate this: appsettings.Production.json is kept
        # across upgrades and still holds the old password.
        $appPassword = (Get-Content $appPasswordFile -Raw).Trim()
        Write-Step "  reusing the existing application account password." DarkGray
    } else {
        $appPassword = New-StrongPassword
    }

    # CREATE ... IF NOT EXISTS throughout: this must be safe to run against a
    # live database during an upgrade. Nothing here drops anything.
    $sql = @"
CREATE DATABASE IF NOT EXISTS ``$Database`` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER IF NOT EXISTS '$AppUser'@'127.0.0.1' IDENTIFIED BY '$appPassword';
ALTER USER '$AppUser'@'127.0.0.1' IDENTIFIED BY '$appPassword';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP, REFERENCES
  ON ``$Database``.* TO '$AppUser'@'127.0.0.1';
FLUSH PRIVILEGES;
"@
    $create = Invoke-Native -Exe $mysqlExe -Label "mysql" -StandardInput $sql -Arguments @($defaultsArg, "--batch")
    if ($create.ExitCode -ne 0) {
        throw "Could not create the application database or account (exit $($create.ExitCode)). See $LogPath"
    }

    Set-Content -Path $appPasswordFile -Value $appPassword -Encoding ASCII -NoNewline
    $acl = Get-Acl $appPasswordFile
    $acl.SetAccessRuleProtection($true, $false)
    @($acl.Access) | ForEach-Object { [void]$acl.RemoveAccessRule($_) }
    foreach ($id in @("BUILTIN\Administrators", "NT AUTHORITY\SYSTEM")) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            ([System.Security.Principal.NTAccount]::new($id)), "FullControl", "Allow")))
    }
    Set-Acl $appPasswordFile $acl

    Write-Step "  database '$Database' and account '$AppUser'@'127.0.0.1' ready." Green
}

# ---------------------------------------------------------------------------
# 4. Report what the caller needs
# ---------------------------------------------------------------------------

$result = [pscustomobject]@{
    ServiceName  = if ($useExisting) { $existing.ServiceName } else { $ServiceName }
    Provisioned  = -not $useExisting
    Host         = "127.0.0.1"
    Port         = $mysqlPort
    Database     = $Database
    AppUser      = $AppUser
    AppPassword  = if ($useExisting) { $null } else { $appPassword }
    RootCnf      = if ($useExisting) { $null } else { $rootCnf }
    LogPath      = $LogPath
}

Write-Step ("MySQL ready: {0}:{1}, service '{2}'." -f $result.Host, $result.Port, $result.ServiceName) Green
$result
