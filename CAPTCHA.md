# reCAPTCHA v3 Setup Guide

## How it works

reCAPTCHA v3 is **invisible** — no checkbox, no image puzzles. It scores user behavior in the background (0.0 = bot, 1.0 = human) and returns a token that the backend verifies with Google.

```
User clicks Login / Register
        │
        ▼
Blazor WASM calls JS: grecaptcha.execute(siteKey, {action: "login"})
        │
        ▼
Google returns a token (string) based on user behavior score
        │
        ▼
WASM sends POST /api/auth/login with X-Captcha-Token header
        │
        ▼
Backend calls Google: POST https://www.google.com/recaptcha/api/siteverify
  - secret = SecretKey (server-side only, never exposed to client)
  - response = token from frontend
        │
        ▼
Google returns: { success: true, score: 0.9 }
  - score >= 0.5 → allowed (likely human)
  - score <  0.5 → blocked (likely bot)
```

## 1. Create reCAPTCHA keys

1. Go to https://www.google.com/recaptcha/admin
2. Click **"+"** to create a new site
3. Fill in:
   - **Label**: `Fakvio` (or any name)
   - **reCAPTCHA type**: **Score based (v3)**
   - **Domains**: add your domain(s):
     - `localhost` (for local dev)
     - `app.fakvio.cz` (production)
     - Your Azure Static Web App domain if applicable
4. Click **Submit**
5. You'll get two keys:
   - **Site Key** (public — goes to Blazor frontend)
   - **Secret Key** (private — goes to API backend)

## 2. Configure the frontend (Site Key)

Edit `Fakvio.BlazorUI/wwwroot/appsettings.json`:

```json
{
  "ApiSettings": {
    "BaseUrl": "https://your-api-url.azurewebsites.net"
  },
  "Recaptcha": {
    "SiteKey": "6LdXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
  }
}
```

For MAUI app, edit `Fakvio.MauiApp/wwwroot/appsettings.json` the same way.

## 3. Configure the backend (Secret Key)

### Option A: appsettings.json (local dev)

Edit `Fakvio.API/appsettings.json`:

```json
{
  "Recaptcha": {
    "SiteKey": "6LdXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
    "SecretKey": "6LdXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX"
  }
}
```

### Option B: Azure App Settings (production)

Set these as application settings in Azure Portal (or via CLI):

```
Recaptcha__SiteKey = 6LdXXXXX...
Recaptcha__SecretKey = 6LdXXXXX...
```

Azure Functions use the same double-underscore notation for nested config.

## 4. Local development (no keys)

When `Recaptcha:SecretKey` is **empty** (default), verification is **skipped** on the backend. The frontend also skips token generation when `SiteKey` is empty.

This means you can develop and test locally without any reCAPTCHA keys — login and registration work normally.

## 5. Protected endpoints

| Endpoint | Action | Header |
|----------|--------|--------|
| `POST /api/auth/login` | `login` | `X-Captcha-Token` |
| `POST /api/auth/register` | `register` | `X-Captcha-Token` |

Both endpoints read the token from the `X-Captcha-Token` HTTP header. If the token is missing or invalid (and SecretKey is configured), the request is rejected with 400 Bad Request.

## 6. Score threshold

The minimum score is set in `Fakvio.Infrastructure/Service/CaptchaService.cs`:

```csharp
private const double MinScore = 0.5;
```

| Score | Meaning |
|-------|---------|
| 0.0 | Almost certainly a bot |
| 0.5 | Default threshold (balanced) |
| 0.7 | Stricter (may block some legitimate users) |
| 1.0 | Almost certainly a human |

Adjust based on your traffic patterns. Monitor scores in the [reCAPTCHA admin console](https://www.google.com/recaptcha/admin).

## 7. Architecture

```
Fakvio.BlazorUI/wwwroot/index.html     ← JS: initRecaptcha(), getRecaptchaToken()
Fakvio.BlazorUI/wwwroot/appsettings.json ← SiteKey (public)

Fakvio.UI.Shared/Components/Pages/
  Login.razor                           ← calls getRecaptchaToken("login")
  Register.razor                        ← calls getRecaptchaToken("register")

Fakvio.UI.Shared/Services/
  AuthApiService.cs                     ← sends X-Captcha-Token header

Fakvio.API/Controller/
  AuthController.cs                     ← validates token via ICaptchaService

Fakvio.Infrastructure/Service/
  CaptchaService.cs                     ← calls Google siteverify API

Fakvio.API/appsettings.json             ← SecretKey (private, server-side only)
```

## 8. Privacy note

reCAPTCHA v3 loads Google's script on the login/register pages and tracks user behavior. Consider adding a note to your privacy policy if required by your jurisdiction (GDPR, etc.).

The script is loaded **only** on Login and Register pages (not globally) — it's initialized via JS interop when those pages load, and only if `SiteKey` is configured.
