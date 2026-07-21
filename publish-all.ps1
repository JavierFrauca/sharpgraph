#Requires -Version 7
<#
.SYNOPSIS
    Publica LocalGraph para los tres RIDs soportados (win-x64, linux-x64, osx-arm64)
    como ejecutables autocontenidos y empaqueta cada uno para distribución.

.DESCRIPTION
    - Detiene cualquier instancia en ejecución de LocalGraph (solo Windows).
    - Para cada RID: dotnet publish -r <rid> --self-contained, single-file.
    - Empaqueta el resultado (zip en Windows, tar.gz en Unix) con el instalador
      correspondiente (install.ps1 / install.sh) dentro.
    - Genera la carpeta dist/ con un artefacto por plataforma.

.PARAMETER RIDs
    Lista de RIDs a compilar. Por defecto: win-x64, linux-x64, osx-arm64.

.PARAMETER SkipPack
    No crear zip/tar.gz, solo dejar las carpetas en dist/<rid>.

.EXAMPLE
    .\publish-all.ps1
    .\publish-all.ps1 -RIDs win-x64
    .\publish-all.ps1 -SkipPack
#>
param(
    [string[]] $RIDs = @("win-x64", "linux-x64", "osx-arm64"),
    [switch] $SkipPack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root "src\LocalGraph\LocalGraph.csproj"
$dist    = Join-Path $root "dist"

# ── 1. Detener procesos en ejecución (best-effort, solo Windows) ─────────────
if ($IsWindows -or ($PSVersionTable.Platform -and $PSVersionTable.Platform -eq 'Win32NT') -or -not $PSVersionTable.Platform) {
    $running = Get-Process -Name LocalGraph -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host "Deteniendo LocalGraph.exe (PID $($running.Id))..."
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

# ── 2. Limpiar dist anterior ─────────────────────────────────────────────────
if (Test-Path $dist) {
    Write-Host "Limpiando $dist ..."
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# ── 3. Publicar + empaquetar cada RID ────────────────────────────────────────
foreach ($rid in $RIDs) {
    $out = Join-Path $dist $rid
    Write-Host ""
    Write-Host "==> Publicando $rid ..."

    dotnet publish $project -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -o $out
    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish ha fallado para $rid (código $LASTEXITCODE)"
        exit $LASTEXITCODE
    }

    # Copiar el instalador correspondiente dentro de la carpeta del RID
    if ($rid -like "win-*") {
        Copy-Item -Path (Join-Path $root "install.ps1") -Destination $out -Force
    } else {
        Copy-Item -Path (Join-Path $root "install.sh") -Destination $out -Force
    }

    # Binario generado
    $binExt = if ($rid -like "win-*") { ".exe" } else { "" }
    $binName = "LocalGraph$binExt"
    $bin = Join-Path $out $binName
    if (-not (Test-Path $bin)) {
        Write-Error "No se encontro el binario $bin"
        exit 1
    }
    $size = [math]::Round((Get-Item $bin).Length / 1MB, 1)
    Write-Host "    OK  $bin ($size MB)"

    if ($SkipPack) { continue }

    # Empaquetar
    $archive = Join-Path $dist "LocalGraph-$rid"
    if ($rid -like "win-*") {
        $zip = "$archive.zip"
        Write-Host "    Empaquetando $zip ..."
        # Compress-Archive es multiplataforma en PS 7+
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -Force
    } else {
        $tgz = "$archive.tar.gz"
        Write-Host "    Empaquetando $tgz ..."
        # tar está disponible en Windows 10+ y en macOS/Linux
        Push-Location $dist
        try {
            & tar -czf (Split-Path $tgz -Leaf) $rid
            if ($LASTEXITCODE -ne 0) { throw "tar ha fallado para $rid" }
        }
        finally { Pop-Location }
    }
}

Write-Host ""
Write-Host "Publicación completa. Artefactos en: $dist"
Get-ChildItem $dist | ForEach-Object { Write-Host "  - $($_.Name)" }
