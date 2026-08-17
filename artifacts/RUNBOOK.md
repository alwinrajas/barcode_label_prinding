# Barcode Label Printing — Deployment and Recovery Runbook

Applies to the server and the ~20 client workstations on the LAN.
Targets: **RPO ≤ 1 hour, RTO ≤ 2 hours** (blueprint §16).

---

## Which deployment are you doing?

There are two, and they do not mix.

**Single machine — `BarcodePrinterSetup.exe`.** One file, double-clicked. It
installs MySQL, creates the database and a restricted account, applies the
schema, installs and starts the API service, issues and trusts an HTTPS
certificate, configures the client and puts shortcuts on the desktop and Start
Menu. Nothing below section 8 applies: there are no manual steps. Section 1a
covers it. This is what a customer receives.

**LAN server plus workstations — the scripts in this package.** The server is
built by hand from sections 0–6 and each workstation runs `Install-Client.ps1`.
Use this when the database and API live on a server and operators work from
their own machines.

The rest of this runbook documents the second one, plus the backup, recovery and
troubleshooting procedures that apply to both.

---

## 1a. Single-machine installation

Give the customer `BarcodePrinterSetup.exe` (~400 MB) and nothing else. They
double-click it, accept the elevation prompt, and wait. It finishes on a screen
offering **Launch**.

They do **not** need .NET, Docker, MySQL, PowerShell, or any knowledge of what
is inside.

What it does, in order, and what each step protects:

1. Checks the machine is 64-bit Windows 10/Server 2016 or later, and installs
   the Visual C++ runtime if missing — **MySQL's binaries need it**, and without
   it the database service fails to start with no useful message.
2. Lays down the payload under `C:\Program Files\Barcode Label Printing`.
3. **Looks for an existing MySQL 8**, and asks the binary its version rather
   than trusting the service name — XAMPP and WAMP both register MariaDB as a
   service called `MySQL`, and MariaDB cannot run this schema.
4. Provisions MySQL from the bundled archive if none was found: writes `my.ini`
   **before first start** (so `ngram_token_size`, `local_infile` and
   READ-COMMITTED hold from the first byte written), takes the next free port if
   3306 is occupied, initialises the data directory, registers
   `BarcodePrinterMySQL` and secures the root account.
5. Creates the database and a **restricted account scoped to it**. Root
   credentials go to an ACL'd option file for maintenance, never into
   application configuration.
6. Applies the schema with the migrator as an explicit, logged step, and seeds
   the initial admin **only when the users table is empty**.
7. Creates the service account, grants it "log on as a service", issues a
   self-signed certificate (or **reuses the existing one** — it does not
   regenerate on every run, which would break already-configured clients),
   grants the account read access to its private key, and starts
   `BarcodePrinter.Api` with automatic start.
8. Configures the client to point at the local API and trusts the certificate.
9. **Verifies all of it** — see section 1b — and only then offers Launch.

Re-running the same installer repairs and upgrades in place. The database is
never recreated, and an uninstall keeps it unless data removal is explicitly
requested.

Command-line options, for scripted or GPO deployment:

```powershell
BarcodePrinterSetup.exe /passive                       # progress bar, no prompts
BarcodePrinterSetup.exe /quiet                         # fully silent
BarcodePrinterSetup.exe /quiet HttpsPort=5443          # non-default port
BarcodePrinterSetup.exe /quiet LanSubnet=192.168.10.0/24   # also serve other workstations
BarcodePrinterSetup.exe /repair
BarcodePrinterSetup.exe /uninstall
BarcodePrinterSetup.exe /log C:\temp\install.log       # always worth passing
```

By default the API is **not** exposed to the network at all — no firewall rule
is opened unless `LanSubnet` is given.

---

## 1b. Verifying an installation

The installer runs this itself and refuses to report success unless it passes.
Run it again any time the machine starts misbehaving:

```powershell
& "C:\Program Files\Barcode Label Printing\Test-Installation.ps1" `
    -InstallDir "C:\Program Files\Barcode Label Printing" -Detailed
