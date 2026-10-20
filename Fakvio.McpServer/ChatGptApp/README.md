# ChatGPT app package (OpenAI plugin submission)

Files for submitting the hosted MCP server (`https://mcp.fakvio.cz/mcp`) to
platform.openai.com/plugins. Built from https://developers.openai.com/apps-sdk/deploy/submission
and https://developers.openai.com/plugins/build/plugins (Agent Plugins format).

## Domain verification

1. In the OpenAI portal copy the challenge token.
2. Set it as the App Service application setting **`FAKVIO_OPENAI_APPS_CHALLENGE_TOKEN`**
   on `fakvio-mcp-web`, then restart the app.
3. Check: `curl -i https://mcp.fakvio.cz/.well-known/openai-apps-challenge` returns 200,
   `text/plain`, body = token only. Without the setting it returns 404.

## Zip

Zip the *contents* of this folder (so `plugin.json` is at the zip root), from this folder:

```
Compress-Archive -Path plugin.json, mcp.json, assets -DestinationPath fakvio-plugin.zip
```

Upload the zip in the portal. Do not put credentials in it. The OAuth client is CIMD, so no
client id or secret exists; the authorization server is `https://api.fakvio.cz` (PKCE S256,
scopes `read` / `write`, no DCR).

## TODO verify

- TODO verify: `assets/icon.png` is 86x100, NOT square. OpenAI requires square, at least 48x48.
  Replace it with a square PNG before submitting.
- TODO verify: `category` ("Productivity") is a guess from the docs example; pick the allowed value in the portal.
- TODO verify: `capabilities` values `Read` / `Write` copied from the docs example.
- TODO verify: `supportURL` is `https://fakvio.cz` because the docs say HTTPS only (no `mailto:`).
- TODO verify: `auth.authorizationServerBase` and `defaultScopes` are optional overrides; the PRM
  at `/.well-known/oauth-protected-resource/mcp` already advertises both. Drop them if the portal complains.
- TODO verify: a public MCP review also requires `extensions.com.openai.review.test_cases`
  (5 positive, 3 negative) and `review.demo_recording_url`; not added here (no invented content).
  The docs also mention an optional `onboardingSkill`.
- TODO verify: `docs` URL (`https://fakvio.cz/ai-asistent.html`) has no field in the schema; use it in the portal/reviewer notes.
