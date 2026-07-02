# Builds all projects (net48/x64) and stages the plugin + native deps into app\plugins.
param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dotnet = "dotnet"

Write-Host "Building GameHelper ($Configuration)..." -ForegroundColor Cyan
& $dotnet build "$root\src\GameHelper.slnx" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$pluginOut = "$root\src\GameHelper.Plugin\bin\$Configuration"
$dest = "$root\app\plugins"
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Write-Host "Staging plugin -> app\plugins" -ForegroundColor Cyan
Copy-Item "$pluginOut\*.dll" $dest -Force
foreach ($native in @("x64","amd64")) {
  if (Test-Path "$pluginOut\$native") {
    Copy-Item "$pluginOut\$native" $dest -Recurse -Force
  }
}

# Unblock DLLs (Windows marks downloaded/native DLLs; Overwolf refuses blocked ones).
Get-ChildItem $dest -Recurse -Include *.dll | Unblock-File

Write-Host "Done. Load app\ as an unpacked extension in Overwolf (Settings > Support > Development)." -ForegroundColor Green
