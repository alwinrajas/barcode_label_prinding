<#
.SYNOPSIS
    Backs up the Barcode Printer database, images, configuration and Data
    Protection key ring.

.DESCRIPTION
    Blueprint §16. Targets RPO <= 1 hour, RTO <= 2 hours.

      -Mode Full   (nightly)  mysqldump --single-transaction + images + config + keys
      -Mode Binlog (hourly)   copies newly closed binary logs for point-in-time recovery

    --single-transaction takes a consistent snapshot WITHOUT locking, so a night
    shift is never interrupted by the backup.

    THE KEY RING IS PART OF THE BACKUP SET. The Oracle password in
    integration_settings is encrypted with it; without the key ring that
    password is unrecoverable and the integration must be reconfigured by hand.

    Every run writes backup-status.json. The application reads that file and
    warns in-app when the last successful backup is over 48 hours old — it does
    not, and must not, offer a restore.

.EXAMPLE
    .\Backup-BarcodePrinter.ps1 -Mode Full -Destination E:\Backups\BarcodePrinter
#>
[CmdletBinding()]
param(
    [ValidateSet("Full", "Binlog")]
    [string]$Mode = "Full",
    [string]$InstallRoot   = "D:\BarcodePrinter",
    [Parameter(Mandatory)]
    [string]$Destination,
    [string]$MySqlBin      = "C:\Program Files\MySQL\MySQL Server 8.4\bin",
    [string]$MySqlDataDir  = "C:\ProgramData\MySQL\MySQL Server 8.4\Data",
    [string]$MySqlDatabase = "barcodeprinter",
    [string]$MySqlUser     = "root",
    [string]$MySqlConfig   = "C:\ProgramData\MySQL\backup.cnf",
    [int]$RetentionDays    = 30,
    [string]$OffboxPath
)

$ErrorActionPreference = "Stop"
$started = Get-Date
$statusFile = Join-Path $InstallRoot "backup\backup-status.json"
$log = Join-Path $InstallRoot ("logs\backup-{0:yyyyMMdd}.log" -f $started)

function Write-Log([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}" -f (Get-Date), $Mode, $Message
    Add-Content -Path $log -Value $line
    Write-Host $line
}

function Write-Status([string]$Result, [string]$ErrorMessage, [long]$Bytes) {
    # Merge, so an hourly binlog run never erases the nightly full-backup time
    # the in-app warning is based on.
    $status = if (Test-Path $statusFile) {
        Get-Content $statusFile -Raw | ConvertFrom-Json
    } else {
        [pscustomobject]@{}
    }

    $fields = @{
        lastRunUtc      = $started.ToUniversalTime().ToString("o")
        lastMode        = $Mode
        lastResult      = $Result
        lastError       = $ErrorMessage
        durationSeconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    }
    if ($Result -eq "Success") {
        $fields["last${Mode}SuccessUtc"] = $started.ToUniversalTime().ToString("o")
        $fields["last${Mode}Bytes"] = $Bytes
    }
    foreach ($key in $fields.Keys) {
        $status | Add-Member -NotePropertyName $key -NotePropertyValue $fields[$key] -Force
    }

    New-Item -ItemType Directory -Path (Split-Path $statusFile) -Force | Out-Null
    $status | ConvertTo-Json -Depth 4 | Set-Content $statusFile -Encoding UTF8
}

