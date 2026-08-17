<#
.SYNOPSIS
    Removes the configured parts of a Barcode Label Printing installation.
    Files are the installer's business; this undoes what the installer's scripts
    configured on the machine.

.DESCRIPTION
    Called by the installer on uninstall, before it removes the payload.

    The database is KEPT by default, and so are images, imports and the Data
    Protection key ring. Print history is a compliance record: an uninstall is
    an operational act, not permission to destroy it. -RemoveData is the only
    way to delete it, and it says so plainly.

    Everything here tolerates the component already being gone — an uninstall
    that fails because a service was removed by hand leaves the machine in a
    worse state than one that shrugs and continues.

.EXAMPLE
    .\Uninstall-BarcodePrinter.ps1
.EXAMPLE
    .\Uninstall-BarcodePrinter.ps1 -RemoveData     # also destroys the database
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = "High")]
param(
    # The installer's payload directory. Used to clean up the MySQL server tree
    # that Install-MySql.ps1 extracted into it — those files were created after
    # installation, so Windows Installer does not know about them and would
    # leave roughly 1.5 GB behind.
    [string]$InstallDir,

    [string]$DataDir = "$env:ProgramData\BarcodePrinter",
    [string]$ServiceName = "BarcodePrinter.Api",
    [string]$MySqlServiceName = "BarcodePrinterMySQL",
    [string]$ServiceAccount = "BarcodePrinterSvc",
    [int]$HttpsPort = 5001,

    # Deletes the database, images, imports and the key ring. Without this the
    # data survives and a later reinstall picks it back up.
    [switch]$RemoveData,

    # Keep the local account and its "log on as a service" right, for a
    # reinstall that is about to follow.
    [switch]$KeepServiceAccount
)

$ErrorActionPreference = "Continue"

$logDir = Join-Path $DataDir "logs"
New-Item -ItemType Directory -Path $logDir -Force -ErrorAction SilentlyContinue | Out-Null
$logPath = Join-Path $logDir ("uninstall-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
function Write-Log([string]$Message, [string]$Colour = "Gray") {
    Add-Content -Path $logPath -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message) -ErrorAction SilentlyContinue
    Write-Host $Message -ForegroundColor $Colour
}

Write-Log "Removing Barcode Label Printing configuration"

# ---- 1. Stop things, in dependency order -----------------------------------

foreach ($name in @($ServiceName, $MySqlServiceName)) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Log "  service $name not present." DarkGray; continue }
    if ($svc.Status -ne "Stopped") {
        Write-Log "  stopping $name..."
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        $svc.WaitForStatus("Stopped", "00:01:00")
    }
    & sc.exe delete $name | Out-Null
    Write-Log "  service $name removed." Green
}

# The WPF client may be open; the payload cannot be deleted while it is.
# Ask first, the same way the close button does, so the application gets to run
# its own shutdown. Only a client that ignores the request is terminated, and
# that is reported rather than done silently — the installer's own preflight
# normally closes it well before this point.
$client = Get-Process -Name "BarcodePrinter.Wpf" -ErrorAction SilentlyContinue
if ($client) {
    Write-Log "  asking the desktop client to close..."
    foreach ($process in $client) {
        try { $null = $process.CloseMainWindow() } catch { }
    }
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Process -Name "BarcodePrinter.Wpf" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }

    $stubborn = Get-Process -Name "BarcodePrinter.Wpf" -ErrorAction SilentlyContinue
    if ($stubborn) {
        Write-Log "  the client did not close within 20s; terminating it so the payload can be removed." Yellow
        $stubborn | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    } else {
        Write-Log "  desktop client closed." Green
    }
}

# ---- 2. Firewall -----------------------------------------------------------

Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule -ErrorAction SilentlyContinue
Write-Log "  firewall rule removed." Green

# ---- 3. Certificates -------------------------------------------------------
# Only the certificate this installer generated, identified by the friendly name
# it sets. A certificate issued by the customer's own CA is left alone: it may
# be in use by something else on this machine.

foreach ($store in @("Cert:\LocalMachine\My", "Cert:\LocalMachine\Root")) {
    $generated = Get-ChildItem $store -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -eq "Barcode Printer API" }
    foreach ($cert in $generated) {
        Remove-Item $cert.PSPath -Force -ErrorAction SilentlyContinue
        Write-Log "  removed certificate $($cert.Thumbprint) from $store." Green
    }
}