```

It checks the MySQL service, that the application's own account can reach its
own database, that the schema is current, that the API service runs as the
dedicated account with automatic start, that the HTTPS endpoint answers **with
the certificate that was installed** (not a developer certificate it fell back
to), that the certificate is trusted, that `/health` and `/health/ready` are
healthy, that the client payload is complete, and that the client points at this
API.

Logs, when something is wrong:

| File | What it tells you |
|---|---|
| `C:\ProgramData\BarcodePrinter\logs\install-*.log` | Which phase failed, and why. **Start here.** |
| `C:\ProgramData\BarcodePrinter\logs\mysql-setup.log` | What mysqld actually said |
| `C:\ProgramData\BarcodePrinter\logs\api-*.log` | The running service |
| `%TEMP%\Barcode Label Printing_*_BarcodePrinterApp.log` | The MSI's own log |

The bundle log says only *that* the MSI failed. The install log says *why*.

---

## 0. Before you start

| Need | Why |
|---|---|
| Windows Server, 8 cores / 16 GB / SSD (baseline, pending C-21) | §16 |
| MySQL 8.4 installed, **not** MariaDB | The schema uses MySQL-8 partitioning, `ngram` FULLTEXT and window functions. MariaDB will not run it. |
| A LAN TLS certificate **in `LocalMachine\My`, with its private key** — or `-GenerateSelfSignedCert` for a pilot | HTTPS is enforced outside Development, and the service account cannot see a certificate in your personal store |
| A dedicated service account password | The service must not run as LocalSystem |
| A backup volume **separate from the data volume**, and an off-box target | A backup on the same disk does not survive losing the disk |

Confirm the MySQL flavour before anything else:

```sql
SELECT VERSION();   -- must NOT contain "MariaDB"
```

XAMPP and WAMP both register MariaDB under a Windows service literally named
`MySQL`. Check the version string, not the service name.

The installer runs this check for you and refuses to go further, but you can run
it on its own before you touch the server at all:

```powershell
.\migrator\BarcodePrinter.DbMigrator.exe "Server=127.0.0.1;Port=3306;Database=barcodeprinter;Uid=barcodeprinter;Pwd=<pwd>" --preflight-only
```

It verifies the server is MySQL 8+, and that `ngram_token_size`, `local_infile`
and `transaction_isolation` are right. `ngram_token_size` in particular **cannot
be corrected after the FULLTEXT index is built** without rebuilding it, and a
wrong value produces a product search that silently matches nothing.

---

## 1. Build

On the build machine:

```powershell
.\deploy\Publish.ps1 -Version 1.0.0
```

The test suite runs first and **nothing is published if it fails**. Output lands in `artifacts\`:
`api\`, `migrator\`, `client\`, the scripts, and `build-info.json` (version, commit, who built it).

Copy `artifacts\` to the server.

---

## 2. Configure MySQL

Merge `mysql\barcodeprinter.cnf` into `my.ini` and restart MySQL. Then verify — do not assume:

```sql
SELECT @@transaction_isolation, @@local_infile, @@ngram_token_size,
       @@binlog_format, @@innodb_buffer_pool_size;
```

Expect `READ-COMMITTED`, `1`, `2`, `ROW`, and your configured pool size. `READ-COMMITTED` and
`local_infile` are not preferences: the first prevents deadlocks under concurrent carton
allocation, the second is what the 20k-row import runs on.

Create the application login. It needs no `SUPER`, no `FILE`, and no rights outside its own schema:

```sql
CREATE DATABASE barcodeprinter CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'barcodeprinter'@'127.0.0.1' IDENTIFIED BY '<strong password>';
GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, INDEX, DROP, REFERENCES
  ON barcodeprinter.* TO 'barcodeprinter'@'127.0.0.1';
FLUSH PRIVILEGES;
```

Create the backup credential file so passwords never appear in a command line
(process arguments are visible to every user on the box). Save as
`C:\ProgramData\MySQL\backup.cnf` and restrict it to Administrators and SYSTEM:

```ini
[client]
user=root
password=<root password>
host=127.0.0.1
```

---

## 3. Install the server

With a certificate from your internal CA (already imported into `LocalMachine\My`):

```powershell
.\Install-Server.ps1 `
    -ServiceAccountPassword (Read-Host "Service account password" -AsSecureString) `
    -MySqlPassword          (Read-Host "MySQL app password"       -AsSecureString) `
    -LanSubnet              192.168.10.0/24 `
    -CertThumbprint         A1B2C3...
```

For a pilot or a LAN with no internal CA, let the installer create one:

```powershell
.\Install-Server.ps1 `
    -ServiceAccountPassword (Read-Host "Service account password" -AsSecureString) `
    -MySqlPassword          (Read-Host "MySQL app password"       -AsSecureString) `
    -LanSubnet              192.168.10.0/24 `
    -GenerateSelfSignedCert
