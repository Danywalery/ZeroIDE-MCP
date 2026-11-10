# Build ZeroIDE-MCP.exe into tray\build\
# Requires the .NET 8 SDK (see docs/BUILDING.md).
$ErrorActionPreference = "Stop"
Push-Location $PSScriptRoot
try {
    dotnet publish ZeroIDE-MCP.csproj -c Release -o build
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    Write-Host "Built: $(Join-Path $PSScriptRoot 'build\ZeroIDE-MCP.exe')"
} finally {
    Pop-Location
}