try {
    New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
    $stamp = "{0:yyyyMMdd-HHmmss}" -f $started
    $script:bytes = 0L

    if ($Mode -eq "Full") {
        $target = Join-Path $Destination "full\$stamp"
        New-Item -ItemType Directory -Path $target -Force | Out-Null

        # --- database ---
        Write-Log "Dumping $MySqlDatabase..."
        $dump = Join-Path $target "$MySqlDatabase.sql"
        # Credentials come from a 0600-equivalent defaults file, never the command
        # line — arguments are visible to every user in the process list.
        if (-not (Test-Path $MySqlConfig)) {
            throw "MySQL credential file not found at $MySqlConfig. See RUNBOOK.md."
        }
        & (Join-Path $MySqlBin "mysqldump.exe") `
            "--defaults-extra-file=$MySqlConfig" `
            --single-transaction --routines --triggers --events `
            --source-data=2 --hex-blob `
            $MySqlDatabase --result-file=$dump
        if ($LASTEXITCODE -ne 0) { throw "mysqldump exited with $LASTEXITCODE." }

        Compress-Archive -Path $dump -DestinationPath "$dump.zip" -CompressionLevel Optimal
        Remove-Item $dump
        $script:bytes += (Get-Item "$dump.zip").Length
        Write-Log ("Database dumped ({0:N1} MB compressed)." -f ($script:bytes / 1MB))

        # --- images: mirrored, not re-copied. Content-addressed names mean an
        #     existing file never changes, so /MIR moves only what is new. ---
        Write-Log "Mirroring images..."
        $imageTarget = Join-Path $Destination "images"
        & robocopy (Join-Path $InstallRoot "images") $imageTarget /MIR /R:2 /W:5 /NP /NFL /NDL /NJH /NJS
        # robocopy exit codes below 8 are informational, not failures.
        if ($LASTEXITCODE -ge 8) { throw "robocopy failed with $LASTEXITCODE." }
        $LASTEXITCODE = 0

        # --- configuration and the key ring ---
        Write-Log "Copying configuration and the Data Protection key ring..."
        Copy-Item (Join-Path $InstallRoot "api\appsettings*.json") $target -Force
        Copy-Item (Join-Path $InstallRoot "keys") (Join-Path $target "keys") -Recurse -Force

        # --- retention ---
        Get-ChildItem (Join-Path $Destination "full") -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.CreationTime -lt $started.AddDays(-$RetentionDays) } |
            ForEach-Object {
                Write-Log "Pruning $($_.Name) (older than $RetentionDays days)."
                Remove-Item $_.FullName -Recurse -Force
            }
    }
    else {
        # --- binlogs: hourly, for point-in-time recovery between full dumps ---
        $target = Join-Path $Destination "binlog"
        New-Item -ItemType Directory -Path $target -Force | Out-Null

        # Flush so the current log is closed and safe to copy; the one MySQL is
        # actively writing must be left alone.
        & (Join-Path $MySqlBin "mysql.exe") "--defaults-extra-file=$MySqlConfig" `
            -e "FLUSH BINARY LOGS;" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "FLUSH BINARY LOGS failed with $LASTEXITCODE." }

        $index = Get-ChildItem $MySqlDataDir -Filter "*.index" | Select-Object -First 1
        $current = if ($index) { (Get-Content $index.FullName | Select-Object -Last 1).Trim() } else { $null }

        $script:copied = 0
        Get-ChildItem $MySqlDataDir -Filter "bin*.0*" | ForEach-Object {
            if ($current -and $_.Name -eq (Split-Path $current -Leaf)) { return }
            $destinationFile = Join-Path $target $_.Name
            if (-not (Test-Path $destinationFile) -or
                (Get-Item $destinationFile).Length -ne $_.Length) {
                Copy-Item $_.FullName $destinationFile -Force
                $script:copied++
                $script:bytes += $_.Length
            }
        }
        Write-Log "Copied $script:copied closed binary log(s)."

        Get-ChildItem $target -File |
            Where-Object { $_.LastWriteTime -lt $started.AddDays(-$RetentionDays) } |
            Remove-Item -Force
    }

    # --- off-box copy: a backup on the same machine is not a backup ---
    if ($OffboxPath) {
        Write-Log "Copying off-box to $OffboxPath..."
        & robocopy $Destination $OffboxPath /MIR /R:2 /W:5 /NP /NFL /NDL /NJH /NJS
        if ($LASTEXITCODE -ge 8) { throw "Off-box copy failed with $LASTEXITCODE." }
        $LASTEXITCODE = 0
    } else {
        Write-Log "WARNING: no -OffboxPath. A backup on the same machine does not survive losing the machine."
    }

    Write-Log "Completed in $([math]::Round(((Get-Date) - $started).TotalSeconds, 1))s."
    Write-Status -Result "Success" -ErrorMessage $null -Bytes $script:bytes
}
catch {
    Write-Log "FAILED: $($_.Exception.Message)"
    Write-Status -Result "Failed" -ErrorMessage $_.Exception.Message -Bytes 0
    throw
}
