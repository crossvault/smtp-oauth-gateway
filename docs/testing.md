# Testing

## Test projects

| Project | Covers |
|---|---|
| `tests/SmtpGateway.Core.Tests` | Pure domain logic in `SmtpGateway.Core`: envelope validation, the queue state resolver (`QueueItemStatusResolver`), TTL expiry policy, the retry/backoff schedule (`RetryPolicy`), the sender-rewrite decision, the sliding-window rate limiter, and the small value types/enums (`AuthMode`, `SmtpTlsMode`, `SubmitOutcome`, etc.). No infrastructure dependencies - no SQLite, no filesystem, no network. |
| `tests/SmtpGateway.Infrastructure.Tests` | The adapters: `FileSpool` (temp-write/flush/atomic-rename semantics), `SqliteQueueRepository` (enqueue, lease claiming/exclusivity, recipient status transitions, TTL sweep, discard/retry), `GatewayOptionsValidator`, the loopback/bind-endpoint parsing and validation, `SmtpErrorClassifier`, the outbound provider factory, the generic SMTP and Graph providers (against a local fake SMTP server / fake `HttpMessageHandler` - never the real network), MSAL token acquisition/caching, and a regression test for the `Microsoft.Extensions.Configuration` array-binding pitfall described in [docs/configuration.md](configuration.md). |
| `tests/SmtpGateway.IntegrationTests` | Cross-project flows that need more than one adapter wired together: the Admin TUI's full command tree exercised end-to-end (`AdminTuiCommandTests`, `ConfigCommandTests`, `ProviderTestCommandTests`) via Spectre.Console's `Spectre.Console.Testing`/`Spectre.Console.Cli.Testing` test harness, the real `SmtpGatewayListener` accepting a mail over a loopback socket, the outbound delivery worker driven against a fake SMTP server, structured/operational logging behavior, and a service-host smoke test that boots the real Generic Host composition from `SmtpGateway.Service`. |

## Running the tests

All three test projects use the native Microsoft.Testing.Platform runner
(`UseMicrosoftTestingPlatformRunner`/`TestingPlatformDotnetTestSupport`, xUnit v3) rather than the
older VSTest-based `dotnet test` pipeline. Run everything from the repository root:

```powershell
dotnet test
```

`dotnet test` still works as the entry point - `TestingPlatformDotnetTestSupport` makes the native
runner participate in the normal `dotnet test` command - so no special invocation is needed. Run a
single project the same way by pointing at its `.csproj`:

```powershell
dotnet test tests/SmtpGateway.Infrastructure.Tests/SmtpGateway.Infrastructure.Tests.csproj
```

`dotnet build`/`dotnet test` for the whole solution also enforce `TreatWarningsAsErrors=true`
(`Directory.Build.props`), so a build with any compiler warning fails outright, not just the
affected test.

CI (`.github/workflows/ci.yml`) runs `dotnet restore`, `dotnet build --configuration Release`,
`dotnet test --configuration Release --no-build`, then `dotnet list package --vulnerable
--include-transitive` as a NuGet dependency vulnerability audit, on `windows-latest` (this project
is developed and tested Windows-only: `SmtpGateway.Service` targets `net10.0-windows` for
`Microsoft.Extensions.Hosting.WindowsServices`/EventLog, `SmtpGateway.Core`/`.Infrastructure`/
`.Admin.Tui` target plain `net10.0`, and the release build in
[docs/operations.md](operations.md) publishes self-contained win-x64 binaries for the Service and
the TUI).

## Running the live E2E suite in GitHub Actions

`tests/SmtpGateway.E2ETests` sends real mail through the O365 sandbox tenant. Locally it reads a
gitignored repo-root `.env`; in CI it is excluded entirely (see `ci.yml` above). A separate
workflow, `.github/workflows/e2e.yml`, runs the live suite on demand when the repository has the
credentials configured as Actions secrets.

