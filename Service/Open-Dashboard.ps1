#Requires -RunAsAdministrator

$TaskName = "PlutoForChannels_BackgroundService"
$InstallDir = $PSScriptRoot
$ExePath = Join-Path -Path $InstallDir -ChildPath "PlutoForChannels.exe"

# 1. Stop the hidden background server
Write-Host "Stopping headless background server..." -ForegroundColor Yellow
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

# Give it a second to release the port
Start-Sleep -Seconds 2 

# 2. Launch the normal Desktop UI and wait for the user to close it
Write-Host "Opening Dashboard..." -ForegroundColor Cyan
Write-Host "IMPORTANT: Clicking 'X' only minimizes the app to the system tray!" -ForegroundColor Red
Write-Host "To finish saving and restart the background service, you MUST right-click the System Tray icon and click 'Quit Server'." -ForegroundColor Red

Start-Process -FilePath $ExePath -WorkingDirectory $InstallDir -Wait

# 3. Restart the hidden background server
Write-Host "Dashboard completely closed. Restarting headless background server..." -ForegroundColor Yellow
Start-ScheduledTask -TaskName $TaskName

Write-Host "Success! Server is running in the background again." -ForegroundColor Green
Start-Sleep -Seconds 3