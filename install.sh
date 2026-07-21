#!/usr/bin/env bash
# Instala LocalGraph MCP para el cliente MCP que uses (Claude Code por defecto).
#
# Sintaxis:  ./install.sh [--client <claude|cline|cursor|generic>] [--no-hook] [--install-path <dir>]
#
# Qué hace:
#   1. Copia LocalGraph a ~/tools/LocalGraph/ (o --install-path)
#   2. Registra el servidor MCP en el cliente indicado (--client, por defecto claude)
#   3. Si el cliente es Claude Code y no se pasa --no-hook, configura el hook
#      CwdChanged para auto-escanear al cambiar de proyecto.
#
# El auto-escaneo (hook CwdChanged) solo existe en Claude Code. En otros clientes
# tendras que llamar a scan(path) manualmente, o configurar el hook equivalente.
set -euo pipefail

CLIENT="claude"
INSTALL_PATH="$HOME/tools/LocalGraph"
CONFIGURE_HOOK="true"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --client)        CLIENT="$2"; shift 2 ;;
        --install-path)  INSTALL_PATH="$2"; shift 2 ;;
        --no-hook)       CONFIGURE_HOOK="false"; shift ;;
        -h|--help)
            grep '^#' "$0" | sed 's/^# \?//'
            exit 0 ;;
        *) echo "Argumento desconocido: $1" >&2; exit 2 ;;
    esac
done

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_NAME="LocalGraph"
SRC_BIN="$SCRIPT_DIR/$BIN_NAME"

if [[ ! -x "$SRC_BIN" ]]; then
    # algunos paquetes vienen sin bit de ejecución puesto tras descomprimir
    if [[ -f "$SRC_BIN" ]] && chmod +x "$SRC_BIN" 2>/dev/null; then :
    else
        echo "No se encontro el ejecutable LocalGraph junto a este script ($SCRIPT_DIR)." >&2
        echo "Ejecuta install.sh desde la carpeta del paquete (descomprimido)." >&2
        exit 1
    fi
fi

mkdir -p "$INSTALL_PATH"
cp -f "$SRC_BIN" "$INSTALL_PATH/$BIN_NAME"
chmod +x "$INSTALL_PATH/$BIN_NAME"
echo "OK  LocalGraph copiado a $INSTALL_PATH/$BIN_NAME"

register_claude() {
    if ! command -v claude >/dev/null 2>&1; then
        echo "Claude Code no esta instalado o no esta en el PATH." >&2
        echo "Instalalo primero: https://claude.ai/code" >&2
        exit 1
    fi
    claude mcp remove localgraph -s user 2>/dev/null || true
    claude mcp add -s user localgraph "$INSTALL_PATH/$BIN_NAME"
    echo "OK  MCP registrado para Claude Code"
}

# Cline (VS Code): escribe en ~/Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json (macOS)
# o ~/.config/Code/User/globalStorage/saoudrizwan.claude-dev/settings/cline_mcp_settings.json (Linux)
register_cline() {
    local base
    if [[ "$(uname)" == "Darwin" ]]; then
        base="$HOME/Library/Application Support/Code/User/globalStorage/saoudrizwan.claude-dev/settings"
    else
        base="$HOME/.config/Code/User/globalStorage/saoudrizwan.claude-dev/settings"
    fi
    mkdir -p "$base"
    local f="$base/cline_mcp_settings.json"
    # si ya existe, añadimos/actualizamos la entrada localgraph; si no, creamos el esqueleto.
    if [[ -f "$f" ]]; then
        echo "El fichero $f ya existe. Edítalo a mano y añade el servidor localgraph (ver docs/CLIENTS.md)."
    else
        cat > "$f" <<JSON
{
  "mcpServers": {
    "localgraph": {
      "command": "$INSTALL_PATH/$BIN_NAME",
      "args": [],
      "env": {}
    }
  }
}
JSON
        echo "OK  MCP registrado para Cline en $f"
    fi
}

register_cursor() {
    local f="$HOME/.cursor/mcp.json"
    mkdir -p "$(dirname "$f")"
    if [[ -f "$f" ]]; then
        echo "El fichero $f ya existe. Edítalo a mano y añade el servidor localgraph (ver docs/CLIENTS.md)."
    else
        cat > "$f" <<JSON
{
  "mcpServers": {
    "localgraph": {
      "command": "$INSTALL_PATH/$BIN_NAME",
      "args": []
    }
  }
}
JSON
        echo "OK  MCP registrado para Cursor en $f"
    fi
}

register_generic() {
    echo "Cliente 'generic': no se ha escrito ningun fichero de configuracion automaticamente."
    echo "Anade este servidor a la configuracion MCP de tu cliente:"
    echo ""
    echo "  command: $INSTALL_PATH/$BIN_NAME"
    echo "  args:    []"
    echo ""
    echo "Ver docs/CLIENTS.md para ejemplos por cliente."
}

case "$CLIENT" in
    claude)  register_claude ;;
    cline)   register_cline ;;
    cursor)  register_cursor ;;
    generic) register_generic ;;
    *) echo "Cliente desconocido: $CLIENT (usar: claude|cline|cursor|generic)" >&2; exit 2 ;;
esac

# Hook CwdChanged solo aplica a Claude Code.
if [[ "$CLIENT" == "claude" && "$CONFIGURE_HOOK" == "true" ]]; then
    SETTINGS="$HOME/.claude/settings.json"
    mkdir -p "$(dirname "$SETTINGS")"

    if [[ -f "$SETTINGS" ]] && grep -q '"localgraph"' "$SETTINGS" 2>/dev/null; then
        echo "--  Hook/auto-config de localgraph ya presente en $SETTINGS"
    else
        # usa python3 si esta disponible para editar el JSON de forma segura; si no, instructiones manuales.
        if command -v python3 >/dev/null 2>&1; then
            python3 - "$SETTINGS" "$INSTALL_PATH/$BIN_NAME" <<'PY'
import json, os, sys
path, exe = sys.argv[1], sys.argv[2]
try:
    with open(path) as f: cfg = json.load(f)
except Exception:
    cfg = {}
hooks = cfg.setdefault("hooks", {})
cwd = hooks.setdefault("CwdChanged", [])
already = any(h.get("server") == "localgraph" for group in cwd for h in group.get("hooks", []))
if not already:
    cwd.append({"hooks": [{
        "type": "mcp_tool", "server": "localgraph", "tool": "Scan",
        "input": {"path": "${cwd}"}, "async": True,
        "statusMessage": "LocalGraph indexing..."
    }]})
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w") as f: json.dump(cfg, f, indent=2)
    print(f"OK  Hook CwdChanged configurado en {path}")
else:
    print(f"--  Hook CwdChanged ya configurado en {path}")
PY
        else
            echo "Python3 no disponible; configura el hook CwdChanged a mano en $SETTINGS"
            echo "(ver docs/CLIENTS.md para el snippet)."
        fi
    fi
elif [[ "$CONFIGURE_HOOK" == "true" && "$CLIENT" != "claude" ]]; then
    echo "--  Hook auto-scan omitido: solo aplica a Claude Code. En $CLIENT, llama a scan(path) manualmente."
fi

echo ""
echo "Instalacion completada. Reinicia tu cliente MCP para aplicar los cambios."
echo ""
echo "A partir de entonces, podras pedir al LLM cosas como:"
echo "  scan(\"/ruta/a/tu/proyecto\")   # si no tienes el hook automatico"
echo "  search(\"GrossService\")        # localiza un tipo"
echo "  trace_to_endpoints(\"IMiServicio\")"
