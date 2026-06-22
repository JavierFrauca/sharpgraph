#Requires -Version 7
<#
.SYNOPSIS
    Compila y publica LocalGraph como ejecutable autocontenido para Windows x64.

.DESCRIPTION
    - Detiene cualquier instancia en ejecucion de LocalGraph.exe
    - Limpia la carpeta publish\ anterior
    - Publica un ejecutable autocontenido (no requiere .NET en destino)
    - Actualiza el registro MCP del usuario actual

.EXAMPLE
    .\publish.ps1
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root    = $PSScriptRoot
$project = Join-Path $root "src\LocalGraph\LocalGraph.csproj"
$out     = Join-Path $root "publish"

# ── 1. Detener procesos en ejecucion ─────────────────────────────────────────
$running = Get-Process -Name LocalGraph -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Deteniendo LocalGraph.exe (PID $($running.Id))..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

# ── 2. Limpiar publicacion anterior ──────────────────────────────────────────
if (Test-Path $out) {
    Write-Host "Limpiando $out ..."
    Remove-Item $out -Recurse -Force
}

# ── 3. Publicar ──────────────────────────────────────────────────────────────
Write-Host "Compilando y publicando..."
dotnet publish $project -c Release -o $out
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish ha fallado con codigo $LASTEXITCODE"
    exit $LASTEXITCODE
}

# ── 4. Verificar ejecutable generado ─────────────────────────────────────────
$exe = Join-Path $out "LocalGraph.exe"
if (-not (Test-Path $exe)) {
    Write-Error "No se encontro el ejecutable en $exe"
    exit 1
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Publicado correctamente: $exe ($size MB)"

# ── 5. Incluir script de instalacion en el paquete ───────────────────────────
Copy-Item -Path (Join-Path $root "install.ps1") -Destination $out -Force
Write-Host "Script de instalacion incluido en $out"

# ── 6. Actualizar registro MCP ────────────────────────────────────────────────
Write-Host "Actualizando registro MCP..."
claude mcp remove localgraph -s user 2>$null
claude mcp add -s user localgraph $exe
Write-Host "MCP actualizado. Reinicia Claude Code para aplicar los cambios."
