# Microsoft 365 SMTP AUTH OAuth Setup

This gateway authenticates to Microsoft 365's SMTP endpoint (`smtp.office365.com:587`, STARTTLS) using
OAuth 2.0 client credentials and XOAUTH2 - no user ever signs in interactively, and no password is stored.
Follow these steps once per tenant/mailbox before configuring `AuthMode: M365Oauth` in the gateway.

All steps below are performed by a Microsoft 365 / Entra administrator, outside the gateway. The gateway
does not provision any of this itself - it only consumes the resulting client ID, tenant ID, client secret,
and send-as mailbox address.

## 1. Create a dedicated send-mailbox

Use a mailbox dedicated to this gateway (do not reuse a real user's mailbox). This limits blast radius if
the client secret is ever compromised, and keeps delivery volume isolated from a person's mailbox limits.

## 2. Register an Entra ID application

1. Entra admin center -> **App registrations** -> **New registration**.
2. Give it a clear name (e.g. `smtp-gateway-<environment>`). No redirect URI is needed (this is a
   client-credentials-only app, never an interactive sign-in).
3. Note the **Application (client) ID** and **Directory (tenant) ID** shown on the app's Overview page.
4. **Certificates & secrets** -> **New client secret**. Copy the secret value immediately (it is not
   retrievable again later). This is the `ClientSecret` the gateway will use.

The MVP only supports client-secret credentials - no certificate-based or delegated/interactive/device-code
authentication.

## 3. Grant the `SMTP.SendAsApp` permission

1. On the app registration, go to **API permissions** -> **Add a permission** -> **APIs my organization
   uses** -> search for **Office 365 Exchange Online**.
2. Choose **Application permissions** -> select **`SMTP.SendAsApp`**.
3. **Grant admin consent** for the tenant (this step requires a Global/Application admin).

`SMTP.SendAsApp` alone does not let the app send as any mailbox - it only allows *authenticating* over SMTP
AUTH as an application. Which mailbox it may send as is controlled separately in Exchange Online (step 5).

## 4. Create the Exchange Online service principal for the app

Exchange Online needs its own service-principal record linked to the Entra app before mailbox permissions
can be assigned to it. Using Exchange Online PowerShell (`Connect-ExchangeOnline`):

```powershell
New-ServicePrincipal -AppId <application-client-id> -ObjectId <application-object-id>
```

(`ObjectId` here is the **Object ID of the app registration**, found on the same Overview page as the
Application ID - not the same as the Application ID itself.)

## 5. Grant mailbox SendAs / SMTP AUTH rights for the app

Restrict the app to exactly the one dedicated send-mailbox from step 1:

```powershell
Add-MailboxPermission -Identity "gateway@yourtenant.onmicrosoft.com" `
    -User <exchange-service-principal-object-id> -AccessRights SendAs
```

Some tenants additionally require an **Application Access Policy** to scope which mailboxes an app-only
principal may act on at all, even with `SMTP.SendAsApp` granted tenant-wide:

```powershell
New-ApplicationAccessPolicy -AppId <application-client-id> `
    -PolicyScopeGroupId "gateway-senders@yourtenant.onmicrosoft.com" `
    -AccessRight RestrictAccess -Description "Restrict smtp-gateway app to its dedicated mailbox"
```

(This requires the mailbox to be a member of the referenced mail-enabled security group.)

## 6. Confirm SMTP AUTH is enabled for the mailbox

Microsoft 365 disables SMTP AUTH tenant-wide by default on new tenants ("Security Defaults"). Enable it for
the dedicated send-mailbox specifically (do not enable it tenant-wide):

```powershell
Set-CASMailbox -Identity "gateway@yourtenant.onmicrosoft.com" -SmtpClientAuthenticationDisabled $false
```

If tenant-wide **Security Defaults** or a Conditional Access policy blocks legacy/basic authentication
protocols entirely, SMTP AUTH client-credentials access may still be blocked regardless of the mailbox
setting above - this is a tenant policy decision outside the gateway's control and must be confirmed with
the Microsoft 365 admin.

## 7. Configure the gateway

Once the four values above are known, configure the provider in `appsettings.json`:

```json
{
  "Host": "smtp.office365.com",
  "Port": 587,
  "TlsMode": "StartTlsRequired",
  "AuthMode": "M365Oauth",
  "Username": "gateway@yourtenant.onmicrosoft.com",
  "TenantId": "<directory-tenant-id>",
  "ClientId": "<application-client-id>",
  "ClientSecret": "<client-secret-value>"
}
```

The gateway acquires an access token via client-credentials against
`https://outlook.office365.com/.default`, caches it in memory only (never written to disk), and refreshes it
about 5 minutes before it expires. Access tokens and the client secret are never written to logs.

## Known limitations

- Exchange Online is not a bulk-mail system; sustained high-volume sending through a single mailbox can hit
  Microsoft 365 sending limits regardless of correct OAuth setup.
- SMTP AUTH must remain allowed for this mailbox specifically; a tenant-wide Security Defaults or Conditional
  Access change can silently break delivery without any code-level error until the next send attempt.
- If SMTP AUTH is blocked for a tenant entirely, use the Microsoft Graph `sendMail` provider instead (see
  the Graph setup docs once Phase 3b lands) rather than trying to force SMTP AUTH through.
