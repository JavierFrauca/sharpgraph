# Contributing to LocalGraph

Thanks for your interest in contributing! LocalGraph is a small project and
there's plenty of room to help — bug reports, fixture contributions, new
detection patterns, docs, and benchmarks are all welcome.

## How to report a bug

Open an issue using the **Bug report** template. Include:

- LocalGraph version (`LocalGraph.exe --version` if available, or commit hash).
- OS and architecture (win-x64 / linux-x64 / osx-arm64).
- MCP client used (Claude Code, Cursor, Cline, etc.) and version.
- The smallest C# snippet that reproduces the issue.
- Expected output vs actual output.

The most useful bug reports include a **minimal `.cs` fixture** that we can drop
into `src/LocalGraph.Tests/Fixtures/` as a regression test.

## How to suggest an enhancement

Open an issue using the **Feature request** template. Explain the use case
before the solution.

## How to contribute code

### Setup

```bash
git clone https://github.com/JavierFrauca/localgraph.git
cd localgraph
dotnet restore
dotnet test
```

Requires .NET 10 SDK.

### Workflow

1. **Open an issue first** for anything non-trivial. A 2-line typo fix doesn't
   need one; a new detection pattern does.
2. **Branch from `main`**: `feat/<short-description>` or `fix/<short-description>`.
3. **Tests first**: if you're adding a detection pattern or fixing a bug, write a
   failing test in `src/LocalGraph.Tests/` that reproduces the case, then make it
   pass. The existing test suite uses synthetic `.cs` fixtures in `Fixtures/` —
   follow the same pattern.
4. **Keep the build clean**: no warnings, all tests passing.
5. **Conventional commits**: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`,
   `chore:`. Match the existing style.
6. **Open a PR against `main`**. Reference the issue (e.g. `Closes #42`).

### Adding a new detection pattern

The most common kind of contribution. General approach:

1. **Write a failing test** in `CallSiteCoverageTests.cs` (or a new test file)
   that reproduces the C# pattern you want LocalGraph to recognize.
2. **Run the test** and confirm it fails. Inspect what the visitor currently
   extracts (the `GraphTestHarness.ParseSnippet` + dump pattern is useful here).
3. **Implement the fix**:
   - If the pattern can be resolved locally in the visitor (e.g. a new receiver
     shape), add it to `TypeReferenceVisitor.cs`.
   - If it needs global symbol information (return types of methods/properties,
     type bindings, etc.), follow the **two-pass pattern** used in Fase B:
     the visitor serializes a `PendingCallSite` or `PendingLocal`, and the graph
     resolves it in `CodeGraph.RebuildLocked`.
4. **Document the new pattern** in the README section "Patrones de call-site que
   SÍ se resuelven".
5. **Bump `ParserVersion`** in `Persistence/GraphStore.cs` if the `FileFragment`
   model changed (new fields, renamed fields, etc.). This invalidates old caches
   so users don't load stale fragments.

### Adding a new MCP tool

1. Add the tool method in `Mcp/GraphTools.cs` with `[McpServerTool]` and a
   thorough `Description` (this is what the LLM sees to decide when to call it).
2. Implement the query in `Graph/CodeGraph.cs`.
3. Add tests covering the happy path and the empty-graph case.
4. Document it in `README.md` (tabla de herramientas) and `docs/ARCHITECTURE.md`.

### Don't break the cache

If you change the shape of `FileFragment`, `TypeEdge`, `CallSite`, `DiBinding`,
`MemberSpan`, `MemberReturnSignature`, `PendingCallSite`, `PendingLocal`, or any
other record persisted by `GraphStore`, **bump `ParserVersion`**. Otherwise
users will load JSON caches from a previous version and get cryptic crashes.

## Style

- Match the surrounding code style. The codebase uses:
  - File-scoped namespaces where possible.
  - `var` for local type inference.
  - Records for value-like data.
  - XML doc comments on public APIs and non-obvious private methods.
- Comments are welcome when they explain *why*, not *what*. The codebase has
  many examples of this (search for "A.1", "B.3", "Fase" to find them).

## Releases

Releases are tagged on `main` as `vMAJOR.MINOR.PATCH`. Pushing a tag triggers
the GitHub Actions workflow that builds the three RIDs (`win-x64`, `linux-x64`,
`osx-arm64`) and publishes a GitHub Release with the binaries.

During beta, breaking changes in the MCP tool surface are OK; we'll bump MINOR.

## License

By contributing, you agree that your contributions will be licensed under the
MIT license that covers the project.
