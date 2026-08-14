# Enterprise Barcode Label Printing System

[![NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/Client-WPF_Desktop-0078D4?logo=windows&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/)
[![ASP.NET Core](https://img.shields.io/badge/Backend-ASP.NET_Core_10-512BD4?logo=dotnet&logoColor=white)](https://asp.net/)
[![MySQL 8.4](https://img.shields.io/badge/Database-MySQL_8.4-4479A1?logo=mysql&logoColor=white)](https://www.mysql.com/)
[![License](https://img.shields.io/badge/License-Proprietary-red.svg)]()

High-performance, enterprise-grade Barcode Label Printing & Warehouse Dispatch System built on **.NET 10**. Designed for mission-critical warehouse, inventory, and packaging workflows requiring high-throughput barcode generation, real-time print spooler management, bulk data ingestion, and enterprise security.

---

## 📋 Table of Contents

- [Overview](#-overview)
- [Key Features](#-key-features)
- [System Architecture](#-system-architecture)
- [Technology Stack](#-technology-stack)
- [Repository Structure](#-repository-structure)
- [Prerequisites](#-prerequisites)
- [Getting Started](#-getting-started)
- [Database Setup & Migrations](#-database-setup--migrations)
- [Print Dispatch Subsystem](#-print-dispatch-subsystem)
- [Testing & Quality Assurance](#-testing--quality-assurance)
- [Deployment & Production Runbook](#-deployment--production-runbook)
- [Security & Compliance](#-security--compliance)

---

## 🎯 Overview

The **Barcode Label Printing System** provides a complete end-to-end solution for generating, previewing, and dispatching barcode labels across enterprise warehouse environments. The system supports direct network TCP printing (ZPL/TSPL) and local Windows spooler raw printing, with real-time job status broadcasting over WebSockets.

### High-Level Workflow

```
 ┌────────────────┐          HTTPS / REST API           ┌──────────────────────┐
 │                ├────────────────────────────────────►│                      │
 │   WPF Client   │                                     │  ASP.NET Core 10     │
 │  (Desktop UI)  │◄────────────────────────────────────┤  Web API & SignalR   │
 └───────┬────────┘        SignalR (WebSockets)         └──────────┬───────────┘
         │                                                         │
         │ Direct Windows Spooler                                  │ EF Core 9 / Dapper
         ▼                                                         ▼
 ┌────────────────┐                                     ┌──────────────────────┐
 │  Local Printer │                                     │      MySQL 8.4       │
 │   (USB / RAW)  │                                     │      Database        │
 └────────────────┘                                     └──────────────────────┘
         ▲                                                         │
         │                 Direct TCP / RAW Print                  │
         └─────────────────────────────────────────────────────────┘
```

---

## ✨ Key Features

### 🖨️ Barcode & Label Generation Engine
- **High-Performance Rendering**: Built-in rendering engine utilizing **ZXing.Net** and **SkiaSharp** for sub-millisecond barcode generation (Code 128, QR Code, DataMatrix, EAN-13).
- **ZPL & Vector Support**: Native support for Zebra Programming Language (ZPL) templates and crisp raster/vector label previews.
- **Dynamic Layout Templates**: Flexible template builder supporting customizable field placements, serial number auto-incrementing, and batch carton distribution.

### ⚡ Real-Time Print Dispatch Subsystem
- **Dual Dispatch Modes**:
  - **Server Direct (Network TCP)**: Pushes raw ZPL payloads straight to industrial network printers without requiring client PCs to be powered on.
  - **Client Spooler (WindowsRaw)**: Routes print streams through local Windows printer spools (USB/Shared queues) managed by workstation clients.
- **Job Lease & Resiliency**: Built-in `PrintDispatchWorker` and `PrintLeaseWatchdog` background services handle job retries, workstation timeouts, and prevent duplicate carton allocations.
- **Live Status Broadcaster**: Real-time print job status updates (`Queued`, `Leased`, `Printing`, `Completed`, `Failed`) pushed instantly to connected UI clients over SignalR.

### 📊 Ingestion & Bulk Operations
- **High-Throughput Excel Ingestion**: Stream-based Excel processing powered by **MiniExcel** capable of ingesting and validating **20,000+ product rows under 25 seconds**.
- **Real-Time Progress Tracking**: SignalR progress channel providing live row-by-row progress bars, validation errors, and completion summaries.

### 🛡️ Enterprise Security & Governance
- **JWT Authentication**: Secure token-based authentication featuring rotating refresh tokens and security stamp revocation (instant session invalidation upon password reset).
- **Fine-Grained RBAC**: Permission-driven authorization enforced at endpoint and SignalR hub level.
- **Audit Logging**: Mandatory immutable audit trails for print operations, master data edits, user role changes, and system configuration adjustments.
- **Rate Limiting**: IP-based rate limiting on authentication endpoints to safeguard against brute-force attacks.

---

## 🛠️ Technology Stack

| Layer | Technologies & Libraries |
|---|---|
| **Client UI** | .NET 10 WPF, CommunityToolkit.Mvvm 8.4, SignalR Client |
| **API Host** | ASP.NET Core 10 (Windows Service / Console), Kestrel HTTPS |
| **Data Access** | Entity Framework Core 9.0, Pomelo MySQL, Dapper 2.1, DbUp 6.1 |
| **Database** | MySQL Server 8.4 Community Edition |
| **Barcodes & Images** | ZXing.Net 0.16, SkiaSharp 4.151, System.Drawing.Common |
| **Data Ingestion** | MiniExcel 1.45, ClosedXML 0.105 |
| **Security & Auth** | ASP.NET Core Identity Core, JWT Bearer, Polly 8.7, FluentValidation 12.1 |
| **Logging** | Serilog 10.0 (Console, File Sinks, Custom Secret Redaction) |
| **Testing** | xUnit 2.9, FluentAssertions 8.10, NSubstitute 6.2, Testcontainers MySQL 4.13, Xunit.StaFact |

---

## 📁 Repository Structure

```
Barcode Printer/
├── BarcodePrinter.slnx          # Visual Studio / .NET Solution File
├── Directory.Build.props        # Centralized MSBuild properties & metadata
├── Directory.Packages.props     # Central Package Management (CPM) versions
├── deploy/                      # Production deployment scripts & configuration templates
│   ├── Install-Server.ps1       # Server installation & Windows service registration script
│   ├── Install-Client.ps1       # Workstation client installation script
│   ├── Configure-Server.ps1     # Post-install server configuration management
│   ├── Backup-BarcodePrinter.ps1# Database & asset backup automation script
│   ├── RUNBOOK.md               # Operations & disaster recovery runbook
│   └── mysql/                   # MySQL configuration templates (barcodeprinter.cnf)
├── src/
│   ├── client/                  # Client desktop applications & libraries
│   │   ├── BarcodePrinter.Wpf/                # WPF Desktop UI Application
│   │   ├── BarcodePrinter.Client.Core/        # Client API services & session manager
│   │   └── BarcodePrinter.Printing.Client/    # Client raw spooler & hardware transport
│   ├── server/                  # Backend services & Clean Architecture layers
│   │   ├── BarcodePrinter.Api/                # ASP.NET Core Web API & SignalR Hubs
│   │   ├── BarcodePrinter.Application/        # Application logic, Use Cases, DTOs
│   │   ├── BarcodePrinter.Domain/             # Domain entities, value objects, domain events
│   │   ├── BarcodePrinter.Infrastructure/     # EF Core repositories, auth, DbUp scripts
│   │   ├── BarcodePrinter.Integration.Oracle/ # Oracle ERP integration service
│   │   ├── BarcodePrinter.Labels/             # Label rendering engine & ZPL generators
│   │   ├── BarcodePrinter.Printing.Abstractions/# Transport & Queue Interfaces
│   │   └── BarcodePrinter.Printing.Server/    # Server raw print transports & spooler
│   ├── shared/
│   │   └── BarcodePrinter.Contracts/          # Shared DTOs, API routes, permission codes
│   └── tools/
│       └── BarcodePrinter.DbMigrator/         # Console tool for DbUp schema migrations
└── tests/                       # Unit, integration, and UI test suites
    ├── BarcodePrinter.Application.Tests/
    ├── BarcodePrinter.Domain.Tests/
    ├── BarcodePrinter.Integration.Tests/
    ├── BarcodePrinter.Labels.Tests/
    ├── BarcodePrinter.Printing.Tests/
    └── BarcodePrinter.Wpf.ViewModels.Tests/
```

---

## ⚙️ Prerequisites

Before developing or hosting the solution, ensure the following prerequisites are installed:

1. **Development Environment**:
   - [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
   - Visual Studio 2022 / VS Code / JetBrains Rider

2. **Database Engine**:
   - **MySQL Server 8.4 Community Edition** (*Note: MariaDB is not supported due to MySQL-8 specific partitioning, window functions, and `ngram` FULLTEXT indexing*).

3. **Client Workstation Requirements**:
   - Windows 10 / 11 (64-bit)
   - Network connectivity to the server API (`https://<server-ip>:5001`)

---

## 🚀 Getting Started

### 1. Clone & Restore

```bash
# Clone repository
git clone https://github.com/your-org/barcode-printer.git
cd "barcode-printer"

# Restore dependencies
dotnet restore
```

### 2. Configure Database & Migrations

Create the database schema in your local MySQL instance:

```sql
CREATE DATABASE barcodeprinter CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER 'barcodeprinter'@'127.0.0.1' IDENTIFIED BY 'LocalDevPassword123!';
GRANT ALL PRIVILEGES ON barcodeprinter.* TO 'barcodeprinter'@'127.0.0.1';
FLUSH PRIVILEGES;
```

Run the database migrator tool to apply all schema scripts and reference seed data:

```bash
dotnet run --project src/tools/BarcodePrinter.DbMigrator -- "Server=127.0.0.1;Port=3306;Database=barcodeprinter;Uid=barcodeprinter;Pwd=LocalDevPassword123!"
```

### 3. Run the Backend API

```bash
dotnet run --project src/server/BarcodePrinter.Api
```
The API server will launch and start listening for connections on `https://localhost:5001`. You can test health endpoints at `https://localhost:5001/health`.

### 4. Run the WPF Desktop Client

```bash
dotnet run --project src/client/BarcodePrinter.Wpf
```

Default seeded credentials for initial access:
- **Username**: `admin`
- **Password**: `Admin123!` *(You will be prompted to change this password on first login)*

---

## 🧪 Testing & Quality Assurance

The codebase includes comprehensive unit, integration, and UI ViewModel tests.

```bash
# Run all tests in the solution
dotnet test

# Run specific test project (e.g., ViewModel unit tests)
dotnet test tests/BarcodePrinter.Wpf.ViewModels.Tests/BarcodePrinter.Wpf.ViewModels.Tests.csproj

# Run API & Integration tests
dotnet test tests/BarcodePrinter.Integration.Tests/BarcodePrinter.Integration.Tests.csproj
```

---

## 📦 Deployment & Production Runbook

Automated PowerShell deployment scripts are located in the `deploy/` folder.

### Publishing Build Artifacts

```powershell
.\deploy\Publish.ps1 -Version 1.0.0
```
This runs the full test suite and packages production binaries into `artifacts/`:
- `artifacts/api/` (ASP.NET Core backend)
- `artifacts/migrator/` (DbUp migration tool)
- `artifacts/client/` (WPF Desktop application)

### Server Installation

Run on the target application server as Administrator:

```powershell
.\deploy\Install-Server.ps1 `
    -ServiceAccountPassword (Read-Host "Service account password" -AsSecureString) `
    -MySqlPassword          (Read-Host "MySQL password"           -AsSecureString) `
    -LanSubnet              192.168.10.0/24 `
    -GenerateSelfSignedCert
```

### Client Installation

Run on each workstation client:

```powershell
.\deploy\Install-Client.ps1 -ApiBaseUrl https://barcodesrv:5001 -CertificateFile .\lan-ca.cer
```

> 📖 For detailed operational procedures, database recovery steps, backup scheduling, and troubleshooting, consult the [Deployment Runbook (`deploy/RUNBOOK.md`)](deploy/RUNBOOK.md).

---

## 🔐 Security Best Practices

- **Enforced HTTPS**: Plain HTTP is automatically redirected to HTTPS outside Development.
- **Zero Plain-Text Credentials**: Client configuration files (`client.json`) store only the API base URL. No connection strings or secrets reside on client machines.
- **Input Sanitization**: Strict input validation using FluentValidation.
- **Log Masking**: Custom Serilog `SecretRedactionPolicy` prevents passwords or tokens from entering log files.

---

## 📄 License

This software is proprietary and confidential. Unauthorized copying, distribution, or usage is strictly prohibited. All rights reserved.
