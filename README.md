# Pluto for Channels (Linux Proxy)

A lightweight, headless Linux daemon that bridges Pluto TV and Channels DVR. 

This application acts as a local proxy, generating dynamic M3U playlists and EPG (Electronic Program Guide) XML data to seamlessly integrate Pluto TV into your Channels DVR setup. It is built as a highly optimized, single-file executable specifically designed for Linux environments.

## Features
* **Self-Contained:** No messy dependencies or external web servers required.
* **Web Dashboard:** Manage your Pluto credentials and active regions via a built-in, responsive web interface.
* **Auto-Installer:** Automatically configures itself as a systemd background service and binds to your true LAN IP.
* **Desktop Integration:** Extracts an icon and creates a clickable desktop shortcut for easy dashboard access on Ubuntu/GNOME desktop environments.

---

## Installation

Download the compiled PlutoForChannels Linux binary to your machine. 

### Option A: Install as a Background Service (Recommended)
This is the best approach for a dedicated media server. It will run silently in the background and start automatically when your computer boots.

1. Open your terminal and navigate to the folder containing the downloaded file.
2. Make the file executable:
   chmod +x PlutoForChannels
3. Run the automated installer with root privileges:
   sudo ./PlutoForChannels --install
4. The terminal will output the LAN IP address and Port assigned to your dashboard (e.g., http://192.168.1.50:7777). Open this address in your web browser, select your regions, and click **Save Global Settings** to generate your M3U and EPG links.

### Option B: Run Interactively (Desktop Mode)
If you are using a Linux Desktop environment (like Ubuntu) and just want to test the app without installing a permanent service:
* Simply double-click the PlutoForChannels executable. It will start the server and automatically pop open your default web browser to the dashboard. *(Note: The proxy will stop running if you close your terminal or log out).*

---

## Managing the Service

If you installed the application as a background service (Option A), you can manage it using standard systemd commands:

**Check if the service is running:**
sudo systemctl status plutoforchannels

**Restart the service:**
sudo systemctl restart plutoforchannels

**Stop the service:**
sudo systemctl stop plutoforchannels
