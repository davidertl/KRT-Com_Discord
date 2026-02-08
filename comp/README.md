# Companion App

This folder contains the Windows companion app for the das-KRT system.

## Build

- Open the solution: `comp/CompanionApp.sln`
- Build in Visual Studio 2022 or `dotnet build comp/CompanionApp/CompanionApp.csproj`

## Runtime config

The app stores config at:

`%APPDATA%\das-KRT_com\config.json`

Open the config folder from the UI or edit the JSON directly.

## Audio transport

Audio is routed via Mumble. Clients connect directly to the Mumble server and join the channel named by the 4-digit freqId.
