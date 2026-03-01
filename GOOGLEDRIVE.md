# Google Drive Integration — Setup Guide

## Overview

Fakvio can automatically upload generated PDF invoices to Google Drive.
When a user issues (completes) an invoice, the PDF is uploaded to the configured
Google Drive folder in the background.

The integration uses **OAuth 2.0** with the `drive.file` scope (access only to
files created by this app — minimal permissions).

---

## 1. Create a Google Cloud Project

1. Go to [Google Cloud Console](https://console.cloud.google.com/)
2. Click **Select a project** (top bar) → **New Project**
3. Name it (e.g. `Fakvio`) → **Create**
4. Make sure the new project is selected

## 2. Enable Google Drive API

1. In the Cloud Console, go to **APIs & Services** → **Library**
2. Search for **Google Drive API**
3. Click it → **Enable**

## 3. Configure OAuth Consent Screen

1. Go to **APIs & Services** → **OAuth consent screen**
2. Choose **External** (unless you have Google Workspace and want internal only)
3. Fill in:
   - **App name**: `Fakvio` (or your company name)
   - **User support email**: your email
   - **Developer contact email**: your email
4. Click **Save and Continue**
5. **Scopes** → **Add or Remove Scopes** → find `https://www.googleapis.com/auth/drive.file` → check it → **Update** → **Save and Continue**
6. **Test users** → **Add Users** → add email addresses of users who will test the integration (required while app is in "Testing" status)
7. **Save and Continue** → **Back to Dashboard**

> **Note**: While the app status is "Testing", only added test users can authorize.
> For production, submit the app for verification.

## 4. Create OAuth 2.0 Credentials

1. Go to **APIs & Services** → **Credentials**
2. Click **+ Create Credentials** → **OAuth client ID**
3. **Application type**: **Web application**
4. **Name**: `Fakvio Web Client`
5. **Authorized redirect URIs** → **+ Add URI**:
   - For local development: `https://localhost:7212/cloud-storage/callback`
   - For production: `https://your-blazor-domain.com/cloud-storage/callback`

   > The redirect URI must match EXACTLY what the Blazor UI sends.
   > The port `7212` is the default Blazor HTTPS port from `launchSettings.json`.

6. Click **Create**
7. Copy the **Client ID** and **Client Secret** — you'll need them in the next step

## 5. Configure appsettings

Add the credentials to your API configuration.

### Option A: appsettings.Development.json (local dev)

```json
{
  "OAuth": {
    "Google": {
      "ClientId": "123456789-abc123def456.apps.googleusercontent.com",
      "ClientSecret": "GOCSPX-your-secret-here"
    }
  }
}
```

### Option B: User Secrets (recommended for dev)

```bash
cd Fakvio.API
dotnet user-secrets set "OAuth:Google:ClientId" "123456789-abc123def456.apps.googleusercontent.com"
dotnet user-secrets set "OAuth:Google:ClientSecret" "GOCSPX-your-secret-here"
```

### Option C: Environment Variables (production)

```
OAuth__Google__ClientId=123456789-abc123def456.apps.googleusercontent.com
OAuth__Google__ClientSecret=GOCSPX-your-secret-here
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
5. Click **Connect** on the Google Drive card
6. A popup window opens → sign in with your Google account → approve access
7. The popup closes automatically → status changes to **Connected**
8. (Optional) Click **Select Folder** to choose where PDFs should be uploaded
9. Click **Test** to verify the connection works

## 8. How It Works

- When an invoice is **completed** (issued), the API automatically:
  1. Generates the PDF
  2. Uploads it to Google Drive (to the selected folder, or root if none selected)
  3. If the upload fails, it logs a warning but does NOT block the invoice workflow
- File name format: `{DocumentNumber}.pdf` (e.g. `FAK-2026-0001.pdf`)
- Only companies with `GoogleDriveEnabled = true` and a valid refresh token are affected

---

## Configuration Reference

| Key | Where | Description |
|-----|-------|-------------|
| `OAuth:Google:ClientId` | `appsettings.json` or secrets | Google OAuth 2.0 client ID |
| `OAuth:Google:ClientSecret` | `appsettings.json` or secrets | Google OAuth 2.0 client secret |

### Database (CompanySystemSettings — per company, auto-managed)

| Column | Description |
|--------|-------------|
| `GoogleDriveEnabled` | `true` when connected |
| `GoogleDriveAccessToken` | Short-lived access token (~1 hour) |
| `GoogleDriveRefreshToken` | Long-lived refresh token (auto-refreshed) |
| `GoogleDriveTokenExpiresAt` | When the access token expires |
| `GoogleDriveFolderId` | Target folder ID (null = root) |
| `GoogleDriveFolderName` | Target folder display name |

---

## Troubleshooting

| Problem | Solution |
|---------|----------|
| Popup doesn't open | Check browser popup blocker settings |
| "redirect_uri_mismatch" error | The redirect URI in Google Console must match exactly: `https://localhost:7212/cloud-storage/callback` (check port!) |
| "access_denied" error | User is not in the test users list (while app is in Testing mode) |
| "OAuth:Google:ClientId not configured" | The ClientId is empty or missing — check appsettings / user-secrets |
| Upload fails silently | Check API logs — cloud upload errors are logged as warnings |
| Token refresh fails | User may have revoked access in Google Account settings → Disconnect and reconnect |
