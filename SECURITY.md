# Security Policy

## Supported Versions

LocalGraph is in early beta. Only the latest release (`v2.x`) receives security fixes.

## Reporting a Vulnerability

**Do NOT open a public GitHub issue** for security problems.

Instead, please report vulnerabilities privately by emailing the maintainer:
**javier.frauca** (ver dirección en el perfil de GitHub @JavierFrauca)

Please include:
- Description of the issue and its potential impact.
- Steps to reproduce (POC if possible).
- Affected versions.

You should receive a response within **72 hours**. Please do not disclose the
issue publicly until a fix has been released.

## Scope

LocalGraph runs as a local MCP server over stdio. It parses C# source files from
disk using Roslyn and exposes query tools to a local LLM client. Security
considerations relevant to this surface:

- **Filesystem access**: LocalGraph reads `.cs` files under the path passed to
  `scan()`. It does NOT write to source files; it only persists a JSON cache in
  the user's local app data folder (`%LOCALAPPDATA%\LocalGraph\cache\` on
  Windows, equivalent on macOS/Linux). Cache writes are best-effort and sandboxed
  to that folder.
- **No network**: LocalGraph does not make any outbound network connection. It
  does not send code or telemetry anywhere.
- **`install.sh` / `install.ps1`**: these scripts edit the user's MCP client
  configuration (`~/.claude/settings.json`, `~/.cursor/mcp.json`, etc.) to
  register the server. They are idempotent and only add a `localgraph` entry.
  Review them before running, as you would any install script.
- **Untrusted codebases**: if you `scan()` a codebase you do not trust, be aware
  that LocalGraph will read all `.cs` files under that path (respecting the
  standard `obj/`, `bin/`, `.git/`, `node_modules/`, `.vs/` exclusions). Parsing
  is done with Roslyn which is robust, but as with any tool that processes
  untrusted input, running it on adversarial code is at your own risk.

## Disclosure Policy

Once a fix is released, we will publish a GitHub Security Advisory crediting the
reporter (unless they prefer to remain anonymous).
