Kumi Incremental Backup - Windows Package

Install
-------
Open PowerShell as Administrator from this folder and run:

  .\install.ps1

To install and create a Windows Scheduled Task that starts the app at boot:

  .\install.ps1 -CreateScheduledTask

The app is installed to:

  C:\Program Files\KumiIncrementalbackUp

Configuration
-------------
Edit appsettings.json in the install folder to set:

  Backup:SourceUsbDrive
  CosmosDb settings
  AzureFileShare settings
  Schedule settings

Uninstall
---------
Open PowerShell as Administrator from this folder and run:

  .\uninstall.ps1 -RemoveScheduledTask

Run Manually
------------
After installation:

  "C:\Program Files\KumiIncrementalbackUp\KumiIncrementalbackUp.exe"

Run with the built-in scheduler:

  "C:\Program Files\KumiIncrementalbackUp\KumiIncrementalbackUp.exe" --schedule
