param(
    [string]$InstallPath = "$env:ProgramFiles\KumiIncrementalbackUp",
    [string]$TaskName = "Kumi Incremental Backup",
    [switch]$RemoveScheduledTask
)

$ErrorActionPreference = "Stop"

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)

    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this uninstaller from an elevated PowerShell session."
    }
}

Assert-Administrator

if ($RemoveScheduledTask -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
    Write-Host "Removing scheduled task '$TaskName'"
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

if (Test-Path -LiteralPath $InstallPath) {
    Write-Host "Removing $InstallPath"
    Remove-Item -LiteralPath $InstallPath -Recurse -Force
}

Write-Host "Uninstall complete."