```

There is deliberately **no "install without a certificate" path**. HTTPS is enforced outside
Development, so an install with no usable certificate produces a service that registers fine and
then refuses to start.

What it does, and what each step is protecting:

1. **Checks the MySQL server before creating anything** — flavour, version, and the three settings
   that cannot be fixed comfortably later. A failed check installs nothing.
2. Creates `D:\BarcodePrinter\{api,images,imports,logs,backup,keys}`.
3. Creates the low-privilege service account **and grants it "log on as a service"**. Windows does
   not grant this automatically; without it the service registers cleanly and then fails to start
   with error 1069, which reads like a bad password and sends you looking in the wrong place.
4. Resolves the certificate, checks it has a private key and an unambiguous subject, and **grants
   the service account read access to the private key file**.
5. Writes `appsettings.Production.json` with a **freshly generated 512-bit JWT signing key** and a
   Kestrel HTTPS endpoint that **names the certificate by subject and store**.
6. ACLs that file and `keys\` to Administrators, SYSTEM and the service account only — inheritance
   is switched off, so `Users` cannot read the connection string.
7. **Runs the migrator as an explicit, logged step.** The API never migrates at startup, so a
   restart at the wrong moment cannot alter the schema. A failed migration aborts the install and
   the service is not started.
8. Registers the service: **delayed** auto-start, **dependent on the local MySQL service**, restart
   on failure at 5 s / 15 s / 60 s.
9. Opens **only** the API port to the LAN subnet, and warns if anything has opened 3306.
10. Exports the public certificate to `D:\BarcodePrinter\barcodeprinter-lan.cer` for the clients.
11. Starts the service and polls `/health` — the install fails if it does not come up, and prints
    the tail of the log so you can see why.

> **Kestrel does not use `netsh http add sslcert`.** That command binds a certificate for http.sys,
> which this service never touches. The certificate has to be named in `appsettings.Production.json`,
> which is what the installer now writes. It also has to be in **`LocalMachine\My`** — the service
> account has its own, empty, `CurrentUser` store, so a certificate imported into the installing
> administrator's personal store is invisible to the running service.

**Upgrades** are the same command against the new `artifacts\`. The existing
`appsettings.Production.json` is kept, so the signing key is not rotated (rotating it would log
every user out mid-shift). The HTTPS block is refreshed on every run, so an install made before
this fix is repaired by re-running the installer.

---

## 3a. Changing settings afterwards

`Install-Server.ps1` puts the service on the box. `Configure-Server.ps1` is for what changes later —
a rotated database password, a renumbered LAN, the pilot certificate replaced with a real one.

```powershell
.\Configure-Server.ps1 -Show                                            # current settings, secrets redacted
.\Configure-Server.ps1 -MySqlPassword (Read-Host -AsSecureString)       # rotate the DB password
.\Configure-Server.ps1 -CertThumbprint A1B2C3...                        # swap in the CA certificate
.\Configure-Server.ps1 -LanSubnet 192.168.20.0/24 -HttpsPort 5001       # renumbered LAN
.\Configure-Server.ps1 -LogLevel Debug                                  # while chasing a fault
```

New database settings are proven against the real server **before** they become the only ones the
service has. The previous `appsettings.Production.json` is kept until the service comes back
healthy on the new one; if it does not, the old file is put back and the service restarted on it. A
mistyped password should not take the line down until somebody notices.

It deliberately cannot change the JWT signing key or the Data Protection key ring. Rotating the
first logs every user out mid-shift; losing the second makes the stored Oracle password
permanently undecryptable.

---

## 4. Schedule backups

```powershell
.\Register-BackupTasks.ps1 -Destination E:\Backups\BarcodePrinter `
                           -OffboxPath  \\nas\backups\barcodeprinter
```

| Task | Schedule | Contents |
|---|---|---|
| Full | nightly 01:30 | `mysqldump --single-transaction` (consistent, non-blocking), image mirror, `appsettings*.json`, **the Data Protection key ring** |
| Binlog | hourly | closed binary logs, for point-in-time recovery between full backups |

The script runs the full backup once immediately and fails loudly if it does not succeed — a
schedule that has never produced a backup is not a backup.

