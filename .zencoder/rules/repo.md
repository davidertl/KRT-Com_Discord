---
description: Repository Information Overview
alwaysApply: true
---

# KRT-Com (das-krt) Information

## Summary
KRT-Com (das-krt) is a radio-like communication solution for Discord, inspired by classic TeamSpeak radio plugins. It enables users to communicate across multiple groups simultaneously without switching voice channels, focusing on realistic radio logic and Opus audio relay.

## Structure
- **comp/**: Contains the Windows-based Companion App.
- **server/**: Contains installation, service management scripts, and the embedded backend source code.
- **docs/**: Project documentation including architecture, requirements, and privacy policy.

### Main Repository Components
- **Companion App**: A .NET 10 WPF application providing the user interface for radio control, Push-To-Talk (PTT), and audio capture/playback.
- **das-krt Backend**: A Node.js service (bundled in `install.sh`) that handles Discord bot integration, WebSocket-based audio relay, SQLite persistence, and OAuth2 authentication.
- **Operations Support**: Shell scripts for automated installation (`install.sh`), service management (`service.sh`), and Traefik reverse proxy configuration.

## Projects

### Companion App
**Configuration File**: [./comp/CompanionApp/CompanionApp.csproj](./comp/CompanionApp/CompanionApp.csproj)

#### Language & Runtime
**Language**: C#  
**Version**: .NET 10.0-windows  
**Build System**: MSBuild / dotnet CLI  
**Package Manager**: NuGet

#### Dependencies
**Main Dependencies**:
- **NAudio** (v2.2.1): Audio capture and playback.
- **Concentus** (v1.1.7): Opus audio codec implementation.
- **NAudio.Wasapi**: Windows Audio Session API support.

#### Build & Installation
```bash
# Build using dotnet CLI
dotnet build comp/CompanionApp/CompanionApp.csproj

# Or open in Visual Studio 2022
# comp/CompanionApp.sln
```

#### Main Files & Resources
- **Entry Point**: [./comp/CompanionApp/App.xaml.cs](./comp/CompanionApp/App.xaml.cs)
- **Main UI**: [./comp/CompanionApp/MainWindow.xaml](./comp/CompanionApp/MainWindow.xaml)
- **Configuration**: `%APPDATA%\das-KRT_com\config.json`

---

### das-krt Backend
**Note**: The backend source code is bundled within the `server/install.sh` script and extracted during deployment to `/opt/das-krt/backend`.

#### Language & Runtime
**Language**: Node.js  
**Version**: 24  
**Package Manager**: npm

#### Dependencies
**Main Dependencies**:
- **discord.js**: Discord API integration.
- **express**: REST API framework.
- **ws**: WebSocket support for audio relay.
- **better-sqlite3**: SQLite database with WAL support.
- **helmet/cors**: Security and cross-origin resource sharing.
- **express-rate-limit**: API rate limiting.

#### Build & Installation
The backend is installed and configured via the `install.sh` script on a Debian-based system.
```bash
# Installation on a clean Debian/Ubuntu server
sudo bash server/install.sh
```

#### Usage & Operations
The `service.sh` script provides a comprehensive CLI for managing the backend service.
```bash
# Start/Stop/Restart service
bash server/service.sh start
bash server/service.sh stop
bash server/service.sh restart

# Access management menu
bash server/service.sh menu
```

#### Infrastructure
- **Reverse Proxy**: Traefik (v3.3.3) with TLS termination via Let's Encrypt.
- **Database**: SQLite (WAL mode).
- **Service Management**: systemd (`das-krt-backend.service`).

#### Validation
- **Healthcheck**: `GET /health` endpoint (checked via `service.sh status`).
- **Logs**: Integrated logging via `service.sh logs`.
- **DSGVO**: Compliance module for data cleanup and user-id hashing.
