# Contributing to SmtpGateway

Thanks for your interest in improving SmtpGateway. This guide covers how to set
up a development environment, the contribution workflow, and the quality bar the
project holds itself to. It is meant to be practical, not bureaucratic - if
something here is unclear or out of date, a PR fixing it is welcome.

By participating you agree to abide by our [Code of Conduct](CODE_OF_CONDUCT.md).

## Development environment

You need the **.NET 10 SDK**. The exact version is pinned in
[`global.json`](global.json) (currently `10.0.301`, with `latestPatch`
roll-forward), so install that SDK version or a newer patch of it. The project
builds and is tested on **Windows** (it ships as a Windows service and the CI
runs on `windows-latest`).

Clone and build:

```sh
git clone https://github.com/crossvault/smtp-oauth-gateway.git
cd smtp-oauth-gateway
dotnet build
```

Run the tests:

```sh
dotnet test
```

### Tests

Tests use **xUnit v3** and run on the **Microsoft.Testing.Platform (MTP)**
runner. There are four test projects, and you can run any of them individually:

```sh
dotnet test tests/SmtpGateway.Core.Tests
dotnet test tests/SmtpGateway.Infrastructure.Tests
dotnet test tests/SmtpGateway.IntegrationTests
dotnet test tests/SmtpGateway.E2ETests
```

The first three are what CI runs and are what you should keep green.

`SmtpGateway.E2ETests` is a **live** end-to-end suite that talks to a real
Microsoft 365 tenant. It needs optional credentials, supplied either via a
`.env` file at the repo root (gitignored) or via matching process environment
variables. Copy [`.env.example`](.env.example) to `.env` and fill in the
`SMTPGATEWAY_E2E_*` values to run it. **Without credentials these tests
self-skip**, so a plain `dotnet test` is always safe and never requires a
tenant. This project is intentionally excluded from CI.

## Contribution workflow

1. **Fork** the repository.
2. Create a **branch** off `main` for your change.
3. Make your change, with tests (see the quality bar below).
4. Open a **pull request against `main`**. Fill in the PR checklist.
5. Make sure **CI passes** - PRs are gated on it.

For anything beyond a small fix, opening an issue first to discuss the approach
is appreciated and can save everyone time.

## Quality bar

This mirrors how the repo is actually built, so meeting it is what makes review
smooth:

- **Zero build warnings.** `TreatWarningsAsErrors` is enabled solution-wide, so
  a warning is a build failure. `dotnet build` must be clean.
- **Tests for behavior changes.** Any change in behavior needs test coverage;
  TDD is encouraged. Bug fixes should come with a test that fails before the fix.
- **No new dependencies without discussion.** Please raise a new NuGet package
  in an issue or the PR before adding it.
- **No secrets or PII** in code, tests, logs, fixtures, or commit history. This
  is a hard rule in this codebase - the gateway is deliberately built so that
  secrets and message content never reach logs, and contributions must uphold
  that. Never commit real tenant IDs, client secrets, passwords, or mailbox
  data; use the `.env` mechanism for live credentials.
- **Central Package Management.** Package versions live in
  [`Directory.Packages.props`](Directory.Packages.props). Do **not** put a
  `Version=` attribute on a `PackageReference` in a `.csproj`; add or update the
  `PackageVersion` entry in the central file instead.
- **CI must pass.** The build, the three product test projects, and the NuGet
  vulnerability audit all run on every PR (see
  [`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## Security issues

Please do **not** report security vulnerabilities through public issues. See
[SECURITY.md](SECURITY.md) for how to disclose them privately.
