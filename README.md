# PlutoForChannels-Windows

A native Windows bridge for integrating Pluto TV channels into Channels DVR. This application replaces the need for Docker by providing a standalone `.exe` that runs in your system tray, manages M3U playlists, and automatically updates EPG data.

## Features

* **Native Windows App**: No Docker or WSL required.
* **System Tray Integration**: Runs silently in the background with a "Minimize to Tray" feature.
* **Region Selection**: Choose specifically which Pluto TV regions (US, CA, UK, etc.) you want to pull feeds from.
* **Automated EPG**: Background scheduler automatically generates and refreshes XMLTV files every 2 hours.
* **Dynamic Links**: Dashboard provides copyable links for M3U and EPG feeds based on your selected regions.
* **Settings Persistence**: Remembers your selected regions between restarts via a local configuration file.

## Installation

1. Download the latest `PlutoForChannels.exe` from the **Releases** section.
2. Move the `.exe` to a folder of your choice (e.g., `C:\PlutoForChannels`).
3. Run the application. Windows may show a "SmartScreen" warning; click "More Info" and "Run Anyway."

## Setup in Channels DVR

1. Open the PlutoForChannels dashboard from your system tray.
2. Select the regions you wish to use (e.g., "us_east", "ca").
3. In your **Channels DVR Web Admin**, go to **Settings** > **Sources** > **Add Source** > **Custom Channels**.
4. **M3U URL**: Copy the M3U link from the PlutoForChannels dashboard (e.g., `http://localhost:7777/pluto/all/playlist.m3u`).
5. **XMLTV URL**: Copy the EPG link from the dashboard (e.g., `http://localhost:7777/pluto/epg/all/epg-all.xml`).
6. Set the **Refresh Interval** to "6 hours."

## Build from Source

If you want to build the executable yourself using the .NET 8 SDK:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:ApplicationIcon=icon.ico

```

## How it Works

This application acts as a middleware bridge. It fetches channel metadata from Pluto TV's API and translates it into the M3U and XMLTV formats that Channels DVR expects. When you play a channel, the app provides a redirect to the official Pluto TV stream with the necessary session tokens and device parameters.
