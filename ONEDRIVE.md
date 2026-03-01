# OneDrive Integration — Setup Guide

## Overview

Fakvio can automatically upload generated PDF invoices to OneDrive.
When a user issues (completes) an invoice, the PDF is uploaded to the configured
OneDrive folder in the background.

The integration uses **OAuth 2.0** with the Microsoft identity platform
and the **Microsoft Graph REST API**. Scopes: `Files.ReadWrite offline_access`.

---

## 1. Register an App in Azure AD

1. Go to [Azure Portal — App registrations](https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade)
2. Click **+ New registration**
3. Fill in:
   - **Name**: `Fakvio OneDrive`
   - **Supported account types**: **Accounts in any organizational directory and personal Microsoft accounts** (multi-tenant + personal)
     > This allows both work/school (Microsoft 365) and personal (outlook.com) accounts.
     > If you only need work/school accounts, choose "Accounts in any organizational directory".
   - **Redirect URI**:
     - Platform: **Web**
     - URI: `https://localhost:7212/cloud-storage/callback` (for local dev)
4. Click **Register**
5. Copy the **Application (client) ID** — this is your `ClientId`

## 2. Create a Client Secret

1. In the app registration, go to **Certificates & secrets**
2. Click **+ New client secret**
3. **Description**: `Fakvio` → **Expiry**: 24 months (or your preference)
4. Click **Add**
5. **Copy the Value immediately** (you won't be able to see it again!) — this is your `ClientSecret`

> **Important**: Set a calendar reminder to rotate the secret before it expires.

## 3. Configure API Permissions

1. Go to **API permissions**
2. Click **+ Add a permission** → **Microsoft Graph** → **Delegated permissions**
3. Search and add:
   - `Files.ReadWrite` — read/write access to the user's OneDrive files
   - `offline_access` — allows token refresh (long-lived access)
4. Click **Add permissions**
5. (Optional) If you're an Azure AD admin, click **Grant admin consent** to pre-approve for all users

## 4. Add Redirect URIs

If you need additional redirect URIs (e.g. for production):

1. Go to **Authentication** in the app registration
2. Under **Web** → **Redirect URIs**, add:
   - `https://your-blazor-domain.com/cloud-storage/callback`
3. Click **Save**

> The redirect URI must match EXACTLY what the Blazor UI sends.
> The port `7212` is the default Blazor HTTPS port from `launchSettings.json`.

## 5. Configure appsettings

Add the credentials to your API configuration.

### Option A: appsettings.Development.json (local dev)

```json
{
  "OAuth": {
    "Microsoft": {
      "ClientId": "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
      "ClientSecret": "your-client-secret-value",
      "TenantId": "common"
    }
  }
}
```

> **TenantId values:**
> - `common` — multi-tenant + personal accounts (recommended)
> - `organizations` — work/school accounts only
> - `consumers` — personal Microsoft accounts only
> - `{tenant-guid}` — specific Azure AD tenant only

### Option B: User Secrets (recommended for dev)

```bash
cd Fakvio.API
dotnet user-secrets set "OAuth:Microsoft:ClientId" "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
dotnet user-secrets set "OAuth:Microsoft:ClientSecret" "your-client-secret-value"
dotnet user-secrets set "OAuth:Microsoft:TenantId" "common"
```

### Option C: Environment Variables (production)

```
OAuth__Microsoft__ClientId=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
OAuth__Microsoft__ClientSecret=your-client-secret-value
OAuth__Microsoft__TenantId=common
```

## 6. Apply Database Migration

The cloud storage settings are stored in the `CompanySystemSettings` table (master DB).
Run the migration if you haven't already:

```bash
cd Fakvio.API
dotnet ef database update --context MasterDbContext
```

## 7. Connect in the UI

1. Start both projects (API + BlazorUI)
2. Log in as **Admin** (not SysAdmin — cloud storage is per-company)
3. Go to **My Company** page (nav menu)
4. Scroll down to the **Cloud Storage** section
5. Click **Connect** on the OneDrive card
6. A popup window opens → sign in with your Microsoft account → approve access
7. The popup closes automatically → status changes to **Connected**
8. (Optional) Click **Select Folder** to choose where PDFs should be uploaded
9. Click **Test** to verify the connection works

## 8. How It Works

- When an invoice is **completed** (issued), the API automatically:
  1. Generates the PDF
  2. Uploads it to OneDrive (to the selected folder, or root if none selected)
  3. If the upload fails, it logs a warning but does NOT block the invoice workflow
- Upload method: simple PUT (for files < 4 MB — PDFs are typically well under)
- File name format: `{DocumentNumber}.pdf` (e.g. `FAK-2026-0001.pdf`)
- Only companies with `OneDriveEnabled = true` and a valid refresh token are affected
- Microsoft may return a new refresh token on each refresh — the app always stores the latest one

---

## Configuration Reference

| Key | Where | Description |
|-----|-------|-------------|
| `OAuth:Microsoft:ClientId` | `appsettings.json` or secrets | Azure AD Application (client) ID |
| `OAuth:Microsoft:ClientSecret` | `appsettings.json` or secrets | Azure AD client secret value |
| `OAuth:Microsoft:TenantId` | `appsettings.json` or secrets | `common`, `organizations`, `consumers`, or specific tenant GUID |

### Database (CompanySystemSettings — per company, auto-managed)

| Column | Description |
|--------|-------------|
| `OneDriveEnabled` | `true` when connected |
| `OneDriveAccessToken` | Short-lived access token (~1 hour) |
| `OneDriveRefreshToken` | Long-lived refresh token (auto-refreshed) |
| `OneDriveTokenExpiresAt` | When the access token expires |
| `OneDriveFolderId` | Target folder item ID (null = root) |
| `OneDriveFolderName` | Target folder display name |

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Popup doesn't open | Check browser popup blocker settings |
| "AADSTS50011: redirect_uri does not match" | The redirect URI in Azure Portal must match exactly: `https://localhost:7212/cloud-storage/callback` (check port, protocol, path!) |
| "AADSTS700016: Application not found" | Wrong ClientId — check the Application (client) ID in Azure Portal |
| "AADSTS7000215: Invalid client secret" | Secret expired or wrong value — create a new one in Azure Portal |
| "Need admin approval" | Admin consent required — ask your Azure AD admin to grant consent, or use a personal Microsoft account |
| "OAuth:Microsoft:ClientId not configured" | The ClientId is empty or missing — check appsettings / user-secrets |
| Upload fails silently | Check API logs — cloud upload errors are logged as warnings |
| Token refresh fails | User may have revoked access in Microsoft account settings → Disconnect and reconnect |

---

## Differences from Google Drive

| Aspect | Google Drive | OneDrive |
|--------|-------------|----------|
| Console | Google Cloud Console | Azure Portal |
| Scope | `drive.file` (app files only) | `Files.ReadWrite` (all user files) |
| Token revocation | Explicit API call | Token cleared locally only |
| Upload method | Multipart POST | Simple PUT |
| Token refresh | Google never rotates refresh token | Microsoft may return new refresh token |
| Tenant ID | N/A | Required (`common` for multi-tenant) |
