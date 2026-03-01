#Requires -RunAsAdministrator

# Get the folder where this script (and the EXE) is currently located
$InstallDir = $PSScriptRoot
$ExePath = Join-Path -Path $InstallDir -ChildPath "PlutoForChannels.exe"

# Define the Task Name
$TaskName = "PlutoForChannels_BackgroundService"

Write-Host "Installing $TaskName..." -ForegroundColor Cyan
Write-Host "Target Executable: $ExePath" -ForegroundColor Gray

# 1. Set the Action (Run the EXE in its current folder)
$Action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $InstallDir

# 2. Set the Trigger (Run immediately on system boot)
$Trigger = New-ScheduledTaskTrigger -AtStartup

# 3. Set the Principal (Run as the hidden SYSTEM account with highest privileges)
$Principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest

# 4. Set the Settings (Never stop the task, allow running on battery)
$Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0

# 5. Register the Task
Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Principal $Principal -Settings $Settings -Force

Write-Host ""
Write-Host "Success! The proxy server will now run silently in the background every time the PC boots." -ForegroundColor Green
Write-Host "Note: Do not use the 'Run at Startup' toggle inside the app's tray menu if you use this service." -ForegroundColor Yellow
Write-Host "You can view or remove this task in the Windows Task Scheduler." -ForegroundColor Yellow
Pause