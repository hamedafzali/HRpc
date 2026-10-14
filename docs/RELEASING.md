# Releasing HRpc

How a new version gets from `main` to nuget.org. All commands run from the repository root.

## How publishing works

Publishing is automated by [`.github/workflows/publish.yml`](../.github/workflows/publish.yml),
triggered by pushing a `v*` tag (e.g. `v1.4.0`, `v1.4.0-preview.1`). The workflow:

1. Re-runs the full CI test matrix (net48 on Windows; net8.0, net9.0 and net10.0 on Linux and macOS).
2. Derives the package version **from the tag**. `<Version>` in `HRpc.csproj` is overridden
   via `/p:Version=` at pack time and is only a local-build convenience, but keep it in sync.
3. Runs a version guard: the tag must be strictly greater (SemVer precedence) than the
   highest version already on nuget.org.
4. Packs and pushes both `.nupkg` and `.snupkg` via **NuGet Trusted Publishing (OIDC)**: a
   GitHub-issued OIDC token is exchanged for a short-lived NuGet API key by
   `NuGet/login@v1`. No long-lived `NUGET_API_KEY` secret is stored or used.

A pre-release suffix (`-preview.1`, `-rc.1`, …) publishes as a pre-release package.

### The `NUGET_USER` pitfall

**`NUGET_USER` must be the nuget.org profile username (`afzali.hamed`), not the GitHub
username (`hamedafzali`).** `NuGet/login@v1` matches trust policies strictly by the nuget.org
account. A wrong value fails the token exchange with HTTP 401 "No matching trust policy owned
by user `<value>`". That is the same message nuget.org returns for a username that doesn't
exist, so it is easy to misdiagnose as a policy problem. It cost four failed publish attempts
to isolate. If the exchange 401s, check the username before touching the policy.

## Checklist

### 1. Prepare

- [ ] `<Version>` bumped in `HRpc.csproj` (see [Versioning](#versioning)).
- [ ] `CHANGELOG.md`: move `[Unreleased]` items under a new `## [X.Y.Z] - YYYY-MM-DD` heading.
- [ ] `README.md` and [`docs/PROTOCOL.md`](PROTOCOL.md) match the current API and wire format.
- [ ] [`docs/ROADMAP.md`](ROADMAP.md): move the shipped items to "Shipped".

### 2. Validate locally

```bash
dotnet restore HRpc.sln
dotnet test HRpc.sln -c Release
# Build first, then pack --no-build: a bare multi-target pack can fail with NU5026.
dotnet build HRpc.csproj -c Release /p:Version=<VERSION>
dotnet pack HRpc.csproj -c Release /p:Version=<VERSION> --no-build -o ./artifacts
ls -la ./artifacts
```

Optional: run the publish workflow's dry run (build, test, pack and version guard; no push,
no secret needed):

```bash
gh workflow run publish.yml -f dry_run_version=<VERSION>
```

### 3. Publish

```bash
git push origin main
git tag v<VERSION>
git push origin v<VERSION>
```

Watch the run with `gh run watch`. A tag is effectively irreversible: the version can never be
reused on nuget.org.

**Manual fallback only.** OIDC exchange works only inside GitHub Actions, so a local push
needs a real API key from nuget.org's "API Keys" page:

```bash
dotnet nuget push ./artifacts/HRpc.<VERSION>.nupkg --api-key <key> \
  --source https://api.nuget.org/v3/index.json --skip-duplicate
```

### 4. Post-publish

- [ ] The package page is live and the README renders: https://www.nuget.org/packages/HRpc
- [ ] A clean sample installs it: `dotnet add package HRpc --version <VERSION>`
- [ ] A basic connect/send/receive smoke test passes.
- [ ] Optional: create a GitHub release from the tag, with the CHANGELOG entry as its body.

## Release gates

- Tests pass in `Release` on every target framework (CI matrix green).
- The package contains the README and the expected metadata.
- Public API changes are intentional and documented in `CHANGELOG.md`.
- The version increment follows the rules below.

## Versioning

Semantic versioning, applied strictly to the public API:

| Bump  | When                                                      | Example |
|-------|-----------------------------------------------------------|---------|
| Patch | Bug fixes only. No public API added or removed.           | `1.3.1` |
| Minor | Backward-compatible features. Deprecate with `[Obsolete]`. | `1.4.0` |
| Major | Removing/renaming public API, breaking wire-protocol changes. | `2.0.0` |

Removing a public type, even an unused one, is a major-version change. Mark it `[Obsolete]`
in a minor or patch release and remove it in the next major.

## If a bad version ships

NuGet packages are immutable.

1. Deprecate (or unlist) the affected version on nuget.org.
2. Publish a fixed patch version.
3. Note the problem and the fix version in `CHANGELOG.md`.
