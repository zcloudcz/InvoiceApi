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

This file is the **only** way the site key reaches the browser: it is served verbatim from GitHub Pages, so the value has to be committed and the Pages deploy re-run (`blazorui-deploy.yml`, push to `master` or manual dispatch). That is safe — the site key is public by design, it is visible in the page source of every site using reCAPTCHA. The secret key never goes here.

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

Set this as an application setting in Azure Portal (or via CLI), on the **API and the Function App**:

```
Recaptcha__SecretKey = 6LdXXXXX...
```

Azure Functions use the same double-underscore notation for nested config.

`Recaptcha__SiteKey` in App Settings does **nothing** — the server never reads the site key. The site key reaches the browser only through step 2 above (committed into `Fakvio.BlazorUI/wwwroot/appsettings.json` and published by the Pages deploy), because `blazorui-deploy.yml` publishes that file verbatim and static Pages have no App Settings.

**Turning the gate on in production therefore takes both steps.** With `SecretKey` set but the site key still empty, the browser sends no `X-Captcha-Token`, and the fail-closed gate answers 400 to login, registration and the ARES lookup for every user. See ADMINGUIDE §9 for the two supported deployment variants.

### Deployment order (RC story, 2026-W39)

As of this writing `Recaptcha__Enabled=false` on `fakvio-api` — the gate is off in
production, and the UI's `SiteKey` is empty. Turning it on is **strictly ordered**:

1. **RC.1 (human, needs:human)** — create the reCAPTCHA v3 keys in the admin console,
   set `Recaptcha__SecretKey` and `Recaptcha__AllowedHostnames__0` on `fakvio-api`.
   Do **not** flip `Recaptcha__Enabled` yet.
2. **RC.2 (this task)** — put the site key into
   `Fakvio.BlazorUI/wwwroot/appsettings.json` and deploy it (GitHub Pages).
3. **Only then** flip `Recaptcha__Enabled=true` on `fakvio-api`.

Doing it in any other order breaks login: the gate fails closed (§4), so
`Enabled=true` with an empty `SiteKey` in the UI means the browser never sends
`X-Captcha-Token` and every login/register/ARES/forgot-password request gets 400.

## 4. Running without keys (local development)

Since issue #200 the gate **fails closed**: an empty `Recaptcha:SecretKey` no longer means "skip verification", it means "reject every gated request". Running without reCAPTCHA is now explicit:

```json
{ "Recaptcha": { "Enabled": false } }
```

Already set in `Fakvio.API/appsettings.Development.json` (`Recaptcha__Enabled`) and in the integration-test host. With `Enabled: false` no token is required and no call to Google is made, so login and registration work normally without any keys.

**Never set `Enabled: false` in production** unless you knowingly want the three anonymous endpoints unprotected — there is no rate limiting behind them.

## 5. Protected endpoints

| Endpoint | Action | Header |
|----------|--------|--------|
| `POST /api/auth/login` | `login` | `X-Captcha-Token` |
| `POST /api/auth/register` | `register` | `X-Captcha-Token` |
| `GET /api/auth/ares/{ico}` | `ares` | `X-Captcha-Token` |
| `POST /api/user/forgot-password` | `forgot_password` | `X-Captcha-Token` (RC.3) |

All four read the token from the `X-Captcha-Token` HTTP header. A missing or invalid token means 400 Bad Request — and so does a token issued for a different action, a token from a host outside `Recaptcha:AllowedHostnames` (when that list is configured), a missing `SecretKey`, and an outage at Google. Anything the server cannot positively verify is rejected.

`forgot-password` is the one endpoint where the 400 is safe to show the user distinctly
(see §8 below): it reveals only that the CAPTCHA check failed, never whether the email
address exists — the existing anti-enumeration behavior (always 200, same message) is
unchanged for every other failure.

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

## 8. UI error handling (RC.2)

A failed CAPTCHA verification (400 with `"CAPTCHA verification failed..."` in the body)
is shown as a distinct, localized message instead of the endpoint's own generic error
("Invalid email or password", "Registration failed", "ARES lookup failed", ...) — the
user did nothing wrong; the common cause is an ad blocker keeping Google's script from
loading.

- `Fakvio.UI.Shared/Services/CaptchaException.cs` — thrown by `AuthApiService`
  (login/register/ares) and `UserApiService.ForgotPasswordAsync` when the response body
  matches the server's fixed CAPTCHA-failure text.
- `Login.razor` / `Register.razor` catch it and show `Captcha_VerificationFailed`
  (CZ/EN, `SharedResource.resx`).
- `Fakvio.BlazorUI/wwwroot/index.html` `getRecaptchaToken()` — a 5s timeout guarantees an
  empty token (which the gate then rejects the same way) instead of hanging forever when
  `grecaptcha.execute()`'s promise never settles (adblock blocking a sub-resource, not the
  loader script itself).

## 9. Privacy note

reCAPTCHA v3 loads Google's script on the login/register pages and tracks user behavior. Consider adding a note to your privacy policy if required by your jurisdiction (GDPR, etc.).

The script is loaded **only** on Login and Register pages (not globally) — it's initialized via JS interop when those pages load, and only if `SiteKey` is configured.
