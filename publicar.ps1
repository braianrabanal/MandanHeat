# Genera release\MandanHeat.exe: un único ejecutable que funciona sin instalar .NET.
# Uso:  powershell -ExecutionPolicy Bypass -File .\publicar.ps1
param([string]$Out = (Join-Path $PSScriptRoot "release"))

$ErrorActionPreference = "Stop"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }

dotnet publish (Join-Path $PSScriptRoot "src\MandanHeat") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $Out

$exe = Join-Path $Out "MandanHeat.exe"
$zip = Join-Path $Out "MandanHeat-win-x64.zip"
Compress-Archive -Path $exe -DestinationPath $zip -Force
"{0}  ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
