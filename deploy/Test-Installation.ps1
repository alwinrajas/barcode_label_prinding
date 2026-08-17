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
        Can the service account still read the certificate's private key?
        Do the shortcuts the user is expected to click actually exist?
        Does the service point at an executable that is still on disk?
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
    # The account Install-Server.ps1 creates and grants read access to the
    # certificate's private key. Overridable because the installer accepts an
    # override for it too, and checking the wrong account is worse than not
    # checking at all.
    [string]$ServiceAccount = "BarcodePrinterSvc",
    [switch]$Detailed
)

# The product name as the MSI and Install-Client.ps1 write it into the Start
# Menu and onto the desktop. One place, so a rename cannot leave this checking
# for a shortcut nothing creates any more.
$productName = "Barcode Label Printing"

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

# ---- Payload ---------------------------------------------------------------

Add-Check "API executable and client executable present" {
    # Cheapest possible check, and it runs first: if these two files are not
    # there, every failure below is a consequence rather than a cause, and
    # saying so up front saves a support engineer an hour.
    $missing = @()
    foreach ($relative in @("api\BarcodePrinter.Api.exe", "client\BarcodePrinter.Wpf.exe")) {
        if (-not (Test-Path (Join-Path $InstallDir $relative) -PathType Leaf)) {
            $missing += $relative
        }
    }
    if ($missing) { throw ("missing under '$InstallDir': " + ($missing -join ', ')) }
    "api\BarcodePrinter.Api.exe and client\BarcodePrinter.Wpf.exe present"
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

Add-Check "API service binary path points at an existing file" {
    # An uninstall that removes the files but leaves the service registration —
    # or an install into a different directory than the last one — produces a
    # service whose binPath names a file that is gone. It looks perfectly healthy
    # until the next boot, when it fails with error 2 and no explanation.
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if (-not $wmi)          { throw "service '$ServiceName' is not installed" }
    if (-not $wmi.PathName) { throw "the service registration names no binary" }

    # binPath is a command line, not a path: it may be quoted and may carry
    # arguments. Peel it apart in that order.
    $commandLine = $wmi.PathName.Trim()
    if ($commandLine.StartsWith('"')) {
        $close = $commandLine.IndexOf('"', 1)
        if ($close -lt 1) { throw "the service binary path is malformed: $commandLine" }
        $binary = $commandLine.Substring(1, $close - 1)
    } elseif (Test-Path -LiteralPath $commandLine -PathType Leaf) {
        # Unquoted and argument-free, possibly with spaces in the directory name.
        $binary = $commandLine
    } else {
        # Unquoted with arguments. The executable is the LONGEST leading prefix
        # that names a file — splitting on the first space would break on every
        # path under "C:\Program Files".
        $binary = $null
        $words = $commandLine -split ' '
        for ($i = 1; $i -le $words.Count; $i++) {
            $candidate = ($words[0..($i - 1)] -join ' ')
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $binary = $candidate }
        }
        if (-not $binary) { $binary = $words[0] }
    }

    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "the service points at '$binary', which does not exist — the registration is orphaned and the service will not start"
    }
    $binary
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

