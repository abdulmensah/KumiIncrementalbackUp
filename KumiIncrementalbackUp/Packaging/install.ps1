param(
    [string]$InstallPath = "$env:ProgramFiles\KumiIncrementalbackUp",
    [switch]$CreateScheduledTask,
    [string]$TaskName = "Kumi Incremental Backup",
    [string]$TaskArguments = "--schedule"
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this installer from an elevated PowerShell session."
    }
}

Assert-Administrator

$sourcePath = Split-Path -Parent $MyInvocation.MyCommand.Path
$exePath = Join-Path $InstallPath "KumiIncrementalbackUp.exe"

Write-Host "Installing Kumi Incremental Backup to $InstallPath"
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null

Get-ChildItem -Path $sourcePath -File |
    Where-Object { $_.Name -notin @("install.ps1", "uninstall.ps1", "README.txt") } |
    Copy-Item -Destination $InstallPath -Force

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Install failed because $exePath was not found after copying files."
}

if ($CreateScheduledTask) {
    Write-Host "Creating scheduled task '$TaskName'"

    $action = New-ScheduledTaskAction -Execute $exePath -Argument $TaskArguments -WorkingDirectory $InstallPath
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Force | Out-Null

    Write-Host "Scheduled task created. It will start at Windows startup."
}

Write-Host "Installation complete."
Write-Host "Config file: $(Join-Path $InstallPath 'appsettings.json')"
