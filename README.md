# Pluto Proxy (Linux Proxy)

A lightweight, headless Linux daemon that bridges Pluto TV and Channels DVR. 

This application acts as a local proxy, generating dynamic M3U playlists and EPG (Electronic Program Guide) XML data to seamlessly integrate Pluto TV into your Channels DVR setup. It is built as a highly optimized, single-file executable specifically designed for Linux environments. Username and Password fields have been added to the dashboard to work with the latest changes to the Pluto API.

## Features
* **Self-Contained:** No messy dependencies or external web servers required.
* **Web Dashboard:** Manage your Pluto credentials and active regions via a built-in, responsive web interface.
* **Auto-Installer:** Automatically configures itself as a `systemd` background service and binds to your true LAN IP.
* **Desktop Integration:** Extracts an icon and creates a clickable desktop shortcut for easy dashboard access on Ubuntu/GNOME desktop environments.

---

## Installation

Because this application installs itself as a permanent background system service, you must place the downloaded file in the exact folder where you want it to live forever before running the installer. [Linux Release](https://github.com/nuken/Pluto-Windows_4C/releases/tag/v1.2.0-linux)

1. Download the compiled `PlutoForChannels` Linux binary.
2. Move the file to your preferred permanent location (e.g., create a folder on your Desktop or in your Home directory and put the file inside).
3. Right-click inside that folder and select **Open in Terminal**.
4. Run the following command to make the file executable:
```bash
   chmod +x PlutoForChannels

```

5. Run the installer with root privileges:

```bash
   sudo ./PlutoForChannels --install

```

6. The terminal will output the LAN IP address and Port assigned to your dashboard (the default port is 7777). Open this address in your web browser, select your regions, and click **Save Global Settings** to generate your M3U and EPG links.



### Adding the Dashboard to your Desktop

During installation, the app creates a file named `Pluto Dashboard.desktop` in the same folder. To use this as a quick shortcut:

1. Drag and drop the `Pluto Dashboard.desktop` file onto your Ubuntu Desktop background.


2. Right-click the file on your Desktop and select **Allow Launching**.
3. The file will transform into a clickable icon. Double-click it anytime to open the management dashboard.

---

## Setup in Channels DVR

1. Open the Pluto Proxy dashboard.
2. Select the regions you wish to use.
3. In your **Channels DVR Web Admin**, go to **Settings** > **Sources** > **Add Source** > **Custom Channels**.


4. **M3U URL**: Copy the M3U link from the PlutoForChannels dashboard.


5. **XMLTV URL**: Copy the EPG link from the dashboard.


6. Set the format to MPEG-TS and **Refresh Interval** to "6 hours".



---

## Managing the Service

You can manage the background service at any time using standard systemd terminal commands:

**Check if the service is running:**

```bash
sudo systemctl status plutoforchannels

```

**Restart the service:**

```bash
sudo systemctl restart plutoforchannels

```

**Stop the service (required before updating/reinstalling):**

```bash
sudo systemctl stop plutoforchannels

```
