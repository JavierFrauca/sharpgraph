# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
(with the caveat that, during beta, the MCP tool surface may change between
minor versions).

## [Unreleased]

### Added
- **Server metadata for LLM discoverability**: the MCP `initialize` response now carries
  `serverInfo.title` ("SharpGraph — grafo de código y documentación C#"), a `serverInfo.description`
  of what/how (dependency graph + docs index, token-saving thesis) and `websiteUrl`. The server
  instructions were rewritten around what/how/when: a new "CUÁNDO USAR / CUÁNDO NO" section
  (when to prefer grep, C#-only scope, read-only, unindexed formats), the 11-step flow, and an
  expanded "LÍMITES" section. All 17 tools and their 31 parameters already carried descriptions
  (verified over the wire with a JSON-RPC probe).
- **Documentation index (`search_docs`)**: `scan` now also indexes project documentation —
  `.md`/`.markdown`/`.txt` and known config JSON (`appsettings*`, `launchSettings`) — into a
  lightweight in-RAM index (`Docs/DocIndex.cs`; no new dependencies, no disk cache). New MCP
  tool `search_docs` (CLI: `sharpgraph docs`) runs BM25 over content with titles and sections
  weighted x3, returning path + section — never content. Docs are cross-linked with code:
  `search` tags types with `[docs:N]` and `understand` lists the docs mentioning the type
  (PascalCase/camelCase symbols of 4+ chars only, to avoid prose false positives). A second
  `FileSystemWatcher` in `ProjectWatcher` keeps the doc index fresh on save; doc-only changes
  skip the fragment-cache save.
- **Incremental fragment merge**: `GraphEngine.MergeFragments` now takes a fast path when
  every changed file still declares the same types and exposes the same return signatures —
  the old fragment's contributions are subtracted and the new ones added in milliseconds
  (`GraphEngine.Incremental.cs`), instead of rebuilding every index from every fragment.
  Structural changes (type added/removed/renamed, changed return type, new file) fall back
  to a single full rebuild per batch. Exposed `GraphEngine.LastMergeIncremental` for
  diagnostics/tests.

### Changed
- **Watcher batching**: `ProjectWatcher.Flush` parses all pending files and performs ONE
  merge per batch (was: one full rebuild per file), guards against overlapping flushes, and
  saves the cache with a 10 s throttle. `SolutionScanner.RescanFiles` parses a batch without
  merging.

### Fixed
- **Auto-scan hook invoked a wrong tool name**: the `CwdChanged` hook written by
  `configure_auto_scan`, `install.ps1` and `install.sh` used `"tool": "Scan"`, but the MCP wire
  name is snake_case (`scan`) — the hook failed silently with "Unknown tool". All three writers
  now emit `"scan"`. Existing hooks in `~/.claude/settings.json` need a one-line manual fix (or
  remove the old entry and re-run `configure_auto_scan`).
- **Query starvation on file saves**: saving `.cs` files while queries were in flight could
  freeze `understand`/`search` for a long time on large solutions — each saved file held the
  graph lock for a full rebuild and the cache was rewritten entirely on every flush, with
  concurrent saves failing silently. The incremental merge plus throttled, atomic cache
  writes (`GraphStore` temp-file + move) eliminate both stalls and corrupted/failed saves.
- PageRank is now only recomputed on full rebuilds; body-edit deltas keep the previous
  ranking (suggestion ordering only, never edge correctness).

### Tests
- 11 new tests (`IncrementalMergeTests`) asserting delta-vs-full-rebuild equivalence
  (stats + query outputs) for body edits, batches, removals, structural fallbacks, chained
  receivers, partial classes, `RemoveFile`, BM25 index, and a delta latency smoke test.
- 8 new tests (`DocIndexTests`) covering markdown parsing (title/sections, code-fence skip),
  heading-weighted BM25 ranking, config-JSON filtering, doc↔type mentions (including the
  `understand`/`search` `[docs:N]` integration), excluded directories, and incremental rescans.

## [2.1.0] — 2026-07-21

First public beta. The first release with a published changelog; earlier
internal history is summarized at the bottom.

### Added
- **Tests**: xUnit test project (`src/SharpGraph.Tests/`) with 45 tests covering
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
- `SharpGraph.csproj` no longer hardcodes `win-x64`; the RID is passed at
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

[Unreleased]: https://github.com/JavierFrauca/sharpgraph/compare/v2.1.0...HEAD
[2.1.0]: https://github.com/JavierFrauca/sharpgraph/releases/tag/v2.1.0
[2.0.0]: https://github.com/JavierFrauca/sharpgraph/compare/v1.0.0...v2.0.0