# Installs predating the Kestrel fix bound a certificate to http.sys. Kestrel
# never used it, but leaving it behind reserves the port.
$binding = & netsh http show sslcert ipport=0.0.0.0:$HttpsPort 2>$null
if ($LASTEXITCODE -eq 0 -and $binding -match 'Certificate Hash') {
    & netsh http delete sslcert ipport=0.0.0.0:$HttpsPort | Out-Null
    Write-Log "  removed the stale http.sys binding on port $HttpsPort." Green
}

# ---- 4. Machine-wide state -------------------------------------------------

if ([Environment]::GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Machine") -eq "Production") {
    [Environment]::SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", $null, "Machine")
    Write-Log "  machine-wide ASPNETCORE_ENVIRONMENT cleared." Green
}

$clientConfig = Join-Path $env:ProgramData "BarcodePrinter\client.json"
Remove-Item $clientConfig -Force -ErrorAction SilentlyContinue

# Everything under the install directory that Windows Installer does NOT track,
# because it was created after the files were laid down: the extracted MySQL
# tree, the generated appsettings (holds the connection string — it should not
# outlive the product), and whatever the API wrote beside itself at runtime.
# RemoveFiles only deletes what it tracks, so anything listed here would
# otherwise keep the whole directory pinned in Program Files forever.
if ($InstallDir) {
    foreach ($untracked in @("mysql-server", "api\appsettings.Production.json", "api\data")) {
        $path = Join-Path $InstallDir $untracked
        if (Test-Path $path) {
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
            Write-Log "  removed untracked: $untracked" Green
        }
    }
}

# ---- 5. Service account ----------------------------------------------------

if (-not $KeepServiceAccount) {
    $account = Get-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
    if ($account) {
        # Revoke the right before deleting the account, or the policy is left
        # holding an unresolvable SID that shows up in the next security audit.
        $rightsDir = Join-Path $env:TEMP "bp-rights-uninstall"
        New-Item -ItemType Directory -Path $rightsDir -Force | Out-Null
        $exportInf = Join-Path $rightsDir "export.inf"
        $revokeInf = Join-Path $rightsDir "revoke.inf"
        & secedit /export /cfg $exportInf /areas USER_RIGHTS | Out-Null
        $sid = $account.SID.Value
        $lines = Get-Content $exportInf -ErrorAction SilentlyContinue
        if ($lines -and (($lines | Where-Object { $_ -match '^SeServiceLogonRight' }) -match [regex]::Escape($sid))) {
            $lines = $lines | ForEach-Object {
                if ($_ -match '^SeServiceLogonRight') {
                    ($_ -replace ",\*$sid", "") -replace "=\s*\*$sid,", "= "
                } else { $_ }
            }
            $lines | Set-Content $revokeInf -Encoding Unicode
            & secedit /configure /db (Join-Path $rightsDir "secedit.sdb") /cfg $revokeInf /areas USER_RIGHTS | Out-Null
        }
        Remove-Item $rightsDir -Recurse -Force -ErrorAction SilentlyContinue

        Remove-LocalUser -Name $ServiceAccount -ErrorAction SilentlyContinue
        Write-Log "  service account $ServiceAccount removed." Green
    }
}

# ---- 6. Data ---------------------------------------------------------------

if ($RemoveData) {
    Write-Log ""
    Write-Log "-RemoveData was given. This DESTROYS the database, all print history," Yellow
    Write-Log "product images, and the Data Protection key ring. It cannot be undone." Yellow
    if ($PSCmdlet.ShouldProcess($DataDir, "Permanently delete the database and all application data")) {
        # Copy the log out first: it is inside the directory about to be deleted
        # and it is the only record of what this uninstall did.
        $rescued = Join-Path $env:TEMP (Split-Path $logPath -Leaf)
        Copy-Item $logPath $rescued -Force -ErrorAction SilentlyContinue
        Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "$DataDir deleted. Uninstall log kept at $rescued" -ForegroundColor Yellow
    }
} else {
    Write-Log ""
    Write-Log "Data kept at $DataDir — database, images, imports and the key ring." Green
    Write-Log "Reinstalling will pick it up again. Pass -RemoveData to delete it." DarkGray
    Write-Log "Log: $logPath" DarkGray
}
