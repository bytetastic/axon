# Baut Axon als einzelne, eigenständige Axon.exe nach dist\ (braucht kein installiertes .NET).
# Aufruf:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host 'Tests ...'
dotnet run --project (Join-Path $root 'tests\Axon.Tests') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests fehlgeschlagen' }

Write-Host 'Veroeffentlichen ...'
$out = Join-Path $root 'src\Axon\bin\publish'
dotnet publish (Join-Path $root 'src\Axon\Axon.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:GenerateDocumentationFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'Veroeffentlichen fehlgeschlagen' }

New-Item -ItemType Directory -Force $dist | Out-Null
$exe = Join-Path $dist 'Axon.exe'
try {
    Copy-Item (Join-Path $out 'Axon.exe') $exe -Force -ErrorAction Stop
} catch {
    # Laeuft Axon gerade aus dist\, ist die Datei gesperrt – dann die neue Version daneben ablegen.
    $exe = Join-Path $dist 'Axon-neu.exe'
    Copy-Item (Join-Path $out 'Axon.exe') $exe -Force
    Write-Host 'Hinweis: dist\Axon.exe ist in Benutzung (Axon laeuft). Neue Version liegt als dist\Axon-neu.exe daneben.'
}
$mb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Fertig: $exe ($mb MB)"
