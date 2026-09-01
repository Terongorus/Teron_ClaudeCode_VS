# Phase 18 - Manual Repo Changes and VSIX Packaging Fix

**Date:** 2026-09-01

Follow-up to [Phase 17](Phase%2017%20-%20TEST-1%20and%20the%20xUnit%20Test%20Port.md). Between
sessions, the user made three manual changes directly to the repo, outside any backlog item: a C#
syntax modernization pass across most of the codebase, a relocation of `comparison-audit/` to
`docs/comparison-audit/`, and VSIX manifest branding changes. This phase verifies, fixes what broke,
and commits all of it.

## What was found

- **C# modernization (33 files)** — explicit constructors converted to primary constructors,
  `new T()`/`new List<T>{}` converted to `new()`/collection expressions, `!(x is T)` converted to
  `x is not T`, null-check-then-assign converted to `?.`/`??=`. Confirmed by diff review to be
  uniform, mechanical, Roslyn-verified, semantics-preserving syntax rewrites - no behavior changed
  anywhere in the diff.
- **Repo reorg** — `comparison-audit/` moved to `docs/comparison-audit/`, alongside the numbered
  phase docs. Git detected it as 99 pure renames (0 insertions, 0 deletions).
- **VSIX manifest branding** — `Publisher` changed from `TeronClaudeCodeVS` to `Terongorus`
  (matching the ARSENAL identity convention used across the portfolio), plus new `<License>`,
  `<GettingStartedGuide>`, `<ReleaseNotes>`, and `<Icon>` elements pointing at `LICENSE.txt`,
  `README.md`, `CHANGELOG.md`, and a new `Resources/logo_icon.png`.

## The build break, and the fix

The manifest's four new asset references were wired as WPF `<Resource>` items in the `.csproj`.
`<Resource>` embeds a file into the assembly as a BAML-style stream - it does not place the file in
the `.vsix` package, which is where the manifest's asset references actually need it. This failed a
full rebuild with:

```text
VSSDK1310: A license has been specified in the vsix but is missing from the file list or it will
not be in the expected location (LICENSE.txt) in the archive.
```

Fixed by switching `LICENSE.txt`, `README.md`, `CHANGELOG.md`, and `Resources/logo_icon.png` from
`<Resource>` to `<Content Include>` with `IncludeInVSIX=true`. Confirmed by inspecting the rebuilt
`.vsix` directly as a zip archive - all four files land inside it, alongside `extension.vsixmanifest`.

## Verification and commits

Full rebuild - `MSBuild /t:Rebuild`: 0 warnings, 0 errors, `.vsix` packaged. Full xUnit suite
(Phase 17's 182 tests) re-run against the freshly rebuilt DLL: **182/182 passing**. Split into four
commits on `dev` and pushed, per the user's explicit instruction to push only once the build was
confirmed clean with all the manual changes in place:

1. `b582fde` — relocate `comparison-audit/` under `docs/`.
2. `2470009` — the C# modernization pass.
3. `85cf1b5` — VSIX branding + the `Resource`->`Content`/`IncludeInVSIX` packaging fix.
4. `a484843` — Phase 17 (TEST-1 + the xUnit port), staged and committed alongside.

One incomplete scaffold file (`docs/comparison-audit/scripts/dte-lib.ps1`, early EnvDTE-by-PID
groundwork for TEST-2) was deliberately left uncommitted, then removed outright during a later
repo cleanup pass the same day - it had no consumer, and TEST-2 will need its own design pass
rather than inheriting scaffolding from this one.

**Files:** `TeronClaudeCodeVS.csproj`, `source.extension.vsixmanifest`, `Resources/logo_icon.png`
(new), plus the 33 modernized source files and the 99 relocated `comparison-audit` files.
