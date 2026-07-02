# Microsoft Graph sendMail Setup

The gateway can optionally deliver via Microsoft Graph's `sendMail` API instead of (or in addition to) SMTP
AUTH OAuth. Use this path when a tenant blocks SMTP AUTH entirely, or when the organization prefers
Graph-based application governance. It is **not** a drop-in replacement for SMTP AUTH OAuth - see
Limitations below before choosing it as your only provider.

All setup steps are performed by a Microsoft 365 / Entra administrator, outside the gateway. The gateway
only consumes the resulting tenant ID, client ID, client secret, and sender mailbox address.

## 1. Register an Entra ID application (or reuse the SMTP OAuth one)

You can reuse the same app registration created for [Microsoft 365 SMTP OAuth setup](microsoft365-setup.md),
or create a dedicated one. Either way, note the **Application (client) ID**, **Directory (tenant) ID**, and
create a **client secret** under Certificates & secrets if one doesn't already exist. Only client-secret
credentials are supported in the MVP - no certificates, delegated, or device-code flows.

## 2. Grant the `Mail.Send` application permission

1. On the app registration: **API permissions** -> **Add a permission** -> **Microsoft Graph** ->
   **Application permissions** -> select **`Mail.Send`**.
2. **Grant admin consent** for the tenant.

`Mail.Send` as an application permission is tenant-wide by default - an app holding it can send mail as
*any* mailbox in the tenant unless restricted (step 3).

## 3. Restrict the app to the dedicated sender mailbox with an Application Access Policy

Do not leave `Mail.Send` unrestricted. Scope it to exactly the one sender mailbox using Exchange Online
PowerShell (`Connect-ExchangeOnline`):

```powershell
New-ApplicationAccessPolicy -AppId <application-client-id> `
    -PolicyScopeGroupId "gateway-senders@yourtenant.onmicrosoft.com" `
    -AccessRight RestrictAccess -Description "Restrict smtp-gateway Graph app to its dedicated mailbox"
```

(The referenced mail-enabled security group must contain only the dedicated sender mailbox from your
Microsoft 365 setup.) Without this policy, a compromised client secret could send mail as any mailbox in the
tenant - this step is not optional for a production deployment.

## 4. Configure the gateway

```json
{
  "Mailbox": "gateway@yourtenant.onmicrosoft.com",
  "TenantId": "<directory-tenant-id>",
  "ClientId": "<application-client-id>",
  "ClientSecret": "<client-secret-value>"
}
```

The gateway acquires a Graph access token via client-credentials against
`https://graph.microsoft.com/.default`, using the same in-memory-only, refresh-before-expiry token caching
as the SMTP OAuth path. Access tokens and the client secret are never written to logs.

## How the gateway sends via Graph

Unlike SMTP, Graph has no MAIL FROM/RCPT TO exchange - the gateway represents the queued message as raw
MIME (preserving envelope and MIME fidelity, including attachments) and submits it in a single call:

- `POST /users/{mailbox}/sendMail` with header `Content-Type: text/plain` and the base64-encoded raw MIME
  as the request body - Graph accepts this as a full MIME message and sends it directly, returning
  `202 Accepted`.

This single call needs only the low-privilege `Mail.Send` application permission (a live test against a real
Microsoft 365 sandbox confirmed that `Mail.Send` alone returns `202 Accepted`). The gateway deliberately does
**not** use the older draft-create-then-send round trip (`POST /messages` then `POST /messages/{id}/send`),
which additionally required the much broader `Mail.ReadWrite` permission.

There is no JSON message model and no attachment upload sessions in this MVP - attachments simply travel as
part of the raw MIME body, which keeps the implementation simple and preserves MIME fidelity, at the cost of
Graph's per-recipient rejection detail (see Limitations).

> The `Mail.Send` permission above is all the gateway itself needs. The Admin TUI's **optional** mailbox
> validation probe additionally issues `GET /users/{mailbox}`, which requires a directory-read permission
> such as `User.Read.All`; grant that only if you use that probe.

## Limitations

- **`202 Accepted` is not a delivery confirmation.** The gateway marks the queue item `Sent` once Graph
  accepts the send call, but this only means Graph accepted the request for further processing - it is not
  proof the message reached the recipient's mailbox. This is a Graph API limitation, not something the
  gateway can fix.
- **No per-recipient outcome.** Unlike SMTP (where each recipient gets its own accept/reject at RCPT TO),
  Graph's `sendMail` is all-or-nothing for the whole message - every recipient in a queue item receives the
  same delivery outcome from a single Graph submission. If you need granular per-recipient retry behavior,
  use the Generic SMTP or M365 SMTP OAuth provider instead.
- **Throttling.** If Graph responds with `429` and a `Retry-After` header, the gateway honors that hint for
  scheduling the next attempt; without the header, the normal staged retry policy applies.
- **Attachment size.** Since attachments travel inside the raw MIME body rather than through upload
  sessions, very large attachments may hit Graph's message-size limits sooner than a chunked-upload approach
  would. For attachment-heavy, large-message workloads, prefer SMTP-based delivery.
- **Alias/primary-mailbox behavior.** Sending "as" an alias of the configured mailbox is not modeled - the
  gateway always sends from the exact mailbox configured, not a dynamically derived address.
