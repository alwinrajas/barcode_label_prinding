<#
.SYNOPSIS
    Undoes the machine-level resources that a FAILED Barcode Label Printing
    transaction created. Run by Windows Installer as the RollbackSystem custom
    action; not intended to be run by hand.

.DESCRIPTION
    Windows Installer rolls back only what Windows Installer did. The services,
    certificate, service account and firewall rule are created by
    Install-BarcodePrinter.ps1 through PowerShell, so the transaction can be
    rolled back to the letter and still leave every one of them behind. That is
    what happened in BP-20260816-839444A5: the payload was removed and the
    machine was left with a stopped BarcodePrinter.Api service whose binPath
    pointed at a deleted executable, plus a BarcodePrinterMySQL service still
    running.

    This script does NOT try to work out what to delete. Install-BarcodePrinter
    writes $DataDir\install-transaction.json before it provisions anything,
    recording which of those resources ALREADY existed. Everything here removes
    only resources whose "pre-existed" flag is false — the difference between the
    machine before the run and the machine now. That is safe on a machine that
    already ran the product, safe to run twice, and safe when half the work never
    happened.

    What it will never do, no matter what the manifest says:
      * delete the database ($DataDir\mysql)
      * delete the Data Protection key ring ($DataDir\keys)
      * delete $DataDir itself
      * delete a certificate that existed before this run
      * do anything at all when the manifest is missing or already committed

    A rollback runs on a machine that is already having a bad day. Every step
    tolerates its resource being absent, and the script always exits 0 — failing
    here would turn one failure into two and tell the operator nothing useful.

.EXAMPLE
    .\Rollback-BarcodePrinter.ps1 -InstallDir "C:\Program Files\Barcode Label Printing"
#>
[CmdletBinding()]
param(
    # Where the payload was laid down. Only used to find the generated
    # appsettings.Production.json; the payload itself belongs to Windows
    # Installer, which is removing it in the same rollback.
    [string]$InstallDir,

    [string]$DataDir = "$env:ProgramData\BarcodePrinter"
)

# Continue, not Stop: a rollback that aborts halfway leaves a worse machine than
# one that reports what it could not do and keeps going.
$ErrorActionPreference = "Continue"
$ProgressPreference    = "SilentlyContinue"

$manifestPath = Join-Path $DataDir "install-transaction.json"

# ---- Gate: is there a transaction of ours to undo? --------------------------
# The rollback script is written into the rollback sequence of EVERY failed
# transaction, including ones that failed before ConfigureSystem ever ran and
# ones on machines this product was never installed on. Silence is the correct
# response to all of them — no log file, no side effects, nothing.

if (-not (Test-Path $manifestPath)) { exit 0 }

$manifest = $null
try {
    $manifest = Get-Content $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json
} catch {
    # An unreadable manifest is indistinguishable from no manifest. Deleting
    # resources on a guess is the one outcome worse than deleting none.
    exit 0
}
if (-not $manifest -or -not $manifest.preExisting) { exit 0 }

# The whole point of the commit flag: this installation finished successfully,
# so its services and certificate belong to it, not to whatever failed later.
if ($manifest.committed) { exit 0 }

# ---- Logging ----------------------------------------------------------------

$logDir = Join-Path $DataDir "logs"
New-Item -ItemType Directory -Path $logDir -Force -ErrorAction SilentlyContinue | Out-Null
$logPath = Join-Path $logDir ("rollback-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

function Write-Log([string]$Message, [string]$Colour = "Gray") {
    Add-Content -Path $logPath -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Message) -ErrorAction SilentlyContinue
    Write-Host $Message -ForegroundColor $Colour
}

function Test-PreExisting([string]$Name) {
    # A flag we cannot find is treated as "it was already there". An unrecognised
    # resource belongs to somebody else until the manifest proves otherwise, and
    # that is the direction an unknown must always fall.
    $property = $manifest.preExisting.PSObject.Properties[$Name]
    if (-not $property) { return $true }
    return [bool]$property.Value
}

