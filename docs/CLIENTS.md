# Cómo registrar SharpGraph en cada cliente MCP

SharpGraph es un **servidor MCP por stdio**: cualquier cliente que soporte el protocolo
puede usarlo. Aquí tienes los snippets de configuración verificados para los más comunes.

> La forma más fácil es usar los instaladores:
> - **Windows**: `install.ps1` (con `-Client <claude|cline|cursor|generic>`)
> - **macOS/Linux**: `install.sh` (con `--client <claude|cline|cursor|generic>`)
>
> Estos snippets son la alternativa manual y la referencia de qué fichero toca cada uno.

En todos los casos, `command` es la ruta al binario descargado para tu plataforma
(`SharpGraph.exe` en Windows, `SharpGraph` en macOS/Linux).

---

## Claude Code

CLI oficial de Anthropic. Es el único cliente con **auto-escaneo al cambiar de proyecto**
(vía el hook `CwdChanged`); los demás requieren llamar a `scan(path)` manualmente.

```bash
claude mcp add -s user sharpgraph /ruta/al/SharpGraph(.exe)
```

O, editando `~/.claude.json` a mano:

```jsonc
{
  "mcpServers": {
    "sharpgraph": {
      "command": "/ruta/al/SharpGraph",
      "args": []
    }
  }
}
```

**Auto-escaneo (opcional, solo Claude Code):** añade este hook en `~/.claude/settings.json`
para que el grafo se reconstruya al cambiar de carpeta de proyecto:

```jsonc
{
  "hooks": {
    "CwdChanged": [
      {
        "hooks": [
          {
            "type": "mcp_tool",
            "server": "sharpgraph",
            "tool": "Scan",
            "input": { "path": "${cwd}" },
            "async": true,
            "statusMessage": "SharpGraph indexing..."
          }
        ]
      }
    ]
  }
}
```

> La herramienta `configure_auto_scan()` que expone SharpGraph hace exactamente esto;
> es específica de Claude Code y no tiene efecto en otros clientes.

---

## Cursor

Fichero: `~/.cursor/mcp.json` (global) o `.cursor/mcp.json` dentro del proyecto.

```json
{
  "mcpServers": {
    "sharpgraph": {
      "command": "/ruta/al/SharpGraph",
      "args": []
    }
  }
}
```

En Cursor: *Settings → Cursor Settings → MCP → Add new MCP server*, o crea el fichero
a mano. Tras guardarlo, reinicia Cursor.

---

## Cline (VS Code)

Fichero (Windows): `%APPDATA%\Code\User\globalStorage\saoudrizwan.claude-dev\settings\cline_mcp_settings.json`
Fichero (macOS): `~/Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json`
Fichero (Linux): `~/.config/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json`

```json
{
  "mcpServers": {
    "sharpgraph": {
      "command": "/ruta/al/SharpGraph",
      "args": [],
      "env": {}
    }
  }
}
```

Desde la UI: icono de Cline en la barra lateral → pestaña *MCP Servers* → *Configure MCP Servers*.

---

## Continue (VS Code / JetBrains)

Continue usa una sección `mcpServers` en `~/.continue/config.json` (experimental):

```json
{
  "mcpServers": {
    "sharpgraph": {
      "command": "/ruta/al/SharpGraph",
      "args": []
    }
  }
}
```

> La integración MCP de Continue es más nueva; verifica su doc oficial si hay cambios.

---

## Zed

Fichero: `~/.config/zed/settings.json` (Linux/Windows) o `~/Library/Application Support/Zed/settings.json` (macOS).

```jsonc
{
  "context_servers": {
    "sharpgraph": {
      "command": "/ruta/al/SharpGraph",
      "args": []
    }
  }
}
```

---

## VS Code

**Recomendado:** ejecuta `SharpGraph setup --client vscode` desde la raíz de tu
proyecto. Crea un fichero `.vscode/mcp.json` a nivel workspace con la configuración
correcta. Si el fichero ya existe, edítalo a mano.

**Manual:** edita `.vscode/mcp.json` en tu proyecto y añade:

```jsonc
{
  "mcpServers": {
    "sharpgraph": {
      "command": "/ruta/al/SharpGraph",
      "args": []
    }
  }
}
```

Para configuración global (todos los proyectos), edita `settings.json` del usuario
(`Ctrl+Shift+P` → "Preferences: Open User Settings JSON") y añade la misma entrada.
La clave exacta depende de la extensión MCP que uses; las más comunes son
`"mcpServers"` y `"mcp.servers"`.

---

## Verificar que está cargado

Tras registrar el servidor, reinicia el cliente y pide al LLM:

```
stats()
```

Si responde con conteos (tipos, aristas, endpoints), el servidor está activo. Si dice
"grafo vacío", pídele:

```
scan("/ruta/absoluta/a/tu/proyecto")
```

Y vuelve a probar.

---

## Sin auto-escaneo (clientes que no son Claude Code)

En todos los clientes **excepto Claude Code** no hay hook `CwdChanged`. Para mantener el
grafo al día:

1. La primera vez en un proyecto, pide al LLM `scan("/ruta")`.
2. SharpGraph mantiene un *FileSystemWatcher* que re-parsea automáticamente los `.cs`
   que modifiques mientras el servidor esté vivo. No necesitas re-escanear tras cada cambio.
3. Si cambias de proyecto, pide `scan` de la nueva ruta (sobreescribe el grafo en memoria).
