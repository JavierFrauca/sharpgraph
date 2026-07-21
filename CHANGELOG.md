# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
(with the caveat that, during beta, the MCP tool surface may change between
minor versions).

## [Unreleased]

_Nothing yet._

## [2.1.0] — 2026-07-21

First public beta. The first release with a published changelog; earlier
internal history is summarized at the bottom.

### Added
- **Tests**: xUnit test project (`src/LocalGraph.Tests/`) with 45 tests covering
  `TypeReferenceVisitor` extraction (MediatR, Minimal API, DI registrations in
  all forms, nested types, generic containers, routing, ambiguous names, false
  positives) and `CodeGraph` queries (`resolve_di`, `find_callers`,
  `find_call_sites`, `trace_to_endpoints`, `flow` cycles). Synthetic `.cs`
  fixtures are embedded as resources so tests run without touching disk.
- **Call-site coverage (Fases A + B)**: `find_call_sites` now recognizes
  patterns that previously caused silent loss of invocations:
  - Null-conditional `_svc?.Method()` (visitor-level fix).
  - Factory/chaining `_factory.Get().Method()`, `a.B().C().M()` (two-pass
    resolution via the new `MemberReturnSignature` index).
  - Deep member-access `_outer.Inner.Method()`.
  - `var x = await svc.GetAsync(); x.Method()` (via the new `PendingLocal`
    mechanism).
  - Lambdas (already worked; explicit regression tests added).
- **Multiplatform**: publish targets `win-x64`, `linux-x64`, `osx-arm64` as
  self-contained single-file binaries. New `publish-all.ps1` builds and packages
  all three.
- **Multi-client support**: new `docs/CLIENTS.md` with verified registration
  snippets for Claude Code, Cursor, Cline, Continue, Zed, and generic VS Code.
  New `install.sh` for macOS/Linux; `install.ps1` refactored with
  `-Client`/`-ConfigureHook`/`-InstallPath` flags.
- **CI/CD**: GitHub Actions workflows for CI (`ci.yml`, runs tests on every PR)
  and release (`release.yml`, builds 3 RIDs and publishes a GitHub Release on
  tag `v*`).
- **Community health files**: `LICENSE` (MIT), `CONTRIBUTING.md`, `SECURITY.md`,
  `CODE_OF_CONDUCT.md`, issue templates, `CODEOWNERS`.
- **Public benchmark**: `docs/BENCHMARK.md` updated with results over
  [CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture),
  reproducible with `bench/benchmark.py bench/questions.cleanarchitecture.py`.
- **Docs**: `docs/COMPARATIVA.md` (public, anonymized comparison with
  CodeGraph, Sourcegraph MCP, code-graph-mcp).
- **Quickstart** section in README (5 minutes from download to first query).
- **Demo script** (`demo.ps1` / `demo.sh`) reproducing the headline queries
  on CleanArchitecture.

### Changed
- `LocalGraph.csproj` no longer hardcodes `win-x64`; the RID is passed at
  publish time.
- `ServerInfo.Version` bumped to `2.1.0`.
- `ParserVersion` bumped to `7` (the `FileFragment` model gained
  `ReturnSignatures`, `PendingCallSites`, and `PendingLocals`; old caches are
  invalidated automatically).
- `docs/ARCHITECTURE.md` corrected: it previously claimed "no persistence at
  all", but `GraphStore` has cached to disk since v2.0. The persistence,
  incremental scan, and watcher sections now reflect reality.
- README header now states the niche unambiguously: **C#/.NET-only,
  token-efficient**.

### Fixed
- `DetectSend` no longer treats arbitrary `Send`/`Publish`/`Dispatch` calls as
  MediatR/bus messages: it now validates the receiver type against a list of
  known bus types (`IMediator`, `IBus`, `IDispatcher`, …), eliminating false
  positives like `smtp.Send(email)`.
- `flow()` cycle handling: the existing `visited` deduplication was confirmed
  correct via contract tests; a comment was added to `RenderFlow` explaining
  why cycles are cut.
- `configure_auto_scan()` MCP tool no longer blindly writes to
  `~/.claude/settings.json` when the Claude Code folder doesn't exist: it
  returns an explanatory message instead.

### Known limitations (documented in README)
- Indexer receivers (`_map[key].Method()`) and top-level statements with DI
  chaining remain unsupported. See "Limitaciones conocidas" in README.

## [2.0.0] — 2026-05 (internal)

- Rewritten as a .NET MCP server with a token-efficient graph.
- MediatR, DI, Minimal API, and ASP.NET Core routing modeled explicitly.
- 15 MCP tools: `scan`, `trace_to_endpoints`, `find_callers`, `get_usages`,
  `find_call_sites`, `get_source`, `understand`, `flow`, `resolve_di`,
  `search`, `explore_context`, `hubs`, `search_semantic`, `stats`,
  `configure_auto_scan`.
- Disk cache with `ParserVersion` and `FileSystemWatcher` for incremental
  updates.

## [1.0.0] — earlier internal versions

- Initial prototype. Replaced by v2.

[Unreleased]: https://github.com/JavierFrauca/localgraph/compare/v2.1.0...HEAD
[2.1.0]: https://github.com/JavierFrauca/localgraph/releases/tag/v2.1.0
[2.0.0]: https://github.com/JavierFrauca/localgraph/compare/v1.0.0...v2.0.0
