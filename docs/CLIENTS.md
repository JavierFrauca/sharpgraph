# Cómo registrar LocalGraph en cada cliente MCP

LocalGraph es un **servidor MCP por stdio**: cualquier cliente que soporte el protocolo
puede usarlo. Aquí tienes los snippets de configuración verificados para los más comunes.

> La forma más fácil es usar los instaladores:
> - **Windows**: `install.ps1` (con `-Client <claude|cline|cursor|generic>`)
> - **macOS/Linux**: `install.sh` (con `--client <claude|cline|cursor|generic>`)
>
> Estos snippets son la alternativa manual y la referencia de qué fichero toca cada uno.

En todos los casos, `command` es la ruta al binario descargado para tu plataforma
(`LocalGraph.exe` en Windows, `LocalGraph` en macOS/Linux).

---

## Claude Code

CLI oficial de Anthropic. Es el único cliente con **auto-escaneo al cambiar de proyecto**
(vía el hook `CwdChanged`); los demás requieren llamar a `scan(path)` manualmente.

```bash
claude mcp add -s user localgraph /ruta/al/LocalGraph(.exe)
```

O, editando `~/.claude.json` a mano:

```jsonc
{
  "mcpServers": {
    "localgraph": {
      "command": "/ruta/al/LocalGraph",
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
            "server": "localgraph",
            "tool": "Scan",
            "input": { "path": "${cwd}" },
            "async": true,
            "statusMessage": "LocalGraph indexing..."
          }
        ]
      }
    ]
  }
}
```

> La herramienta `configure_auto_scan()` que expone LocalGraph hace exactamente esto;
> es específica de Claude Code y no tiene efecto en otros clientes.

---

## Cursor

Fichero: `~/.cursor/mcp.json` (global) o `.cursor/mcp.json` dentro del proyecto.

```json
{
  "mcpServers": {
    "localgraph": {
      "command": "/ruta/al/LocalGraph",
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
    "localgraph": {
      "command": "/ruta/al/LocalGraph",
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
    "localgraph": {
      "command": "/ruta/al/LocalGraph",
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
    "localgraph": {
      "command": "/ruta/al/LocalGraph",
      "args": []
    }
  }
}
```

---

## VS Code (genérico, cualquier extensión que hable MCP)

Muchas extensiones de VS Code aceptan la convención estándar MCP. Por ejemplo, en
`settings.json` del usuario:

```jsonc
{
  "mcp.servers": {
    "localgraph": {
      "type": "stdio",
      "command": "/ruta/al/LocalGraph",
      "args": []
    }
  }
}
```

(Ajusta la clave según la extensión concreta; algunas usan `"mcpServers"` en vez de `"mcp.servers"`.)

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
2. LocalGraph mantiene un *FileSystemWatcher* que re-parsea automáticamente los `.cs`
   que modifiques mientras el servidor esté vivo. No necesitas re-escanear tras cada cambio.
3. Si cambias de proyecto, pide `scan` de la nueva ruta (sobreescribe el grafo en memoria).