Add-Check "Certificate private key readable by the service account" {
    # The classic post-reboot failure: everything installs, the API runs because
    # the installer left it running, and after the next restart Kestrel cannot
    # open the key it is configured to serve. The service then reports only that
    # it would not start. Checking the ACL now turns that into a diagnosis.
    $certificate = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.FriendlyName -eq "Barcode Printer API" -and $_.HasPrivateKey } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $certificate) {
        throw "no certificate named 'Barcode Printer API' with a private key in LocalMachine\My"
    }

    # Naming the key file is itself a privileged operation on a machine
    # certificate: from a non-elevated console this throws "Invalid provider type
    # specified", which must not be reported as a broken installation when it is
    # really a check that could not run. Guard the empty result too — Join-Path
    # with an empty leaf returns the CONTAINING folder, and the ACL below would
    # then be read off a directory instead of the key.
    $keyFileName = $null
    try {
        $privateKey = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
        if ($privateKey -is [System.Security.Cryptography.RSACng]) {
            $keyFileName = $privateKey.Key.UniqueName                           # CNG
        } elseif ($privateKey) {
            $keyFileName = $privateKey.CspKeyContainerInfo.UniqueKeyContainerName # legacy CSP
        }
    } catch { }
    if ([string]::IsNullOrWhiteSpace($keyFileName)) {
        $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $elevated) {
            throw "the private key of $($certificate.Thumbprint) cannot be inspected without administrator rights; re-run this check from an elevated prompt"
        }
        throw "the private key container for $($certificate.Thumbprint) could not be determined, so its permissions cannot be verified"
    }

    $keyFile = @(
        (Join-Path "$env:ProgramData\Microsoft\Crypto\Keys" $keyFileName)
        (Join-Path "$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys" $keyFileName)
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    # Fail loudly rather than pass silently: "we could not find the key" is not
    # evidence that the key is readable.
    if (-not $keyFile) {
        throw "the private key file '$keyFileName' is not under %ProgramData%\Microsoft\Crypto — is the certificate installed for a user rather than the machine?"
    }

    # Compare on SID. The grant is made by SID, and the same account renders in
    # an ACL as MACHINE\BarcodePrinterSvc, .\BarcodePrinterSvc or a raw SID
    # depending on how it was written and whether it still resolves.
    $accountSid = $null
    try {
        $accountSid = (New-Object System.Security.Principal.NTAccount($env:COMPUTERNAME, $ServiceAccount)
            ).Translate([System.Security.Principal.SecurityIdentifier]).Value
    } catch { }
    if (-not $accountSid) { throw "the service account '$ServiceAccount' does not exist on this machine" }

    $readMask    = [int][System.Security.AccessControl.FileSystemRights]::Read
    $genericRead = [int]::MinValue   # 0x80000000, as unmapped key ACEs sometimes carry it
    $matching = @((Get-Acl -LiteralPath $keyFile).Access | Where-Object {
        $ruleSid = $null
        try { $ruleSid = $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } catch { }
        $ruleSid -eq $accountSid
    })

    $denied = @($matching | Where-Object { $_.AccessControlType -eq "Deny" })
    if ($denied) { throw "'$ServiceAccount' is explicitly denied access to '$keyFile'" }

    $allowed = @($matching | Where-Object {
        $granted = [int]$_.FileSystemRights
        $_.AccessControlType -eq "Allow" -and
        ((($granted -band $readMask) -eq $readMask) -or (($granted -band $genericRead) -ne 0))
    })
    # Only a direct grant counts. A grant inherited through a group would also
    # work, but this installer makes a direct one, so its absence is a real fault.
    if (-not $allowed) {
        throw "'$ServiceAccount' has no read access to '$keyFile'; the API will fail to start after the next reboot"
    }
    "$ServiceAccount can read $(Split-Path $keyFile -Leaf)"
}

Add-Check "/health reports healthy" {
    $request = [System.Net.HttpWebRequest]::Create("https://localhost:$HttpsPort/health")
    $request.Timeout = 15000
    $request.ServerCertificateValidationCallback = { $true }
    try {
        $response = $request.GetResponse()
        try {
            if ([int]$response.StatusCode -ne 200) { throw "HTTP $($response.StatusCode)" }
            "Healthy (200)"
        } finally { $response.Close() }
    } catch {
        throw $_.Exception.Message
    }
}

Add-Check "/health/ready reports healthy" {
    # Distinct from /health: this one exercises the database dependency, so it
    # fails when the API is up but cannot reach MySQL.
    $request = [System.Net.HttpWebRequest]::Create("https://localhost:$HttpsPort/health/ready")
    $request.Timeout = 15000
    $request.ServerCertificateValidationCallback = { $true }
    try {
        $response = $request.GetResponse()
        try {
            if ([int]$response.StatusCode -ne 200) { throw "HTTP $($response.StatusCode)" }
            "Healthy (200)"
        } finally { $response.Close() }
    } catch {
        throw $_.Exception.Message
    }
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

Add-Check "Desktop shortcut present" {
    # A correctly installed application the user cannot find is, to the user, a
    # failed installation. The Start Menu entry is the one that must exist: it is
    # the installer's job and nobody deletes it by accident.
    $startMenu = [Environment]::GetFolderPath("CommonPrograms")
    if (-not $startMenu) { $startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs" }
    $productFolder = Join-Path $startMenu $productName

    # Two shapes are legitimate: the MSI puts the shortcut in a product folder,
    # Install-Client.ps1 puts it straight into Programs. Either one means the
    # user can start the application from the Start Menu.
    $menu = @(
        (Join-Path $productFolder "$productName.lnk"),
        (Join-Path $startMenu "$productName.lnk")
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

    if (-not $menu -and (Test-Path -LiteralPath $productFolder -PathType Container)) {
        # A renamed shortcut still works; only a missing one does not.
        $menu = @(Get-ChildItem -LiteralPath $productFolder -Filter *.lnk -File -ErrorAction SilentlyContinue |
                  ForEach-Object { $_.FullName })
    }
    if (-not $menu) {
        throw "no Start Menu shortcut under '$startMenu'; the application cannot be launched from the Start Menu"
    }

    $desktop = @([Environment]::GetFolderPath("CommonDesktopDirectory"),
                 [Environment]::GetFolderPath("Desktop")) |
        Where-Object { $_ } |
        ForEach-Object { Join-Path $_ "$productName.lnk" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

    # A missing desktop icon is reported, not failed: users delete desktop icons
    # deliberately, and nothing stops working when they do.
    if ($desktop) {
        "Start Menu and Desktop shortcuts present"
    } else {
        "Start Menu shortcut present; no Desktop shortcut (a user may have removed it)"
    }
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
