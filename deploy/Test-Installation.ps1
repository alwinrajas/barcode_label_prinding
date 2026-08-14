<#
.SYNOPSIS
    Verifies that an installed Barcode Label Printing system is actually
    working, and returns one result object per check.

.DESCRIPTION
    "The installer finished" and "the application works" are different claims.
    This makes the second one checkable: the installer refuses to offer Launch
    until every check here passes, and support can re-run it later on a machine
    that has started misbehaving.

    Every check answers a question a user would otherwise answer for us by
    filing a bug:

        Is the database engine running?
        Can the application's own account reach its own database?
        Is the schema current?
        Is the API service running, and running as the right account?
        Does the HTTPS endpoint answer, with the certificate we installed?
        Is the certificate trusted, so the client does not fail validation?
        Does the client know where the API is?

    Output is data, not text, so the caller can decide how to present it.

.EXAMPLE
    .\Test-Installation.ps1 -InstallDir "C:\Program Files\Barcode Label Printing"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallDir,
    [string]$DataDir = "$env:ProgramData\BarcodePrinter",
    [int]$HttpsPort = 5001,
    [string]$ServiceName = "BarcodePrinter.Api",
    [string]$MySqlServiceName = "BarcodePrinterMySQL",
    [switch]$Detailed
)

$ErrorActionPreference = "Continue"

$results = [System.Collections.Generic.List[object]]::new()
function Add-Check([string]$Name, [scriptblock]$Test) {
    try {
        $detail = & $Test
        $results.Add([pscustomobject]@{ Name = $Name; Ok = $true; Detail = $detail })
    } catch {
        $results.Add([pscustomobject]@{ Name = $Name; Ok = $false; Detail = $_.Exception.Message })
    }
}

# ---- Database engine -------------------------------------------------------

Add-Check "MySQL service running" {
    $svc = Get-Service -Name $MySqlServiceName -ErrorAction SilentlyContinue
    if (-not $svc)                    { throw "service '$MySqlServiceName' is not installed" }
    if ($svc.Status -ne "Running")    { throw "service is $($svc.Status)" }
    "$MySqlServiceName running"
}

$settingsPath = Join-Path (Join-Path $InstallDir "api") "appsettings.Production.json"
$connectionString = $null
Add-Check "API configuration present" {
    if (-not (Test-Path $settingsPath)) { throw "$settingsPath is missing" }
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $script:connectionString = $settings.ConnectionStrings.BarcodePrinter
    if (-not $script:connectionString) { throw "no connection string configured" }
    if (-not $settings.Kestrel.Endpoints.Https.Certificate.Subject) {
        throw "the HTTPS endpoint names no certificate — the service cannot start"
    }
    "connection string and HTTPS certificate configured"
}

Add-Check "Database reachable and schema current" {
    if (-not $connectionString) { throw "skipped: no connection string" }
    # The migrator is the authority on the schema, and it connects as the
    # application's own restricted account — so this proves the grant is right
    # as well as the schema.
    $migrator = Join-Path $InstallDir "migrator\BarcodePrinter.DbMigrator.exe"
    $output = & $migrator $connectionString --preflight-only 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw ($output.Trim() -split "`n" | Select-Object -Last 1) }

    $schema = & $migrator $connectionString 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "migrations are not current" }
    if ($schema -notmatch 'Schema is up to date') { throw "migrator did not confirm the schema" }
    "schema up to date"
}

# ---- API service -----------------------------------------------------------

Add-Check "API service running" {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc)                 { throw "service '$ServiceName' is not installed" }
    if ($svc.Status -ne "Running") { throw "service is $($svc.Status)" }
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    if ($wmi.StartName -match 'LocalSystem') { throw "running as LocalSystem, not the dedicated account" }
    "running as $($wmi.StartName), start mode $($wmi.StartMode)"
}

Add-Check "API starts automatically at boot" {
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    if ($wmi.StartMode -ne "Auto") { throw "start mode is $($wmi.StartMode)" }
    "automatic"
}

# ---- HTTPS -----------------------------------------------------------------

