# Dev cycle: stop the agent task, rebuild, restart it. One command so it can be allowlisted
# (instead of ad-hoc compound scripts that permission rules can't match).
#   pwsh wz-helper/tools/dev.ps1            # build + restart agent
#   pwsh wz-helper/tools/dev.ps1 -NoRestart # just build
param([switch]$NoRestart, [string]$Configuration = "Release")

$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
$task = "WarzoneHelper-Console"
$dotnet = "C:\Program Files\dotnet\dotnet.exe"

try { Stop-ScheduledTask -TaskName $task -ErrorAction Stop | Out-Null } catch {}
Get-Process WarzoneHelper.Console -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

& $dotnet build "$root\src\WarzoneHelper.slnx" -c $Configuration 2>&1 | Select-Object -Last 4
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit 1 }

if (-not $NoRestart) {
  Start-ScheduledTask -TaskName $task
  Write-Host "agent restarted."
}
