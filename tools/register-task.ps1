# Registers the elevated background agent as the "GameHelper-Console" scheduled task.
# Run this ONCE from an ADMINISTRATOR PowerShell (RunLevel Highest requires elevation).
#   pwsh -File tools\register-task.ps1
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root "src\GameHelper.Console\bin\$Configuration\net48\GameHelper.Console.exe"
if (-not (Test-Path $exe)) { throw "Build first ($Configuration): $exe not found. Run .\build.ps1" }

# Remove the old (renamed) task if present, then register the new one.
Unregister-ScheduledTask -TaskName "WarzoneHelper-Console" -Confirm:$false -ErrorAction SilentlyContinue

$action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName "GameHelper-Console" -Action $action -Principal $principal -Force | Out-Null

Start-ScheduledTask -TaskName "GameHelper-Console"
Write-Host "Registered + started GameHelper-Console -> $exe" -ForegroundColor Green
