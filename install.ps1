<#
.SYNOPSIS
    Instala LocalGraph MCP para el cliente MCP que uses (Claude Code por defecto).

.DESCRIPTION
    - Copia LocalGraph.exe a %USERPROFILE%\tools\LocalGraph\ (o -InstallPath)
    - Registra el servidor MCP en el cliente indicado (-Client: claude|cline|cursor|generic)
    - Si el cliente es Claude Code y se pasa -ConfigureHook (defecto), configura el
      hook CwdChanged para auto-escanear al cambiar de proyecto.

.PARAMETER InstallPath
    Carpeta donde se instalara LocalGraph.exe. Defecto: %USERPROFILE%\tools\LocalGraph

.PARAMETER Client
    Cliente MCP objetivo. Defecto: claude.

.PARAMETER ConfigureHook
    Configurar hook auto-scan (solo aplica a Claude Code). Defecto: true.

.EXAMPLE
    .\install.ps1
    .\install.ps1 -Client cline -ConfigureHook:$false
    .\install.ps1 -InstallPath "C:\tools\LocalGraph"
#>
param(
    [string] $InstallPath = (Join-Path $env:USERPROFILE "tools\LocalGraph"),
    [ValidateSet("claude","cline","cursor","generic")]
    [string] $Client = "claude",
    [switch] $ConfigureHook = $true
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

# Helper: PSCustomObject → hashtable anidado (compatible PS 5.1+)
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

# ── 2. Registrar servidor MCP en el cliente indicado ─────────────────────────
switch ($Client) {
    "claude" {
        if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
            Write-Error "Claude Code no esta instalado o no esta en el PATH.`nInstalalo primero: https://claude.ai/code"
            exit 1
        }
        try { $null = & claude mcp remove localgraph -s user 2>&1 } catch {}
        & claude mcp add -s user localgraph $target
        Write-Host "  OK  MCP registrado para Claude Code: localgraph -> $target"
    }
    "cline" {
        $base = Join-Path $env:APPDATA "Code\User\globalStorage\saoudrizwan.claude-dev\settings"
        New-Item -ItemType Directory -Force -Path $base | Out-Null
        $f = Join-Path $base "cline_mcp_settings.json"
        if (Test-Path $f) {
            Write-Host "  --  $f ya existe. Editalo a mano y anade el servidor localgraph (ver docs/CLIENTS.md)."
        } else {
            $json = @"
{
  "mcpServers": {
    "localgraph": {
      "command": "$($target -replace '\\','\\')",
      "args": [],
      "env": {}
    }
  }
}
"@
            New-Item -ItemType File -Force -Path $f | Out-Null
            Set-Content -Path $f -Value $json -Encoding UTF8
            Write-Host "  OK  MCP registrado para Cline en $f"
        }
    }
    "cursor" {
        $dir = Join-Path $env:USERPROFILE ".cursor"
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $f = Join-Path $dir "mcp.json"
        if (Test-Path $f) {
            Write-Host "  --  $f ya existe. Editalo a mano y anade el servidor localgraph (ver docs/CLIENTS.md)."
        } else {
            $json = @"
{
  "mcpServers": {
    "localgraph": {
      "command": "$($target -replace '\\','\\')",
      "args": []
    }
  }
}
"@
            Set-Content -Path $f -Value $json -Encoding UTF8
            Write-Host "  OK  MCP registrado para Cursor en $f"
        }
    }
    "generic" {
        Write-Host "  --  Cliente 'generic': anade manualmente este servidor a la configuracion MCP de tu cliente:"
        Write-Host "        command: $target"
        Write-Host "        args:    []"
        Write-Host "      Ver docs/CLIENTS.md para ejemplos por cliente."
    }
}

# ── 3. Hook CwdChanged (solo Claude Code) ────────────────────────────────────
if ($Client -eq "claude" -and $ConfigureHook) {
    Write-Host "Configurando auto-escaneo (hook CwdChanged)..."

    $raw      = if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw } else { '{}' }
    $settings = ConvertTo-DeepHashtable (ConvertFrom-Json $raw)

    if (-not $settings.ContainsKey('hooks'))                { $settings['hooks'] = @{} }
    if (-not $settings['hooks'].ContainsKey('CwdChanged'))  { $settings['hooks']['CwdChanged'] = @() }

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
        Write-Host "  OK  Hook CwdChanged configurado en $settingsFile"
    }
} elseif ($ConfigureHook -and $Client -ne "claude") {
    Write-Host "  --  Hook auto-scan omitido: solo aplica a Claude Code. En $Client, llama a scan(path) manualmente."
}

# ── Resumen ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Instalacion completada. Reinicia tu cliente MCP para aplicar los cambios."
Write-Host ""
if ($Client -eq "claude" -and $ConfigureHook) {
    Write-Host "A partir de entonces, al abrir cualquier proyecto C# en Claude Code"
    Write-Host "el grafo se construira automaticamente en segundo plano."
} else {
    Write-Host "Recuerda pedir al LLM scan(""C:\ruta\a\tu\proyecto"") la primera vez."
}
