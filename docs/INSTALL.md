# Instalación

## Requisitos

- Windows 10/11 x64
- [Claude Code](https://claude.ai/code) instalado y en el PATH

No se requiere .NET ni ninguna otra dependencia: el ejecutable es autocontenido.

---

## Instalación en un paso

Desde la carpeta del paquete recibido, abre una terminal PowerShell y ejecuta:

```powershell
.\install.ps1
```

El script:
1. Copia `SharpGraph.exe` a `%USERPROFILE%\tools\SharpGraph\`
2. Registra el servidor MCP en Claude Code
3. Configura el auto-escaneo automático al cambiar de proyecto

Al terminar, **reinicia Claude Code**.

### Carpeta de instalación personalizada

```powershell
.\install.ps1 -InstallPath "C:\tools\SharpGraph"
```

---

## Qué ocurre tras instalar

Al abrir cualquier proyecto C# en Claude Code, SharpGraph escanea el código automáticamente en segundo plano. El LLM puede empezar a hacer preguntas sobre el código sin ningún paso adicional.

Si prefieres escanear manualmente, puedes pedírselo al LLM en cualquier momento:

```
scan("C:\tu-proyecto\src")
```

---

## Uso habitual

| Pregunta | Herramienta |
|---|---|
| No sé el nombre exacto del tipo | `search("NombreParcial")` |
| ¿Desde qué endpoint se llama a este servicio? | `trace_to_endpoints("IMyService")` |
| ¿Qué partes del código usan este tipo? | `find_callers("IMyService", depth: 3)` |
| ¿De qué depende esta clase? | `get_usages("MyService")` |
| ¿Cuántos tipos hay indexados? | `stats()` |

---

## Solución de problemas

**`stats()` no responde**
Reinicia Claude Code. Si persiste, verifica el registro: `claude mcp list`.

**El escaneo devuelve 0 tipos**
Verifica que el path apunta a una carpeta con ficheros `.cs`. Las carpetas `obj\` y `bin\` se ignoran automáticamente.

**`trace_to_endpoints` no encuentra caminos**
Puede que los endpoints no estén decorados con `[HttpGet]`, `[HttpPost]`, etc. Usa `find_callers` para ver igualmente la red de dependencias.
