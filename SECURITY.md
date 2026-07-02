# Security Policy

## Supported versions

SmtpGateway is a pre-1.0 project. **Only the latest release is supported** with
security fixes. If you are running an older build, please update to the current
release before reporting an issue.

## Reporting a vulnerability

Please report security vulnerabilities **privately**. Do **not** open a public
GitHub issue, pull request, or discussion for a suspected vulnerability.

Email **security@crossvault.de** with the details. Please include:

- A description of the vulnerability and the impact you believe it has.
- The affected version (the ZIP version you are running).
- Steps to reproduce, or a proof of concept, if you have one.
- Any relevant configuration (with **all secrets redacted** - never send a real
  `ClientSecret`, password, or token).
- Relevant log excerpts if available.

We aim to send an **initial response within 7 days**. We are a small team, so
please allow reasonable time for us to investigate and prepare a fix before any
public disclosure. We will keep you updated on progress.

## Scope notes

Some properties of this gateway are deliberate design decisions, documented in
[docs/security.md](docs/security.md), and are **not** vulnerabilities:

- The gateway **binds to loopback only by default** (`127.0.0.1` / `::1`) and
  refuses to start on any other address unless an operator explicitly opts in with
  `Smtp:AllowNonLoopbackBind`. Unauthenticated inbound submission **on loopback**
  is by design (reaching the listener already means running on the host). When an
  operator deliberately binds a network address, the service logs unmissable
  startup warnings, and optional inbound SMTP AUTH (`Smtp:AuthUsername` /
  `Smtp:AuthPassword`) is available and recommended - though, since the inbound
  listener has no STARTTLS, those credentials cross the network in cleartext, so
  the port should also be firewalled to trusted hosts. See
  [docs/security.md](docs/security.md).
- **Secrets live in `appsettings.json` in cleartext by documented design.** This
  is an explicit MVP simplicity decision; protect the file with normal Windows
  filesystem permissions. See [docs/security.md](docs/security.md) for the full
  secrets-handling and at-rest storage model.

Reports that a documented, intentional behavior "is insecure" without a concrete
attack that crosses one of these trust boundaries are unlikely to be treated as
vulnerabilities, but we are happy to discuss the design.

## No bounty

We do not operate a paid bug-bounty program. We genuinely appreciate responsible
disclosure and will credit reporters who wish to be acknowledged.