- **Secrets** (repository or environment secrets, same names the tests read from `.env`; for each
  key the loader prefers a non-blank `.env` value and otherwise falls back to the process
  environment variable of the same name):
  - `SMTPGATEWAY_E2E_TENANT_ID`
  - `SMTPGATEWAY_E2E_CLIENT_ID`
  - `SMTPGATEWAY_E2E_CLIENT_SECRET`
  - `SMTPGATEWAY_E2E_SENDER_MAILBOX`
  - `SMTPGATEWAY_E2E_RECIPIENT_MAILBOX`
  - `SMTPGATEWAY_E2E_RECIPIENT_MAILBOXES` (comma-separated, at least 3 mailboxes)
- **Triggers:** `workflow_dispatch` (manual) and a nightly `schedule` (03:00 UTC). There are
  deliberately **no** `push`/`pull_request` triggers - live sends must never run per-PR, and fork
  PRs have no access to secrets. A `concurrency` group with `cancel-in-progress: false` prevents two
  live runs from hitting the tenant at once.
- **Neutral-skip when unconfigured:** a first gate step checks whether the tenant-id env var
  (mapped from the secret) is empty; if so it logs `E2E secrets not configured - skipping live run`
  and every subsequent step is skipped, so the job ends green rather than red. This is required
  because a job-level `if:` cannot read the `secrets` context, and because an all-skipped
  Microsoft.Testing.Platform `dotnet test` run exits with code 8 (zero tests executed) - which would
  fail the job - so the test step must not run at all when the secrets are absent.

The `.env`-over-environment precedence logic itself is covered by real unit tests in
`CredentialResolutionTests`/`EnvFileTests` (they inject fake values, so they never touch a real
`.env` or the live tenant and never skip). Because they live in the E2ETests project, `ci.yml` does
not run them; they run locally and in `e2e.yml`.

## The 75% coverage gate

The project's coverage bar is a **75% line-coverage floor**, not a 90%+ target. This is a
deliberate choice: a high nominal percentage is easy to game by writing trivial tests for
getters/DTOs/pass-through code just to move the number, which adds maintenance cost without adding
confidence. 75% is treated as a *gate* - a check that catches an obviously undertested change - not
a *replacement* for judgment about which scenarios actually matter (state-machine transitions,
retry/backoff edges, lease-exclusivity races, TLS/auth failure classification, and the Admin TUI's
command surface are all covered explicitly because they are exactly the kind of logic that silently
breaks, not because a coverage tool demanded it).

`Microsoft.Testing.Extensions.CodeCoverage` is referenced by all three test projects and is the
mechanism available for collecting coverage locally or in CI if you want to check the current
number; it is not wired into `ci.yml` as an enforced failing gate today - see the note below.

## TDD-first approach

Development of every phase of this codebase followed a red-green-refactor cycle: a failing test is
written first to pin down the intended behavior (a state transition, a validation rule, a retry
decision), then the minimal implementation is added to make it pass, then the test/implementation
pair is refactored for clarity once green. This is why the domain logic in `SmtpGateway.Core` in
particular reads as a set of small, single-purpose, pure functions/types (`RetryPolicy`,
`QueueItemStatusResolver`, `QueueItemExpiryPolicy`, `SenderRewritePolicy`,
`SlidingWindowRateLimiter`) - each one exists because a test needed an isolated, infrastructure-free
unit to assert against, and the type boundary follows the test boundary rather than the reverse.

## Known gap: coverage is not currently enforced as a CI failure condition

`.github/workflows/ci.yml`'s `Test` step runs `dotnet test --configuration Release --no-build` with
no coverage-collection flags and no threshold check, and no separate coverage step exists in the
workflow. In other words, the 75% line-coverage floor described above is the project's *documented
intent*, but as of this writing it is not yet wired into CI as something that can fail a build - a
pull request that drops coverage below 75% would not currently be blocked automatically. Treat the
75% figure as the target to check manually (using the `Microsoft.Testing.Extensions.CodeCoverage`
collector already referenced by each test project, via its Microsoft.Testing.Platform coverage
options) until an enforced CI gate is added.
