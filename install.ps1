<#
.SYNOPSIS
    Instala LocalGraph MCP para Claude Code.

.DESCRIPTION
    - Copia el ejecutable a la carpeta de instalacion
    - Registra el servidor MCP en Claude Code
    - Configura el auto-escaneo al cambiar de proyecto (hook CwdChanged)

.PARAMETER InstallPath
    Carpeta donde se instalara LocalGraph.exe.
    Por defecto: %USERPROFILE%\tools\LocalGraph

.EXAMPLE
    .\install.ps1
    .\install.ps1 -InstallPath "C:\tools\LocalGraph"
#>

param(
    [string] $InstallPath = (Join-Path $env:USERPROFILE "tools\LocalGraph")
)

$ErrorActionPreference = 'Stop'

$settingsFile = Join-Path $env:USERPROFILE ".claude\settings.json"
$exe          = Join-Path $PSScriptRoot "LocalGraph.exe"
$target       = Join-Path $InstallPath "LocalGraph.exe"

# ── Comprobaciones previas ────────────────────────────────────────────────────
if (-not (Test-Path $exe)) {
    Write-Error "No se encontro LocalGraph.exe junto a este script ($PSScriptRoot).`nAsegurate de ejecutar install.ps1 desde la carpeta del paquete."
    exit 1
}

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Error "Claude Code no esta instalado o no esta en el PATH.`nInstala Claude Code primero: https://claude.ai/code"
    exit 1
}

# ── Helper: PSCustomObject → hashtable anidado (compatible PS 5.1+) ──────────
# Usa Hashtable normal (no OrderedDictionary) para que .ContainsKey() funcione en PS 5.1
function ConvertTo-DeepHashtable($obj) {
    if ($null -eq $obj) { return $null }
    if ($obj -is [System.Management.Automation.PSCustomObject]) {
        $ht = @{}
        foreach ($prop in $obj.PSObject.Properties) {
            $ht[$prop.Name] = ConvertTo-DeepHashtable $prop.Value
        }
        return $ht
    }
    if ($obj -is [System.Collections.IEnumerable] -and $obj -isnot [string]) {
        return @(foreach ($item in $obj) { ConvertTo-DeepHashtable $item })
    }
    return $obj
}

# ── 1. Copiar ejecutable ──────────────────────────────────────────────────────
Write-Host "Instalando en $InstallPath ..."
New-Item -ItemType Directory -Force -Path $InstallPath | Out-Null
Copy-Item -Path $exe -Destination $target -Force
Write-Host "  OK  LocalGraph.exe copiado"

# ── 2. Registrar servidor MCP ─────────────────────────────────────────────────
Write-Host "Registrando servidor MCP..."
try { $null = & claude mcp remove localgraph -s user 2>&1 } catch {}
& claude mcp add -s user localgraph $target
Write-Host "  OK  MCP registrado: localgraph -> $target"

# ── 3. Configurar hook CwdChanged en settings.json ───────────────────────────
Write-Host "Configurando auto-escaneo (hook CwdChanged)..."

$raw      = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw } else { '{}' }
$settings = ConvertTo-DeepHashtable (ConvertFrom-Json $raw)

if (-not $settings.ContainsKey('hooks'))          { $settings['hooks'] = [ordered]@{} }
if (-not $settings['hooks'].ContainsKey('CwdChanged')) { $settings['hooks']['CwdChanged'] = @() }

# Comprobar si ya existe un hook para localgraph
$alreadyConfigured = $false
foreach ($group in $settings['hooks']['CwdChanged']) {
    foreach ($h in $group['hooks']) {
        if ($h['server'] -eq 'localgraph') { $alreadyConfigured = $true; break }
    }
}

if ($alreadyConfigured) {
    Write-Host "  --  Hook CwdChanged ya configurado, sin cambios"
} else {
    $hookEntry = [ordered]@{
        hooks = @(
            [ordered]@{
                type          = "mcp_tool"
                server        = "localgraph"
                tool          = "Scan"
                input         = [ordered]@{ path = '${cwd}' }
                async         = $true
                statusMessage = "LocalGraph indexing..."
            }
        )
    }
    $settings['hooks']['CwdChanged'] = @($settings['hooks']['CwdChanged']) + @($hookEntry)

    New-Item -ItemType File -Force -Path $settingsFile | Out-Null
    $settings | ConvertTo-Json -Depth 10 | Set-Content $settingsFile -Encoding UTF8
    Write-Host "  OK  Hook CwdChanged configurado"
}

# ── Resumen ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Instalacion completada. Reinicia Claude Code para aplicar los cambios."
Write-Host ""
Write-Host "A partir de entonces, al abrir cualquier proyecto C# en Claude Code"
Write-Host "el grafo se construira automaticamente en segundo plano."
