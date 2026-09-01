# ScrollCapture single-file release build (win-x64, self-contained, OpenCV natives embedded)
param(
    [string]$Output = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet publish src/ScrollCapture/ScrollCapture.csproj -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:EnableCompressionInSingleFile=true `
    -o $Output

Write-Host ""
Write-Host "== Release ==" 
Get-Item "$Output\ScrollCapture.exe" | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
Write-Host "Done: $Output\ScrollCapture.exe"
