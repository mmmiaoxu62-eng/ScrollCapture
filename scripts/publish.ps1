# ScrollCapture single-file release build (win-x64, self-contained, OpenCV natives embedded)
param(
    [string]$Output = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# A running ScrollCapture locks the target exe and silently makes publish a no-op.
$running = Get-Process -Name "ScrollCapture" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping running instance(s): $($running.Id -join ', ')"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$before = if (Test-Path "$Output\ScrollCapture.exe") { (Get-Item "$Output\ScrollCapture.exe").LastWriteTime } else { $null }

dotnet publish src/ScrollCapture/ScrollCapture.csproj -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:EnableCompressionInSingleFile=true `
    -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Get-Item "$Output\ScrollCapture.exe"
$after = $exe.LastWriteTime
if ($before -and $after -le $before) {
    throw "publish did NOT update the output file (still $after)"
}

Write-Host ""
Write-Host "== Release =="
$exe | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}, LastWriteTime
Write-Host "Done: $Output\ScrollCapture.exe"