$servedThumbprint = $null
Add-Check "HTTPS endpoint answers" {
    $client = [System.Net.Sockets.TcpClient]::new("localhost", $HttpsPort)
    try {
        $ssl = [System.Net.Security.SslStream]::new($client.GetStream(), $false, { $true })
        $ssl.AuthenticateAsClient("localhost")
        $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($ssl.RemoteCertificate)
        $script:servedThumbprint = $cert.Thumbprint
        "TLS up, certificate $($cert.Subject), expires $($cert.NotAfter.ToString('yyyy-MM-dd'))"
    } finally { $client.Dispose() }
}

Add-Check "Served certificate is the installed one" {
    if (-not $servedThumbprint) { throw "skipped: no TLS handshake" }
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $subject = $settings.Kestrel.Endpoints.Https.Certificate.Subject
    $installed = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.HasPrivateKey -and $_.GetNameInfo('SimpleName', $false) -eq $subject }
    if (-not $installed) { throw "no certificate for subject '$subject' in LocalMachine\My" }
    if ($installed.Thumbprint -notcontains $servedThumbprint) {
        # Almost always means Kestrel fell back to the ASP.NET Core development
        # certificate because the configured one could not be loaded.
        throw "the endpoint is serving $servedThumbprint, which is not the configured certificate"
    }
    $servedThumbprint
}

Add-Check "Certificate is trusted on this machine" {
    if (-not $servedThumbprint) { throw "skipped: no TLS handshake" }
    $trusted = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq $servedThumbprint }
    if (-not $trusted) { throw "not present in the machine's trusted roots; the client will fail certificate validation" }
    "trusted"
}

Add-Check "/health reports healthy" {
    $response = Invoke-WebRequest "https://localhost:$HttpsPort/health" -TimeoutSec 15 -UseBasicParsing
    if ($response.StatusCode -ne 200) { throw "HTTP $($response.StatusCode)" }
    $response.Content
}

Add-Check "/health/ready reports healthy" {
    # Distinct from /health: this one exercises the database dependency, so it
    # fails when the API is up but cannot reach MySQL.
    $response = Invoke-WebRequest "https://localhost:$HttpsPort/health/ready" -TimeoutSec 15 -UseBasicParsing
    if ($response.StatusCode -ne 200) { throw "HTTP $($response.StatusCode)" }
    $response.Content
}

# ---- Client ----------------------------------------------------------------

Add-Check "Client payload complete" {
    $clientExe = Join-Path $InstallDir "client\BarcodePrinter.Wpf.exe"
    if (-not (Test-Path $clientExe)) { throw "BarcodePrinter.Wpf.exe is missing" }
    # Self-contained means the runtime travels with it; a client folder holding
    # only the exe is the classic broken deployment.
    if (-not (Test-Path (Join-Path $InstallDir "client\hostfxr.dll"))) {
        throw "the .NET runtime files are missing — the client will not start on a machine without .NET"
    }
    "$((Get-ChildItem (Join-Path $InstallDir 'client') -File).Count) files"
}

Add-Check "Client points at this API" {
    $configPath = Join-Path $env:ProgramData "BarcodePrinter\client.json"
    if (-not (Test-Path $configPath)) { throw "client.json is missing; the client would fall back to a development URL" }
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    $expected = "https://localhost:$HttpsPort"
    if ($config.apiBaseUrl -ne $expected) { throw "client.json points at '$($config.apiBaseUrl)', expected '$expected'" }
    $config.apiBaseUrl
}

# ---- Report ----------------------------------------------------------------

if ($Detailed) {
    $results | Format-Table @{n='';e={if($_.Ok){'ok'}else{'FAIL'}}}, Name, Detail -AutoSize -Wrap | Out-Host
    $failedCount = @($results | Where-Object { -not $_.Ok }).Count
    Write-Host ""
    if ($failedCount) {
        Write-Host "$failedCount of $($results.Count) checks failed." -ForegroundColor Red
    } else {
        Write-Host "All $($results.Count) checks passed." -ForegroundColor Green
    }
}

$results
