<#
.SYNOPSIS
    Registers the nightly full backup and the hourly binary-log copy as
    Scheduled Tasks.

.DESCRIPTION
    Backup scheduling is deliberately Windows Scheduled Tasks and a runbook, not
    an in-application feature (§16). Backups must keep running when the
    application is stopped, being upgraded, or broken — which is exactly when
    they matter most.

.EXAMPLE
    .\Register-BackupTasks.ps1 -Destination E:\Backups\BarcodePrinter -OffboxPath \\nas\backups\barcodeprinter
#>
[CmdletBinding()]
param(
    [string]$InstallRoot = "D:\BarcodePrinter",
    [Parameter(Mandatory)]
    [string]$Destination,
    [string]$OffboxPath,
    [string]$FullBackupTime = "01:30",
    [string]$TaskUser = "SYSTEM"
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell session."
}

$script = Join-Path $InstallRoot "deploy\Backup-BarcodePrinter.ps1"
if (-not (Test-Path $script)) {
    New-Item -ItemType Directory -Path (Split-Path $script) -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "Backup-BarcodePrinter.ps1") $script -Force
}

function Register-BackupTask {
    param([string]$Name, [string]$Mode, $Trigger, [string]$Description)

    $arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" " +
                 "-Mode $Mode -InstallRoot `"$InstallRoot`" -Destination `"$Destination`""
    if ($OffboxPath) { $arguments += " -OffboxPath `"$OffboxPath`"" }

    $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId $TaskUser -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet `
        -StartWhenAvailable `
        -DontStopOnIdleEnd `
        -ExecutionTimeLimit (New-TimeSpan -Hours 2) `
        -MultipleInstances IgnoreNew

    Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $Name -Action $action -Trigger $Trigger `
        -Principal $principal -Settings $settings -Description $Description | Out-Null
    Write-Host "Registered '$Name'." -ForegroundColor Green
}

Register-BackupTask -Name "BarcodePrinter Full Backup" -Mode "Full" `
    -Trigger (New-ScheduledTaskTrigger -Daily -At $FullBackupTime) `
    -Description "Nightly mysqldump --single-transaction, image mirror, config and Data Protection key ring."

# Hourly, so the recovery point is never worse than one hour (RPO <= 1 h).
Register-BackupTask -Name "BarcodePrinter Binlog Copy" -Mode "Binlog" `
    -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).Date `
        -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 3650)) `
    -Description "Hourly copy of closed MySQL binary logs for point-in-time recovery."

Write-Host ""
Write-Host "Verifying by running the full backup once now..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName "BarcodePrinter Full Backup"

$deadline = (Get-Date).AddMinutes(30)
do {
    Start-Sleep -Seconds 5
    $state = (Get-ScheduledTask -TaskName "BarcodePrinter Full Backup").State
} while ($state -eq "Running" -and (Get-Date) -lt $deadline)

$result = (Get-ScheduledTaskInfo -TaskName "BarcodePrinter Full Backup").LastTaskResult
if ($result -ne 0) {
    throw "The first backup returned $result. Check $InstallRoot\logs\backup-*.log before relying on the schedule."
}

$status = Get-Content (Join-Path $InstallRoot "backup\backup-status.json") -Raw | ConvertFrom-Json
Write-Host "First backup succeeded at $($status.lastFullSuccessUtc) UTC." -ForegroundColor Green
Write-Host ""
Write-Host "A backup you have never restored is a hypothesis. Run .\Test-Recovery.ps1 before go-live."
