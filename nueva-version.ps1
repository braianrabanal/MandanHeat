# Publica una versión nueva: actualiza el número, hace commit, crea la etiqueta y la sube.
# GitHub Actions compila el ejecutable y crea la Release automáticamente.
# Uso:  powershell -ExecutionPolicy Bypass -File .\nueva-version.ps1 -Version 1.2.0
param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "La versión debe tener el formato X.Y.Z (por ejemplo 1.2.0)" }
$tag = "v$Version"
if (git tag --list $tag) { throw "La etiqueta $tag ya existe" }

$pending = git status --porcelain
if ($pending) {
    Write-Host "Hay cambios sin guardar en git. Haz commit de ellos antes de publicar:" -ForegroundColor Yellow
    $pending
    exit 1
}

# Comprobación: ningún archivo del repositorio menciona herramientas de IA.
$words = @(("cla" + "ude"), ("anth" + "ropic"))   # partidas para que este script no se detecte a sí mismo
$traces = git grep -niI -e $words[0] -e $words[1]
if ($traces) { Write-Host "Revisa estas líneas antes de publicar:" -ForegroundColor Red; $traces; exit 1 }

$csproj = "src\MandanHeat\MandanHeat.csproj"
$content = Get-Content $csproj -Raw
$content = [regex]::Replace($content, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText((Resolve-Path $csproj), $content, (New-Object Text.UTF8Encoding($false)))

git add $csproj
git commit -m "Versión $Version"
git tag -a $tag -m "MandanHeat $Version"
git push origin main
git push origin $tag

Write-Host ""
Write-Host "Publicado $tag. GitHub está compilando la Release (tarda unos 3-5 minutos):" -ForegroundColor Green
Write-Host "  https://github.com/braianrabanal/MandanHeat/actions"
Write-Host "  https://github.com/braianrabanal/MandanHeat/releases"
