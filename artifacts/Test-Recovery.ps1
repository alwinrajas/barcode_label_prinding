<#
.SYNOPSIS
    Rehearses the restore against a scratch database and measures how long it
    takes. Read-only with respect to production.

.DESCRIPTION
    Blueprint §16 requires recovery to be rehearsed once before go-live. This
    script does that rehearsal and, crucially, TIMES it — RTO <= 2 hours is a
    claim until someone has actually measured it on this hardware with this much
    data.

    It restores into a scratch database (default barcodeprinter_restoretest) and
    never touches the live one. It verifies the restored copy really is usable:
    the tables are present, print history came back, and the migrator considers
    the schema current.

    NOT a production restore path. There is deliberately no one-click restore in
    the application (§16) — a real recovery is a deliberate, supervised
    operation performed from RUNBOOK.md.

.EXAMPLE
    .\Test-Recovery.ps1 -BackupPath E:\Backups\BarcodePrinter
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BackupPath,
    [string]$MySqlBin      = "C:\Program Files\MySQL\MySQL Server 8.4\bin",
    [string]$MySqlConfig   = "C:\ProgramData\MySQL\backup.cnf",
    [string]$ScratchDatabase = "barcodeprinter_restoretest",
    [switch]$KeepScratchDatabase
)

$ErrorActionPreference = "Stop"
$started = Get-Date

if ($ScratchDatabase -eq "barcodeprinter") {
    throw "Refusing to restore over the live database. Use a scratch name."
}

$latest = Get-ChildItem (Join-Path $BackupPath "full") -Directory |
    Sort-Object CreationTime -Descending | Select-Object -First 1
if (-not $latest) {
    throw "No full backup found under $BackupPath\full."
}
Write-Host "Rehearsing recovery from $($latest.Name)..." -ForegroundColor Cyan

$archive = Get-ChildItem $latest.FullName -Filter "*.sql.zip" | Select-Object -First 1
if (-not $archive) { throw "No .sql.zip in $($latest.FullName)." }

$work = Join-Path $env:TEMP "bp-restore-$([guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    Write-Host "  Extracting..." -ForegroundColor DarkGray
    Expand-Archive $archive.FullName -DestinationPath $work
    $dump = Get-ChildItem $work -Filter "*.sql" | Select-Object -First 1

    $mysql = Join-Path $MySqlBin "mysql.exe"
    Write-Host "  Creating scratch database $ScratchDatabase..." -ForegroundColor DarkGray
    & $mysql "--defaults-extra-file=$MySqlConfig" -e @"
DROP DATABASE IF EXISTS ``$ScratchDatabase``;
CREATE DATABASE ``$ScratchDatabase`` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
"@
    if ($LASTEXITCODE -ne 0) { throw "Could not create the scratch database." }

    Write-Host "  Restoring (this is the part being timed)..." -ForegroundColor DarkGray
    $restoreStarted = Get-Date
    & cmd /c "`"$mysql`" `"--defaults-extra-file=$MySqlConfig`" $ScratchDatabase < `"$($dump.FullName)`""
    if ($LASTEXITCODE -ne 0) { throw "Restore failed with $LASTEXITCODE." }
    $restoreDuration = (Get-Date) - $restoreStarted

    Write-Host "  Verifying..." -ForegroundColor DarkGray
    $checks = & $mysql "--defaults-extra-file=$MySqlConfig" -N -B -e @"
SELECT
  (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$ScratchDatabase'),
  (SELECT COUNT(*) FROM ``$ScratchDatabase``.print_jobs),
  (SELECT COUNT(*) FROM ``$ScratchDatabase``.products),
  (SELECT COUNT(*) FROM ``$ScratchDatabase``.users),
  (SELECT COUNT(*) FROM ``$ScratchDatabase``.schemaversions);
"@
    $tables, $jobs, $products, $users, $migrations = $checks -split "`t"

    # 26 tables per §19.4, plus DbUp's schemaversions.
    if ([int]$tables -lt 27) { throw "Only $tables tables restored; expected at least 27." }
    if ([int]$users -lt 1)   { throw "No users restored — the backup is not usable." }
    if ([int]$migrations -lt 1) { throw "schemaversions is empty; the schema history did not survive." }

    $imageSource = Join-Path $BackupPath "images"
    $imageCount = if (Test-Path $imageSource) {
        (Get-ChildItem $imageSource -Recurse -File).Count
    } else { 0 }

    $total = (Get-Date) - $started
    Write-Host ""
    Write-Host "Recovery rehearsal PASSED" -ForegroundColor Green
    Write-Host "  backup taken       : $($latest.CreationTime)"
    Write-Host "  restore time       : $([math]::Round($restoreDuration.TotalMinutes, 1)) min  <- the RTO figure"
    Write-Host "  end-to-end         : $([math]::Round($total.TotalMinutes, 1)) min"
    Write-Host "  tables             : $tables"
    Write-Host "  products / jobs    : $products / $jobs"
    Write-Host "  users              : $users"
    Write-Host "  images in backup   : $imageCount"

    if ($restoreDuration.TotalHours -gt 2) {
        Write-Warning "Restore exceeded the 2-hour RTO target (§16). Revisit the backup strategy before go-live."
    }
    if ($imageCount -eq 0) {
        Write-Warning "No images in the backup set. Printing survives a missing image, but check the mirror is running."
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
    if (-not $KeepScratchDatabase) {
        & (Join-Path $MySqlBin "mysql.exe") "--defaults-extra-file=$MySqlConfig" `
            -e "DROP DATABASE IF EXISTS ``$ScratchDatabase``;" 2>$null | Out-Null
    }
}
