param(
    [string]$Version = "1.3.2",
    [string]$DotnetPath = "dotnet"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$publishPath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "publish\win-x64"))
$zipPath = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot "Huaci-$Version-win-x64-portable.zip"))

if (-not $publishPath.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish path must remain under artifacts."
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

& $DotnetPath publish (Join-Path $repoRoot "src\Huaci.App\Huaci.App.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --output $publishPath `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

Compress-Archive -Path (Join-Path $publishPath "*") -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Portable package created: $zipPath"