> **The key ring is not optional.** The Oracle password in `integration_settings` is encrypted
> with it. Restore the database without `keys\` and that password is gone for good.

The application reads `backup\backup-status.json` and shows a dashboard warning when the last
successful full backup is over 48 hours old. It shows **status only** — there is deliberately no
restore button anywhere in the UI. Restoring is a supervised operation, not one click away from a
logged-in admin.

---

## 5. Rehearse the restore — before go-live, not after an incident

```powershell
.\Test-Recovery.ps1 -BackupPath E:\Backups\BarcodePrinter
```

Restores the latest full backup into a scratch database, never the live one, verifies the tables,
users and history came back, and **times the restore**. That measured figure is your real RTO;
until you have it, "RTO ≤ 2 hours" is a hypothesis. The script warns if it exceeds two hours.

Record the result in the deployment log. Repeat after any significant growth in history volume.

---

## 6. Install the clients

On each workstation (silent install works under GPO/Intune):

```powershell
.\Install-Client.ps1 -ApiBaseUrl https://barcodesrv:5001 -CertificateFile .\lan-ca.cer
```

`-CertificateFile` is the CA certificate when you have an internal CA. With `-GenerateSelfSignedCert`
there is no CA, so pass the file the installer exported —
`D:\BarcodePrinter\barcodeprinter-lan.cer` — copied from the server. Without it the workstation does
not trust the server and every request fails on certificate validation.

The URL host must **match a name on the certificate**. `-GenerateSelfSignedCert` issues for the
machine name, its FQDN and `localhost`; connecting by IP address will fail validation.

Writes `%ProgramData%\BarcodePrinter\client.json` containing **only the API URL**. No connection
string, no credentials — there is nothing in it worth stealing. The installer refuses to overwrite
a running client (never interrupt a print run) and checks `/health` before finishing, so an
unreachable server is found by IT now rather than by an operator tomorrow.

### Printers

Configure each printer in **Settings → Printers** to match how it is physically attached:

| Printer | Connection | Dispatch |
|---|---|---|
| Zebra on a Windows queue (USB or shared) | `WindowsRaw` | Client |
| Network Zebra with no Windows queue | `NetworkTcp` | Server |
| Laser / inkjet | `WindowsGraphics` | Client |

A client-dispatched printer needs the owning workstation's application to be **running**. Jobs
sent to it while that PC is off fail with `CLIENT_LOST` after the lease expires. This is correct
behaviour, and it is why the printer list shows the owner workstation.

---

## 7. Verify the installation

- [ ] `https://<server>:5001/health` returns 200 from a workstation, not just the server
- [ ] Log in as the seeded admin and **change the password immediately**
- [ ] Create a product, print one label, confirm the barcode scans off the physical media
- [ ] Reprint that job — output must be byte-identical and reuse the same carton numbers
- [ ] Import a 20k-row workbook: under 25 s, UI responsive throughout
- [ ] A non-admin user gets 403 on an admin endpoint (test the API, not just the hidden button)
- [ ] Dashboard shows today's activity and the printers as healthy
- [ ] `Test-Recovery.ps1` has passed and the restore time is recorded

---

## 8. Recovery

> Work from a copy. Never restore over a live database until you have confirmed the backup is good.

**Database, to the last nightly backup:**

```powershell
Expand-Archive E:\Backups\BarcodePrinter\full\<stamp>\barcodeprinter.sql.zip -DestinationPath C:\restore
Stop-Service BarcodePrinter.Api
mysql --defaults-extra-file=C:\ProgramData\MySQL\backup.cnf -e "DROP DATABASE barcodeprinter; CREATE DATABASE barcodeprinter CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"
mysql --defaults-extra-file=C:\ProgramData\MySQL\backup.cnf barcodeprinter < C:\restore\barcodeprinter.sql
```

**Then roll forward to a point in time** (this is what gets you from "last night" to RPO ≤ 1 h).
The dump was taken with `--source-data=2`, so its header records the binlog file and position it
is consistent with:

```powershell
Select-String -Path C:\restore\barcodeprinter.sql -Pattern "CHANGE MASTER TO" -Context 0,2 | Select-Object -First 1

mysqlbinlog --start-position=<pos> --stop-datetime="2026-08-13 14:00:00" `
    E:\Backups\BarcodePrinter\binlog\mysql-bin.0001* |
    mysql --defaults-extra-file=C:\ProgramData\MySQL\backup.cnf barcodeprinter