# ---- Main -------------------------------------------------------------------

try {
    Write-Log "Rolling back a failed Barcode Label Printing installation"
    Write-Log "  reference : $($manifest.referenceId)"
    Write-Log "  started   : $($manifest.startedUtc)"
    Write-Log "  manifest  : $manifestPath"
    Write-Log "  log       : $logPath"
    Write-Log ""
    Write-Log "Removing only resources this installation created. The database, the key ring" DarkGray
    Write-Log "and everything else under $DataDir are not touched." DarkGray
    Write-Log ""

    # ---- 1. Services, in dependency order ----------------------------------
    # The API first: it holds connections to MySQL, and stopping MySQL underneath
    # a running API only fills the event log with reconnect failures.

    $serviceNames = [ordered]@{
        apiService   = $(if ($manifest.apiServiceName)   { $manifest.apiServiceName }   else { "BarcodePrinter.Api" })
        mySqlService = $(if ($manifest.mySqlServiceName) { $manifest.mySqlServiceName } else { "BarcodePrinterMySQL" })
    }

    foreach ($flag in @("apiService", "mySqlService")) {
        $name = $serviceNames[$flag]

        if (Test-PreExisting $flag) {
            Write-Log "  service $name existed before this install — left running." DarkGray
            continue
        }

        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if (-not $service) {
            Write-Log "  service $name is already gone." DarkGray
            continue
        }

        if ($service.Status -ne "Stopped") {
            Write-Log "  stopping $name..."
            Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
            try { $service.WaitForStatus("Stopped", "00:01:00") } catch { }
        }

        # sc.exe rather than Remove-Service: Remove-Service is PowerShell 6+, and
        # this runs under Windows PowerShell 5.1. It also deletes a service whose
        # binPath no longer resolves, which is exactly the orphan we are here for.
        & sc.exe delete $name | Out-Null
        Write-Log "  service $name removed (created by this installation)." Green
    }

    # ---- 2. Certificates ----------------------------------------------------
    # Removal is by exclusion: anything named 'Barcode Printer API' that was NOT
    # in the store when this run started. A customer who already had one — from
    # an earlier install, or issued by their own CA and renamed — keeps it.

    $preThumbprints = @()
    if ($manifest.preExisting.certificateThumbprints) {
        $preThumbprints = @($manifest.preExisting.certificateThumbprints)
    }

    $removedThumbprints = @()
    $ours = Get-ChildItem "Cert:\LocalMachine\My" -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -eq "Barcode Printer API" -and $preThumbprints -notcontains $_.Thumbprint }
    foreach ($certificate in $ours) {
        $thumbprint = $certificate.Thumbprint
        Remove-Item $certificate.PSPath -Force -ErrorAction SilentlyContinue
        $removedThumbprints += $thumbprint
        Write-Log "  removed certificate $thumbprint from LocalMachine\My." Green
    }
    if ($preThumbprints.Count -gt 0) {
        Write-Log ("  kept {0} pre-existing certificate(s): {1}" -f $preThumbprints.Count, ($preThumbprints -join ", ")) DarkGray
    }

    # Install-Client trusts the same certificate by copying it into the machine
    # root store. Only thumbprints we just deleted from My are removed here: the
    # manifest snapshots My, so a root-store entry that does not match one of
    # those is not something this run can prove it created.
    foreach ($thumbprint in $removedThumbprints) {
        $trusted = Get-ChildItem "Cert:\LocalMachine\Root" -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $thumbprint }
        foreach ($certificate in $trusted) {
            Remove-Item $certificate.PSPath -Force -ErrorAction SilentlyContinue
            Write-Log "  removed certificate $thumbprint from LocalMachine\Root." Green
        }
    }

    # ---- 3. Firewall --------------------------------------------------------

    if (Test-PreExisting "firewallRule") {
        Write-Log "  firewall rule 'Barcode Printer API' existed before this install — left alone." DarkGray
    } else {
        $rule = Get-NetFirewallRule -DisplayName "Barcode Printer API" -ErrorAction SilentlyContinue
        if ($rule) {
            $rule | Remove-NetFirewallRule -ErrorAction SilentlyContinue
            Write-Log "  firewall rule 'Barcode Printer API' removed (created by this installation)." Green
        } else {
            Write-Log "  firewall rule 'Barcode Printer API' is already gone." DarkGray
        }
    }

    # ---- 4. Service account -------------------------------------------------

    $accountName = $(if ($manifest.serviceAccount) { $manifest.serviceAccount } else { "BarcodePrinterSvc" })
    if (Test-PreExisting "serviceAccount") {
        Write-Log "  local account $accountName existed before this install — left alone." DarkGray
    } else {
        $account = Get-LocalUser -Name $accountName -ErrorAction SilentlyContinue
        if (-not $account) {
            Write-Log "  local account $accountName is already gone." DarkGray
        } else {
            # Revoke the right before deleting the account, or the local policy is
            # left holding an unresolvable SID that surfaces in the next security
            # audit. Same approach as Uninstall-BarcodePrinter.ps1.
            try {
                $rightsDir = Join-Path $env:TEMP "bp-rights-rollback"
                New-Item -ItemType Directory -Path $rightsDir -Force -ErrorAction SilentlyContinue | Out-Null
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
                    Write-Log "  revoked SeServiceLogonRight from $accountName." Green
                }
                Remove-Item $rightsDir -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                Write-Log "  could not revoke SeServiceLogonRight; removing the account anyway." Yellow
            }

            Remove-LocalUser -Name $accountName -ErrorAction SilentlyContinue
            Write-Log "  local account $accountName removed (created by this installation)." Green
        }
    }

    # ---- 5. Generated configuration ----------------------------------------
    # Only the two files this run generates. appsettings.Production.json carries
    # the connection string and the JWT signing key, so an aborted install must
    # not leave it in Program Files; client.json would otherwise point the
    # desktop client at an API that no longer exists.

    $generated = @()
    if ($InstallDir) {
        $generated += [pscustomobject]@{
            Flag = "apiSettings"
            Path = (Join-Path $InstallDir "api\appsettings.Production.json")
        }
    }
    $generated += [pscustomobject]@{
        Flag = "clientConfig"
        Path = (Join-Path $DataDir "client.json")
    }

    foreach ($file in $generated) {
        if (Test-PreExisting $file.Flag) {
            Write-Log "  $($file.Path) existed before this install — left alone." DarkGray
            continue
        }
        if (Test-Path $file.Path) {
            Remove-Item $file.Path -Force -ErrorAction SilentlyContinue
            Write-Log "  removed generated config $($file.Path)." Green
        } else {
            Write-Log "  $($file.Path) was never written." DarkGray
        }
    }

    # ---- 6. Data, explicitly not touched ------------------------------------
    # Stated in the log rather than merely omitted from the code: the single most
    # damaging thing this script could do is delete a database on a failed
    # upgrade, and the log is where an operator goes to confirm it did not.

    Write-Log ""
    Write-Log "Kept, deliberately:" Yellow
    Write-Log "  the database at $(Join-Path $DataDir 'mysql')" Yellow
    Write-Log "  the Data Protection key ring at $(Join-Path $DataDir 'keys')" Yellow
    Write-Log "  everything else under $DataDir, including logs and images" Yellow

} catch {
    # Reported, never rethrown. Windows Installer is already rolling back; a
    # non-zero exit from here buys nothing and hides what did get cleaned up.
    Write-Log ("Rollback did not complete cleanly: {0}" -f $_.Exception.Message) Red
    Write-Log "Remove anything listed above by hand if it is still present." Yellow
}

# Last, and outside the try: the manifest describes one transaction, and that
# transaction is now over either way. Leaving it behind would let the NEXT failed
# transaction act on a snapshot that no longer describes this machine.
Remove-Item $manifestPath -Force -ErrorAction SilentlyContinue

Write-Log ""
Write-Log "Rollback finished. Log: $logPath" Green
exit 0
