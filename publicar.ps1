# Genera release\MandanHeat\ (carpeta con MandanHeat.exe y sus archivos, sin instalar .NET) y release\MandanHeat-win-x64.zip.
# Uso:  powershell -ExecutionPolicy Bypass -File .\publicar.ps1 [-Version 1.2.0]
param(
    [string]$Out = (Join-Path $PSScriptRoot "release"),
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$dir = Join-Path $Out "MandanHeat"

$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }

dotnet publish (Join-Path $PSScriptRoot "src\MandanHeat") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    @extra `
    -o $dir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló ($LASTEXITCODE)" }

# Marca la carpeta como una instalación publicada: solo esas se actualizan solas desde GitHub.
Set-Content -Path (Join-Path $dir "MandanHeat.release") -Value $Version -Encoding ascii

$zip = Join-Path $Out "MandanHeat-win-x64.zip"
Compress-Archive -Path $dir -DestinationPath $zip -Force
$size = (Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum
"{0}  ({1:N1} MB, {2} archivos)" -f $dir, ($size / 1MB), (Get-ChildItem $dir -Recurse -File).Count
"{0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB)
