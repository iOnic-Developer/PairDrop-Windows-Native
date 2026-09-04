$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "Building PairDrop Native..." -ForegroundColor Cyan
Write-Host ""

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host ".NET 8 SDK is not installed." -ForegroundColor Yellow
    Write-Host "Install it with:"
    Write-Host "  winget install Microsoft.DotNet.SDK.8" -ForegroundColor Cyan
    exit 1
}

Get-ChildItem -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

dotnet restore

dotnet publish .\PairDropNative.csproj `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o .\publish

Write-Host ""
Write-Host "Built successfully:" -ForegroundColor Green
Write-Host "  $PWD\publish\PairDropNative.exe" -ForegroundColor Cyan
Write-Host ""
Write-Host "Run the EXE. On first launch, enter your PairDrop HTTPS URL." -ForegroundColor Gray