```

**Images:**

```powershell
robocopy E:\Backups\BarcodePrinter\images D:\BarcodePrinter\images /MIR
```

Restore these *after* the database if you are pressed for time. A missing image degrades to a
placeholder and **printing keeps working** — so the database is what the line is waiting on.

**Configuration and key ring:**

```powershell
Copy-Item E:\Backups\BarcodePrinter\full\<stamp>\appsettings*.json D:\BarcodePrinter\api\
Copy-Item E:\Backups\BarcodePrinter\full\<stamp>\keys\* D:\BarcodePrinter\keys\ -Recurse
Start-Service BarcodePrinter.Api
```

Restoring `appsettings.Production.json` restores the original JWT signing key, so existing sessions
survive. Restoring a *different* key ring makes the stored Oracle password undecryptable — if that
happens, re-enter it in Settings → Integration.

---

## 9. Routine operations

| When | Do |
|---|---|
| Daily | Check the dashboard for failed jobs and the backup-age warning |
| Weekly | Confirm both scheduled tasks ran with result 0; skim `logs\api-*.log` for errors |
| Monthly | Check disk headroom on the data and backup volumes |
| Quarterly | Re-run `Test-Recovery.ps1`; confirm the restore time still meets RTO |
| **Yearly** | **Add the next 12 monthly partitions** (see below) |

### Yearly maintenance — partitions

`print_jobs`, `print_job_items` and `audit_logs` have 12 monthly partitions pre-created plus a
`pmax` catch-all. Once rows start landing in `pmax` they stop pruning and reports gradually slow
down. There is no error and nothing fails — it just degrades. Add the next year ahead of time:

```sql
ALTER TABLE print_jobs REORGANIZE PARTITION pmax INTO (
  PARTITION p202709 VALUES LESS THAN (TO_DAYS('2027-10-01')),
  PARTITION p202710 VALUES LESS THAN (TO_DAYS('2027-11-01')),
  -- ... twelve months ...
  PARTITION pmax    VALUES LESS THAN MAXVALUE);
```

Repeat for `print_job_items` and `audit_logs`. Run it in a maintenance window: reorganising
rewrites the affected partition.

Check where rows are actually landing:

```sql
SELECT partition_name, table_rows FROM information_schema.partitions
WHERE table_schema = 'barcodeprinter' AND table_name = 'print_jobs'
ORDER BY partition_ordinal_position;
```

---

## 10. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Service will not start, error **1069** | The account lacks "log on as a service" | Re-run `Install-Server.ps1`; it grants the right explicitly |
| Service starts then stops; log says **"No server certificate was specified"** | Certificate missing from `LocalMachine\My`, or an install predating the certificate fix | `.\Configure-Server.ps1 -Show` to see what it is looking for, then re-run `Install-Server.ps1` with `-CertThumbprint` or `-GenerateSelfSignedCert` |
| Log says the certificate could not be **accessed** | The service account cannot read the private key file | Re-run `Install-Server.ps1`, or `.\Configure-Server.ps1 -CertThumbprint <same>` — both grant it |
| Every start at boot fails, then succeeds a minute later | MySQL was not ready yet | Confirm the dependency: `sc.exe qc BarcodePrinter.Api` should list the MySQL service under DEPENDENCIES |
| Migrator refuses to run: **"the server is MariaDB"** | Pointed at XAMPP/WAMP, whose service is *named* MySQL | Install MySQL 8.4 Community and repoint the connection string |
| Service will not start | Bad connection string, or the account cannot read `appsettings.Production.json` | `logs\api-*.log`; re-run `Install-Server.ps1` to reapply ACLs |
| Client: "Cannot reach the server" | Firewall, wrong URL, or untrusted certificate | `Invoke-WebRequest https://server:5001/health` from the workstation |
| Import fails immediately | `local_infile` off | Set it in `my.ini` and restart MySQL |
| Product search finds nothing mid-code | `ngram_token_size` changed after the index was built | Restore it to 2 and rebuild the FULLTEXT index |
| Jobs stuck `Queued` on a USB printer | Owning workstation is off or the client is closed | Start the client there, or reassign the job to another printer |
| Jobs fail `CLIENT_LOST` | Client died mid-job; the lease expired | Reprint — the stored payload replays byte-identically |
| Reports slowing over time | Rows landing in `pmax` | Add partitions (§9) |
| Dashboard warns about backup age | Scheduled task failing | `logs\backup-*.log`, then `Get-ScheduledTaskInfo "BarcodePrinter Full Backup"` |

**Every error dialog shows a correlation ID.** Search the logs for it to get the exact request:

```powershell
Select-String -Path D:\BarcodePrinter\logs\api-*.log -Pattern "<correlation id>"
```

---

## 11. What this deployment deliberately does not do

- **No in-app restore.** Status only. Restore is a supervised operation from this runbook.
- **No automatic migration at startup.** Schema changes are an explicit, logged deployment step.
- **No LAN access to MySQL.** Loopback only; 3306 is never opened.
- **No credentials on the client.** `client.json` holds the API URL and nothing else.
- **No offline printing.** If the server is down, printing stops with a clear banner (B-20).
  This is pending client confirmation — **BQ-5**. Offline support is roughly three additional
  weeks and weakens carton-number continuity.
