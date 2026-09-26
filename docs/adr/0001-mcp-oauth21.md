# ADR 0001 — OAuth 2.1 autorizace MCP hostu `mcp.fakvio.cz`

- **Stav:** Proposed (čeká na schválení ownerem; implementační tasky N5.2–N5.8 se nespouštějí před „Accepted“)
- **Datum:** 2026-09-26
- **Story / task:** N5 / N5.1 (`research/plan-2026-W39-specs.md`)
- **Dotčené projekty:** `Fakvio.API`, `Fakvio.Infrastructure`, `Fakvio.Domain`, `Fakvio.McpServer`, `Fakvio.UI.Shared`, `Fakvio.BlazorUI`

---

## 1. Kontext

### 1.1 Proč

claude.ai (web, desktop, mobil, Cowork) a ChatGPT přidávají vzdálené MCP servery jako „custom connector“
zadáním URL a OAuth přihlášením. Fakvio HTTP host (`https://mcp.fakvio.cz/mcp`) dnes přijímá jen API klíč
v hlavičce `Authorization` (`Fakvio.McpServer/Http/McpApiKeyMiddleware.cs:42-68`), který do claude.ai
běžný uživatel zadat nemůže (hlavičky jsou v Claude jen beta pro Ownery organizací —
[Claude docs][claude-auth]). Bez OAuth je vzdálené MCP pro cílovou skupinu (živnostníci na mobilu/webu)
prakticky nepoužitelné. DEVGUIDE §4.9 to dnes vede jako „Mimo scope (story #144)“.

### 1.2 Současný stav (ověřeno v kódu, `origin/develop` 2026-09-26)

| Oblast | Stav | Kde |
|---|---|---|
| Hosting | API i MCP host běží na **Azure App Service** (`fakvio-api`, `fakvio-mcp-web`, plán `asp-fakvio-b1`); žádné Azure Functions. Test env zrušen v #426 (ADMINGUIDE §9 ho ještě zmiňuje — zastaralé). | DEVGUIDE §1.2, ADMINGUIDE §9 ř. 575-600, CLAUDE.md ř. 43 |
| Veřejné adresy | UI `app.fakvio.cz` (Blazor WASM na Static Web Apps), API `https://fakvio-api.azurewebsites.net` (bez vlastní domény), MCP `https://mcp.fakvio.cz` | `Fakvio.BlazorUI/wwwroot/appsettings.json:4,12` |
| API klíče | `fak_live_…`, SHA-256 hash v master tabulce `ApiKey`, scopes `read` / `read,write`, efektivní oprávnění **role ∩ scope**, soft revokace | `Fakvio.Domain/Entities/ApiKey.cs`, DEVGUIDE §2.9 |
| Autentizace | Policy scheme `FakvioBearer` vybírá podle prefixu `fak_` (API klíč) vs. JWT | `Fakvio.Infrastructure/DependencyInjection/AuthenticationExtensions.cs:121`, `ApiKeyAuthenticationDefaults.cs:24,30` |
| Scope guard | `ApiKeyRequestGuard.GetDenialReason` — ne-GET vyžaduje `write`; `/api/api-key*` (kromě `/me`) klíčem nejde | `Fakvio.Infrastructure/Authentication/ApiKeyRequestGuard.cs` |
| MCP gate | Každý request → `GET /api/api-key/me` přes tentýž credential, **bez cache** (okamžitá revokace); 401 + `WWW-Authenticate: Bearer` | `McpApiKeyMiddleware.cs:42-89` |
| MCP host → API | MCP host credential volajícího **přeposílá** na API (`AuthHeaderHandler`, `HttpContextApiTokenProvider`) | DEVGUIDE §4.9 |
| Uživatel ↔ firma | `User.CompanyId` je **jedna** firma (nullable; SysAdmin bez firmy). Multi-membership neexistuje. | `Fakvio.Domain/Entities/User.cs:46` |
| Login v UI | JWT v `localStorage` (WASM), SSO (Google/MS/FB/Seznam), 2FA; po přihlášení vždy navigace na `/` — **žádný `returnUrl`** | `Login.razor:187,237`, `CustomAuthenticationStateProvider.cs` |
| Audit | Žádná audit tabulka; `BaseEntity.CreatedBy/UpdatedBy` + DB logy (`LogCleanupService` maže jen Debug/Info/Trace > 48 h, Warning+ zůstává) | `LogCleanupService.cs:63` |
| Rate limiting | Nepoužívá se nikde | grep `AddRateLimiter` = 0 |
| SSRF guard (#438) | Jen IP literál + loopback, bez DNS resolve | `Fakvio.McpServer/Tools/ReceivedInvoiceTools.cs:275-278` |
| SDK | `ModelContextProtocol.AspNetCore` 2.2.0 obsahuje `McpAuthenticationHandler` (servíruje protected-resource metadata a doplňuje `resource_metadata` do challenge) | NuGet 2.2.0 XML docs; [csharp-sdk releases][sdk-rel] |

### 1.3 Co vyžaduje spec a klienti (ověřeno 2026-09-26)

**MCP spec 2026-07-28** ([authorization][spec-auth], [discovery][spec-disc], [client registration][spec-reg],
[security considerations][spec-sec], [changelog][spec-chg]):

- MCP server **MUST** publikovat Protected Resource Metadata (RFC 9728) s `authorization_servers`;
  discovery přes `WWW-Authenticate: Bearer resource_metadata="…"` na 401 **nebo** well-known
  (`/.well-known/oauth-protected-resource/mcp`, pak `/.well-known/oauth-protected-resource`).
- AS **MUST** mít RFC 8414 metadata nebo OIDC discovery; `code_challenge_methods_supported` musí
  obsahovat `S256`, jinak klient odmítne pokračovat.
- Registrace klientů: pre-registrace → **CIMD** (SHOULD) → DCR (**deprecated** od 2026-07-28, jen zpětná kompatibilita).
  CIMD: AS **MUST** ověřit `client_id` v dokumentu == URL, validovat `redirect_uri` proti dokumentu,
  **MUST** jasně zobrazit hostname redirect URI, SHOULD varovat u loopback-only, SHOULD řešit SSRF.
- `resource` (RFC 8707) klient posílá vždy; MCP server **MUST** přijímat jen tokeny vydané pro sebe
  a **MUST NOT** přeposílat přijatý token upstream (token passthrough) — viz §4.4, vědomá interpretace.
- Veřejní klienti: AS **MUST** rotovat refresh tokeny; access tokeny SHOULD krátkodobé.
- RFC 9207 `iss` v autorizační odpovědi: SHOULD (budoucí MUST), s `authorization_response_iss_parameter_supported: true`.
- Nedostatečný scope: 403 + `WWW-Authenticate: Bearer error="insufficient_scope", scope=…` (step-up).

**Claude** ([Authentication for connectors][claude-auth], [Lazy authentication][claude-lazy]):

- Sign-in startuje **jen** na HTTP 401 s `resource_metadata`; bere **první** položku `authorization_servers`.
- CIMD použije jen když AS metadata mají `client_id_metadata_document_supported: true` **a** `"none"`
  v `token_endpoint_auth_methods_supported`; jinak padá na DCR (`registration_endpoint`).
- Redirect URI hosted aplikací: `https://claude.ai/api/mcp/auth_callback`. Claude Code: loopback
  `http://localhost:<port>/callback` a `http://127.0.0.1:<port>/callback`, port-agnostická shoda;
  Claude Code CIMD: `https://claude.ai/oauth/claude-code-client-metadata`. URL CIMD dokumentu hosted
  aplikací v docs uvedena není **(neověřeno — zjistit z logu při E2E, N5.8)**.
- PKCE S256 vždy; `offline_access` přidá, jen když ho AS uvádí ve `scopes_supported`.
- Token endpoint: `application/x-www-form-urlencoded`, `invalid_grant` pro neplatný refresh, rotace
  s novým refresh tokenem ve stejné odpovědi; timeout 10 s (discovery/registrace/token), 30 s refresh;
  proaktivní refresh až 5 min před expirací.
- Discovery cache ~5 min globálně per URL. Egress Anthropicu `160.79.104.0/21`.
- Na consent obrazovce jmenovat **host `client_id` URL**, ne self-asserted `client_name`.

**ChatGPT** ([Apps SDK auth][openai-auth], [ChatGPT Learn MCP][openai-learn]):

- Preferuje CIMD (public client `none` nebo `private_key_jwt`), fallback DCR, případně předdefinovaný klient.
- Redirect: stabilní `https://chatgpt.com/connector_platform_oauth_redirect` — **vyžaduje**
  `authorization_response_iss_parameter_supported: true` a `iss` ve všech odpovědích; jinak
  `https://chatgpt.com/connector/oauth/{callback_id}`.
- `resource` posílá v authorize i token requestu, očekává ho v `aud` tokenu.
- Codex: CIMD `https://chatgpt.com/oauth/codex/…/client.json`, loopback `http://127.0.0.1:<port>/callback/<id>`.

---

## 2. Rozhodnutí (souhrn)

1. **Vlastní minimální autorizační server v `Fakvio.API`** (varianta A), bez nové knihovny.
2. **Access token = opaque `fak_oat_…`**, uložený jako krátkodobý řádek v existující tabulce `ApiKey`
   (nový sloupec `OAuthGrantId`). Celá stávající pipeline (`FakvioBearer`, `ApiKeyAuthenticator`,
   `ApiKeyRequestGuard`, MCP gate přes `/api/api-key/me`, okamžitá revokace) platí beze změny.
3. Tok **authorization code + PKCE S256**, **refresh token s rotací a detekcí reuse** (rodina = grant),
   revokace RFC 7009. Žádný implicit, žádný `client_credentials`, žádný password grant.
4. **CIMD ano, DCR ne** (v1). DCR až jako podmíněný task, pokud E2E ukáže klienta, který CIMD neumí.
5. Metadata: RFC 9728 na `mcp.fakvio.cz`, RFC 8414 na issueru, `resource_metadata` v 401 challenge, RFC 9207 `iss`.
6. **Consent obrazovka ve WASM** (`app.fakvio.cz/oauth/consent`), volba **read / read+write**, zobrazení
   host `client_id`, host `redirect_uri`, firmy a e-mailu uživatele.
7. Scopes `read`, `write` (write ⊃ read) mapované 1:1 na `ApiKey.Scopes` → **role ∩ scope** přes `ApiKeyRequestGuard`.
8. **Audience vázaná** na `https://mcp.fakvio.cz/mcp`: OAuth tokeny API přijme **jen z MCP hostu**
   (interní „resource proof“ hlavička, §4.4). Uniklý token tedy nejde použít přímo proti API.
9. **Feature flag `McpOAuth:Enabled` (default off) + allowlist uživatelů/firem**, ověření na produkci.
10. **API klíče (stdio i HTTP) zůstávají beze změny** a jsou nadále plnohodnotnou cestou.

---

## 3. Varianty

| | A: vlastní AS v `Fakvio.API`, opaque tokeny v `ApiKey` (**doporučeno**) | B: vlastní AS, JWT access token s `aud` | C: OpenIddict v `Fakvio.API` | D: externí IdP (Auth0 / Clerk / WorkOS / Entra External ID / Keycloak) |
|---|---|---|---|---|
| Diff | ~4 malé tabulky/sloupec, 1 controller, 1 service, 1 resolver, 1 stránka | jako A + podpisové klíče, JWKS, druhý validační stack | 4–5 tabulek OpenIddict, nové auth schéma vedle `FakvioBearer`, vlastní handlery pro CIMD | federace Fakvio loginu do IdP nebo migrace uživatelů; Fakvio jako OIDC provider pro IdP |
| Revokace | **okamžitá** (MCP gate volá `/me` každý request, řádek `RevokedAt`) | jen po expiraci nebo introspekcí (= stejný round-trip jako A, JWT nic nešetří) | okamžitá s reference tokeny / introspekcí | závisí na IdP; typicky až po expiraci access tokenu |
| CIMD | vlastní resolver (potřeba tak jako tak) | vlastní | **není nativně** (neověřeno); vlastní event handler pro resolve klienta | Clerk CIMD od 2026-08-05 ([Clerk changelog][clerk]); ostatní různě |
| Scopes / role ∩ scope | beze změny (`ApiKeyRequestGuard`) | nový mapping claimů | nový mapping | nový mapping + synchronizace rolí |
| Login / 2FA / SSO | stávající (WASM + API) | stávající | stávající přes passthrough mód | duplicitní nebo federovaný |
| Provoz | nic nového | správa a rotace podpisových klíčů | upgrade knihovny (8.0 s .NET 10 jen preview — [releases][openiddict], datum neověřeno) | cena per MAU, vendor lock-in, data mimo EU (GDPR), nový SPOF |
| Bezpečnostní riziko | vlastní protokolový kód → nutný security review (N5.8) a testy per hrozba | jako A + chyby ve validaci JWT | nižší v protokolu, vyšší v integraci (dvě pipeline) | nejnižší v protokolu, nejvyšší v integraci identit |

**Proč A.** Fakvio už má 90 % toho, co AS potřebuje: uživatele, login s 2FA/SSO, hashované
vysokoentropické credentialy, scopes, revokaci a per-request validaci na MCP hostu. Chybí jen protokolová
vrstva (authorize, token, revoke, metadata, CIMD) — ta je v rozsahu MCP profilu OAuth 2.1 malá
(jeden grant type, jeden typ klienta: public + PKCE). OpenIddict by přinesl druhou autentizační pipeline
a CIMD by stejně musel být psán ručně; externí IdP řeší problém, který nemáme (identity), a zhoršuje ten,
který máme (revokace, data, cena). JWT (B) nic nešetří, protože MCP gate volá API na každý request kvůli
okamžité revokaci (zákaz cache ze story #144).

---

## 4. Návrh

### 4.1 Adresy a issuer

| Co | URL |
|---|---|
| Resource (canonical, RFC 8707) | `https://mcp.fakvio.cz/mcp` |
| Protected Resource Metadata | `https://mcp.fakvio.cz/.well-known/oauth-protected-resource/mcp` **i** `…/.well-known/oauth-protected-resource` |
| Issuer (AS) | **`https://api.fakvio.cz`** — nová vlastní doména na `fakvio-api` (CNAME u Forpsi + managed cert) |
| AS metadata | `https://api.fakvio.cz/.well-known/oauth-authorization-server` |
| Endpointy | `GET /oauth/authorize`, `POST /oauth/token`, `POST /oauth/revoke` (na API) |
| Consent | `https://app.fakvio.cz/oauth/consent?ticket=…` (WASM) |

Issuer na `*.azurewebsites.net` nedoporučuji: issuer je identita, na kterou si klienti klíčují stav
(SEP-2352), změna později = všichni klienti znovu; a uživatel na consent/loginu vidí cizí doménu.
Pokud owner vlastní doménu nechce, funguje i `https://fakvio-api.azurewebsites.net` (otevřená otázka Q1).

AS metadata (za flagem, jinak 404):

```json
{
  "issuer": "https://api.fakvio.cz",
  "authorization_endpoint": "https://api.fakvio.cz/oauth/authorize",
  "token_endpoint": "https://api.fakvio.cz/oauth/token",
  "revocation_endpoint": "https://api.fakvio.cz/oauth/revoke",
  "response_types_supported": ["code"],
  "grant_types_supported": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_methods_supported": ["none"],
  "revocation_endpoint_auth_methods_supported": ["none"],
  "code_challenge_methods_supported": ["S256"],
  "scopes_supported": ["read", "write"],
  "client_id_metadata_document_supported": true,
  "authorization_response_iss_parameter_supported": true
}
```

Protected Resource Metadata: `resource` = `https://mcp.fakvio.cz/mcp`, `authorization_servers` =
`["https://api.fakvio.cz"]`, `scopes_supported` = `["read", "write"]`, `bearer_methods_supported` = `["header"]`.
`offline_access` se **neuvádí** (spec: RS ho nemá uvádět); refresh token AS vydává veřejným klientům vždy.

### 4.2 Toky

**Authorize** (`GET /oauth/authorize`, anonymní):
1. Validuje: `response_type=code`, `client_id` (CIMD resolve §4.6), `redirect_uri` **přesná shoda** s dokumentem
   (loopback: shoda bez portu pro `127.0.0.1`, `[::1]`, `localhost`), `code_challenge` + `code_challenge_method=S256`
   (plain odmítnout), `resource` == canonical URL (chybí → dosadit canonical; jiná hodnota → `invalid_target`),
   `scope` ⊆ {read, write} (prázdný → read+write jako „požadováno“, uživatel volí).
2. Chyba v `client_id` nebo `redirect_uri` → **nikdy nepřesměrovat**, chybová stránka na Fakvio doméně.
   Ostatní chyby → redirect na ověřený `redirect_uri` s `error`, `state`, `iss`.
3. Parametry zabalí do **ticketu** chráněného ASP.NET Data Protection (`ITimeLimitedDataProtector`,
   platnost 10 min; DP klíče jsou perzistované — DEVGUIDE §2.7) a přesměruje na
   `app.fakvio.cz/oauth/consent?ticket=…`. Žádná tabulka pro rozpracované požadavky.

**Consent** (WASM stránka, vyžaduje přihlášení):
1. Nepřihlášený → uloží ticket do `sessionStorage`, login (heslo / SSO / 2FA), po přihlášení
   `Login.razor` / `AuthCallback.razor` vrátí na consent (nový mechanismus, jen pro tuto jednu cestu — žádný obecný `returnUrl`, aby nevznikl open redirect v UI).
2. Stránka si ticket nechá popsat API (`GET /api/oauth/consent/{ticket}` s JWT) → host `client_id`,
   `client_name`, host `redirect_uri`, příznak „ověřený klient“ (allowlist), varování u loopback-only,
   požadované scopes, firma a e-mail uživatele.
3. Uživatel zvolí **Jen čtení** (výchozí) nebo **Čtení i zápis** a klikne Povolit / Zamítnout →
   `POST /api/oauth/consent/decision` (JWT v hlavičce, ne cookie → CSRF se neuplatní).
4. API vydá **authorization code** (32 B CSPRNG, uložen hash, TTL **60 s**, jednorázový) a vrátí WASM
   cílovou URL `redirect_uri?code=…&state=…&iss=https://api.fakvio.cz`; zamítnutí → `error=access_denied`.
   WASM provede `NavigateTo(url, forceLoad: true)`.

**Token** (`POST /oauth/token`, `application/x-www-form-urlencoded`, public client, bez secretu):
- `authorization_code`: najde kód podle hashe; ověří `client_id`, `redirect_uri` shodu, `code_verifier`
  (SHA-256 → base64url == challenge, constant-time), `resource`, expiraci, `ConsumedAt IS NULL`
  (atomicky `UPDATE … SET ConsumedAt WHERE ConsumedAt IS NULL`). Opakované použití kódu → `invalid_grant`
  **a revokace grantu vydaného z tohoto kódu** (OAuth 2.1 §4.1.3). Vytvoří `OAuthGrant`, access token, refresh token.
- `refresh_token`: najde podle hashe. Platný a nepoužitý → označí `ConsumedAt`, vydá nový refresh
  (stejný grant) + nový access token. **Už použitý** refresh token → **revokace celého grantu**
  (všechny refresh i access tokeny, `RevokedReason=RefreshReuse`), Warning log, `invalid_grant`.
- Odpověď: `access_token`, `token_type=Bearer`, `expires_in=3600`, `refresh_token`, `scope` (skutečně udělený),
  `Cache-Control: no-store`. Chyby jen RFC 6749 kódy bez interních detailů.

**Revoke** (`POST /oauth/revoke`, RFC 7009): refresh token → revokace grantu; access token → revokace řádku;
neznámý token → 200 (RFC 7009 §2.2).

**Životnosti**

| Artefakt | TTL | Poznámka |
|---|---|---|
| Consent ticket | 10 min | Data Protection, bez DB |
| Authorization code | 60 s | jednorázový |
| Access token | 1 h | Claude refreshuje ≤ 5 min předem |
| Refresh token | 30 dní klouzavě | každá rotace posune; absolutní strop grantu 180 dní (Q4) |

### 4.3 Datový model (master schema, vzor `ApiKey` — DEVGUIDE §2.9)

Master schema ze stejného důvodu jako `ApiKey`: token se musí najít **před** určením tenanta.

- `ApiKey` + **`OAuthGrantId` (long?, FK → `OAuthGrant`, cascade)**. `null` = ručně vytvořený klíč.
  OAuth access token: `KeyPrefix` `fak_oat_…`, `Name` = host klienta, `Scopes` z grantu, `ExpiresAt` +1 h.
  Seznam klíčů na Integracích filtruje `OAuthGrantId IS NULL`. Samostatný sloupec `Source` není potřeba.
- **`OAuthGrant`**: `UserId`, `ClientId` (URL), `ClientName` (snapshot), `Scopes`, `Resource`,
  `CreatedAt`, `LastUsedAt`, `ExpiresAt` (absolutní strop), `RevokedAt`, `RevokedByUserId`,
  `RevokedReason` (`User`, `RefreshReuse`, `CodeReuse`, `Admin`, `Superseded`).
  Nový souhlas téhož uživatele pro týž `client_id` a `resource` **nahradí** předchozí grant (`Superseded`),
  aby se Integrace nezaplnily při každém „Reconnect“.
- **`OAuthAuthorizationCode`**: `CodeHash` (unique), `UserId`, `ClientId`, `ClientName`, `RedirectUri`,
  `CodeChallenge`, `Scopes`, `Resource`, `ExpiresAt`, `ConsumedAt`, `GrantId?` (po uplatnění, pro revokaci při reuse).
- **`OAuthRefreshToken`**: `TokenHash` (unique), `GrantId` (FK, cascade), `ExpiresAt`, `ConsumedAt`, `CreatedAt`.
- **Žádná tabulka klientů** (CIMD dokumenty jen v `IMemoryCache`, §4.6). Přijde až s DCR.

Hashování všech tří typů tokenů: `ApiKeyService.ComputeHash` (SHA-256, base64) — zdůvodnění z DEVGUIDE §2.9
platí (32 B z CSPRNG, indexovatelný equality lookup). Raw hodnoty se nikdy neukládají ani nelogují
(do logu jen prvních 12 znaků). Pozor na `EnableLegacyTimestampBehavior` u všech `ExpiresAt` (DEVGUIDE §2.10, `ApiKeyAuthenticator.ToUtc`).

**Úklid:** nový `OAuthCleanupService : BackgroundService` (vzor `LogCleanupService`, 1× za hodinu):
maže OAuth řádky `ApiKey` s `ExpiresAt < now − 1 den`, kódy starší 1 den, refresh tokeny po expiraci
nebo 1 den po `ConsumedAt` (ponechat krátce kvůli detekci reuse — viz Q5), granty revokované/expirované
před > 90 dny. Jeden aktivní klient ≈ 24 access tokenů denně na uživatele — bez úklidu tabulka roste.

### 4.4 Audience, resource indicator a „token passthrough“

Spec říká, že MCP server smí přijmout jen token vydaný pro sebe a **nesmí ho přeposlat upstream**.
Fakvio MCP host je ale záměrně bezstavová fasáda nad `Fakvio.API` (nemá DB ani klíče — DEVGUIDE §4.9)
a token přeposílá. Řešení a interpretace:

- **Logicky je chráněný zdroj `https://mcp.fakvio.cz/mcp` = MCP host + API za ním**, oba pod jedním AS
  a jedním vlastníkem. API není „third-party upstream“, ale backend téhož resource serveru.
  Validaci tokenu (hash lookup, expirace, revokace, audience) dělá API, MCP host ji vynucuje přes `/me`.
- Aby tokeny s audience MCP **nefungovaly přímo proti API** (to by byl přesně confused-deputy / passthrough
  problém), API přijme `fak_oat_` token **jen s interní hlavičkou `X-Fakvio-Resource-Proof`**, kterou
  přidává výhradně MCP host (`AuthHeaderHandler`) — sdílené tajemství `McpOAuth:ResourceProofSecret`
  (app setting na obou web appech, Key Vault reference), porovnání constant-time. Bez hlavičky → 401.
  Uniklý OAuth token je tak použitelný jen přes MCP host (tj. jen MCP nástroji), ne na celé REST API.
- `/api/api-key/me` vrací nově i `Resource` grantu; MCP gate odmítne token, jehož `Resource` ≠ vlastní
  canonical URL (příprava na víc resource serverů).
- **API klíče `fak_live_` zůstávají platné všude** (API i MCP) — jsou to uživatelem ručně vydané
  osobní tokeny pro celé API, ne tokeny od AS pro MCP audience. Vědomá výjimka z „MCP server přijímá jen
  tokeny svého AS“; MCP autorizace je pro HTTP podle spec volitelná a API klíč je druhý, explicitní režim.

### 4.5 Scopes a role

- Scopes `read`, `write`; `write` se normalizuje na `read,write` stejně jako u API klíčů. Udělený scope
  zvolí **uživatel** na consentu (může být užší než požadovaný) a vrací se v `scope` token odpovědi.
- Vynucení beze změny: `ApiKeyRequestGuard` (ne-GET chce `write`, default deny, `SafeMethodOverridePaths`).
  Efektivní oprávnění = role uživatele ∩ scope.
- Guard rozšířit: OAuth principal (claim `oauth_grant_id`) nesmí na `/api/oauth/grants*` (správa grantů
  jen JWT — stejný důvod jako u `/api/api-key`).
- Step-up (403 `insufficient_scope`) **v1 ne**: zápisový nástroj s read-only tokenem vrátí nástrojovou
  chybu s textem „připojení je jen pro čtení, odpojte a připojte znovu se zápisem“. HTTP 403 by vyžadoval
  předvalidaci názvu nástroje v MCP gate (vzor [lazy auth][claude-lazy]) — přidat, až bude poptávka.
- 401 challenge: `WWW-Authenticate: Bearer resource_metadata="https://mcp.fakvio.cz/.well-known/oauth-protected-resource/mcp", scope="read write"`.

### 4.6 Registrace klientů: CIMD (+ DCR jen podmíněně)

`IOAuthClientResolver` v Infrastructure:

- `client_id` musí být `https` URL s cestou, bez fragmentu, userinfo a query; port jen 443.
- **Allowlist hostů klientů** `McpOAuth:TrustedClientHosts` (výchozí `claude.ai`, `chatgpt.com`).
  Během early access se klienti mimo allowlist **odmítnou** (Q3). Klienti z allowlistu mají na consentu
  štítek „ověřená aplikace“.
- Stažení dokumentu (SSRF guard, platí i pro allowlistované hosty):
  - vlastní `HttpClient` se `SocketsHttpHandler.ConnectCallback`: resolve DNS, **odmítnout** loopback,
    privátní (10/8, 172.16/12, 192.168/16), link-local (169.254/16, fe80::/10), CGNAT 100.64/10, ULA fc00::/7,
    multicast, unspecified, IPv4-mapped IPv6 těchto rozsahů, Azure metadata 169.254.169.254 a 168.63.129.16;
    připojit se **na ověřenou IP** (brání DNS rebinding TOCTOU);
  - `AllowAutoRedirect = false` (redirect = chyba), timeout 5 s, max 64 kB (čtení streamem s limitem),
    `Content-Type` JSON;
  - negativní cache 5 min a max 1 souběžný fetch per `client_id` (ochrana proti zneužití AS jako skeneru).
- Validace dokumentu: `client_id` == URL přesně, `client_name` a `redirect_uris` přítomné, každý
  `redirect_uri` je `https` nebo loopback `http` (`127.0.0.1`, `[::1]`, `localhost`); `token_endpoint_auth_method`
  chybí nebo `none` (jiné metody v1 nepodporujeme → `invalid_client`).
- Cache v `IMemoryCache` podle `Cache-Control` (min 5 min, max 24 h). Při více instancích stáhne každá
  instance sama — přijatelné.
- **DCR (RFC 7591) v1 neimplementujeme.** Claude i ChatGPT CIMD umí a použijí ho, pokud AS metadata
  obsahují oba příznaky (§1.3). DCR je deprecated, vytváří neomezené množství klientů a otevírá anonymní
  zápisový endpoint. Pokud E2E (N5.8) ukáže podstatného klienta bez CIMD, přidá se task „DCR fallback“
  (tabulka `OAuthClient`, `POST /oauth/register`, rate limit, stejné validace redirectů) — Q2.

### 4.7 Multi-tenant

- Token nese **uživatele**, tenant se odvozuje z `User.CompanyId` při každé autentizaci (jako API klíče,
  DEVGUIDE §2.9) — přesun uživatele mezi firmami token následuje.
- Protože uživatel má dnes **právě jednu firmu**, consent firmu **zobrazí** (název + IČO), ale výběr
  nenabízí. Až vznikne členství ve více firmách, grant dostane `CompanyId` a consent picker — ne dřív (YAGNI).
- **SysAdmin a uživatelé bez `CompanyId` OAuth grant nedostanou** (consent odmítne). Navíc
  `ImpersonationMiddleware` bude `X-Company-Id` ignorovat pro OAuth principal (claim `oauth_grant_id`) —
  impersonace přes token třetí strany je nežádoucí (u API klíčů zůstává dnešní chování).
- Deaktivovaný uživatel: `ApiKeyAuthenticator` už dnes odmítá → platí i pro OAuth tokeny; token endpoint
  při refreshi kontroluje `User.IsActive` a revokuje grant.

### 4.8 Revokace na stránce Integrace

- `GET /api/oauth/grants` (jen vlastní, jen JWT), `POST /api/oauth/grants/{id}/revoke` → nastaví
  `RevokedAt` grantu, všech jeho refresh tokenů a **všech jeho `ApiKey` řádků** najednou → další MCP request
  s tokenem vrátí 401 okamžitě (gate bez cache). Cizí grant → 404.
- Sekce „Připojené aplikace“ v `Integrations.razor`: host klienta, název, scope, vytvořeno, naposledy použito, Odebrat.
- Změna hesla / reset hesla / vypnutí 2FA: **revokovat všechny OAuth granty uživatele** (Q6 — navrženo ano).

### 4.9 Consent obrazovka — bezpečnostní požadavky

- Nadpis „**{host client_id}** chce přístup k vašemu Fakviu“; `client_name` jen jako podtitul (self-asserted).
- Zobrazit host `redirect_uri`; u loopback-only varování „aplikace běží na vašem počítači“.
- Zobrazit e-mail uživatele a firmu („Přihlášen jako … — firma …“) + odkaz „Nejste to vy? Odhlásit“.
- Volba scope (radio): **Jen čtení** (výchozí) / **Čtení i zápis** (vysvětlit: vystavit, odeslat, označit zaplaceno, smazat).
- Tlačítka Povolit / Zamítnout; žádný automatický souhlas (ani pro opakovaný connect) v v1.
- `app.fakvio.cz` dostane v `staticwebapp.config.json` `globalHeaders`: `Content-Security-Policy: frame-ancestors 'none'`
  a `X-Frame-Options: DENY` (celá aplikace — nic ji legitimně nevkládá do iframe).
- Texty v resources CZ/EN.

### 4.10 Rate limiting

ASP.NET Core `AddRateLimiter` (součást frameworku, žádný balíček), policy jen na OAuth endpointy:

| Endpoint | Partition | Limit (návrh) | Proč |
|---|---|---|---|
| `/oauth/token`, `/oauth/revoke` | IP | 300 / min | Claude i ChatGPT chodí ze sdílených egress rozsahů — per-IP limit musí být vysoký; brute force 256bit tokenů nehrozí, chrání se DB |
| `/oauth/authorize` | IP | 60 / min | spouští CIMD fetch |
| `/api/oauth/consent*` | UserId | 30 / min | |
| CIMD fetch | `client_id` | 1 souběžně + negativní cache | SSRF / amplifikace |

Odmítnutí = 429 s `Retry-After`. Limity jsou konfigurovatelné (`McpOAuth:RateLimits`), ladí se podle provozu.

### 4.11 Audit log

Žádná nová audit tabulka (YAGNI) — trvalý záznam jsou řádky `OAuthGrant` (kdo, komu, jaký scope, kdy,
kdy naposledy, kdy a proč revokováno). Bezpečnostní události jako strukturovaný log (retence Warning+ je
trvalá, `LogCleanupService` maže jen Debug/Info/Trace):

| Událost | Level |
|---|---|
| `OAuth.ConsentGranted` / `ConsentDenied` (UserId, ClientId, scope) | Warning (aby přežila 48h úklid) |
| `OAuth.GrantRevoked` (reason) | Warning |
| `OAuth.RefreshReuseDetected`, `OAuth.CodeReuseDetected` | Warning |
| `OAuth.ClientRejected` (SSRF blok, neplatný dokument, mimo allowlist, redirect mismatch) | Warning |
| `OAuth.TokenIssued` / `Refreshed` | Information (objem) |

Nikdy nelogovat raw tokeny, kódy, `code_verifier` ani celé query stringy `/oauth/*` (obsahují `code`) —
ověřit, že request logging tyto cesty nevypisuje s query.

---

## 5. Feature flag, rollout bez test prostředí, rollback

### 5.1 Konfigurace

| Klíč | Kde | Default | Význam |
|---|---|---|---|
| `McpOAuth:Enabled` | API | `false` | `false` = OAuth endpointy i AS metadata 404, `ApiKeyAuthenticator` odmítá `fak_oat_` tokeny |
| `McpOAuth:AllowAll` | API | `false` | `true` = GA, allowlist se ignoruje |
| `McpOAuth:AllowedUserIds` | API | `[]` | uživatelé smí dát souhlas / refreshovat |
| `McpOAuth:AllowedCompanyIds` | API | `[]` | totéž pro všechny uživatele firmy |
| `McpOAuth:TrustedClientHosts` | API | `["claude.ai","chatgpt.com"]` | povolené hosty `client_id` |
| `McpOAuth:Issuer` | API | — | `https://api.fakvio.cz` |
| `McpOAuth:Resource` | API | — | `https://mcp.fakvio.cz/mcp` |
| `McpOAuth:ResourceProofSecret` | API + MCP | — | §4.4; Key Vault reference |
| `FAKVIO_MCP_OAUTH_ENABLED` | MCP host | `false` | `false` = PRM 404 a challenge bez `resource_metadata` (dnešní odpověď) |
| `FAKVIO_MCP_PUBLIC_URL`, `FAKVIO_OAUTH_ISSUER` | MCP host | — | pro PRM |

Allowlist se kontroluje **na consentu, na token endpointu (i refresh) a v autentizaci OAuth tokenu**
(levný lookup v konfiguraci). Odebrání uživatele z allowlistu tedy vypne jeho OAuth okamžitě (po restartu
app settings), bez zásahu do dat.

### 5.2 Rollout (produkce, bez test env)

1. Merge + deploy s flagem **off** — chování identické s dneškem (testy „flag off = dnešní odpověď“ v N5.3 a N5.6).
2. Vlastní doména `api.fakvio.cz` + certifikát (pokud Q1 = ano); ověřit, že staré URL API fungují dál.
3. Zapnout `McpOAuth:Enabled` + `AllowedUserIds=[owner]` na API a `FAKVIO_MCP_OAUTH_ENABLED=true` na MCP.
   Ostatní uživatelé mohou discovery projít, ale consent jim ukáže „OAuth je zatím v uzavřeném testu“ a vrátí `access_denied`.
4. Ruční E2E (N5.8): claude.ai web + mobil, Claude Code (loopback), ChatGPT; read-only souhlas → zápis odmítnut;
   refresh po 1 h; revokace na Integracích → okamžitý 401; reuse starého refresh tokenu (curl) → rodina zneplatněna;
   API klíč v HTTP i stdio beze změny. Zaznamenat skutečné `client_id` URL a `redirect_uri` do ADMINGUIDE.
5. Rozšířit allowlist o několik firem z early access, po 1–2 týdnech bez incidentu `AllowAll=true`.
6. Pak případně přihláška do adresáře konektorů Claude / ChatGPT (mimo tento ADR).

### 5.3 Rollback

- **Rychlý:** `McpOAuth:Enabled=false` (+ `FAKVIO_MCP_OAUTH_ENABLED=false`) → restart app → všechny OAuth
  tokeny okamžitě 401, discovery 404, connector v claude.ai hlásí chybu. Data zůstanou; po opětovném zapnutí
  platné refresh tokeny fungují dál (uživatel nemusí nic dělat, pokud nevypršely).
- **Tvrdý (kompromitace):** SQL `UPDATE "OAuthGrants" SET "RevokedAt"=now()` + totéž pro OAuth řádky `ApiKey`
  (runbook v ADMINGUIDE §9), pak flag off.
- **Kódový:** migrace je čistě aditivní (nové tabulky + nullable sloupec) → revert commitu a `Down` migrace bezpečné.
- **API klíče nejsou rollbackem dotčené** v žádné variantě.

### 5.4 API klíče

Beze změny pro stdio i HTTP: formát `fak_live_`, UI, scopes, `/me`, gate. Jediná viditelná změna: 401 z MCP
hostu (při zapnutém flagu) nese navíc `resource_metadata` a `scope` — klient s neplatným klíčem v hlavičce
může nabídnout OAuth přihlášení. Stránka Integrace dál nabízí oba snippety; USERGUIDE doplní třetí cestu
„claude.ai / ChatGPT: jen URL“.

---

## 6. Threat model

| # | Hrozba | Mitigace | Test (task) |
|---|---|---|---|
| T1 | **Phishing consent** — útočník pošle odkaz na authorize s vlastním klientem, oběť klikne Povolit | Allowlist hostů klientů (early access); consent jmenuje host `client_id` a host `redirect_uri`, ne `client_name`; štítek „ověřená aplikace“; výchozí scope jen čtení; Warning log `ConsentGranted`; revokace na Integracích | bUnit: zobrazení hostů a varování; unit: klient mimo allowlist → chyba (N5.4, N5.5) |
| T2 | **Open redirect** přes `redirect_uri` | Přesná shoda s CIMD dokumentem (loopback jen bez portu); neplatný `client_id`/`redirect_uri` → chybová stránka, nikdy redirect; žádný obecný `returnUrl` v UI | unit: mismatch, jiné schéma, fragment, loopback s cestou navíc → žádný 302 (N5.4) |
| T3 | **Krádež / injekce authorization code** | PKCE S256 povinné (plain odmítnuto), kód 60 s, jednorázový (atomický update), reuse → revokace grantu, vazba na `client_id` + `redirect_uri` + `resource`; `iss` v odpovědi (mix-up) | unit: špatný verifier, prošlý, použitý, jiný redirect, reuse revokuje (N5.3) |
| T4 | **Krádež access tokenu** | TTL 1 h; hash v DB; nikdy v logu ani v URL; funguje jen přes MCP host (resource proof, §4.4); okamžitá revokace | unit: `fak_oat_` bez proof hlavičky → 401 na API; integration: revokace → 401 (N5.3, N5.7) |
| T5 | **Krádež refresh tokenu** | Rotace při každém použití; reuse → revokace celé rodiny (`invalid_grant`); klouzavých 30 dní + absolutní strop; revokace při změně hesla | unit: rotace, reuse starého tokenu zneplatní i nový (N5.3) |
| T6 | **Confused deputy / token passthrough** | Tokeny s audience MCP API přijme jen s interní proof hlavičkou; MCP gate kontroluje `Resource` z `/me`; API klíče jsou vědomá výjimka (§4.4) | unit: token s jiným `Resource` → MCP 401 (N5.6) |
| T7 | **SSRF přes CIMD fetch** | Jen https:443 s cestou; DNS resolve + blokace privátních/loopback/link-local/metadata rozsahů v `ConnectCallback` a připojení na ověřenou IP; bez redirectů; 5 s; 64 kB; allowlist hostů; souběh 1 + negativní cache | unit: localhost, 127.x, 10.x, 172.16.x, 192.168.x, 169.254.169.254, `[::1]`, `[::ffff:10.0.0.1]`, DNS jméno → privátní IP, 302, 65 kB, nevalidní JSON, `client_id` mismatch (N5.5) |
| T8 | **CSRF na consent decision** | Decision nese JWT v hlavičce (ne cookie) → prohlížeč ho cizímu webu nepřiloží; ticket chráněný Data Protection s expirací | unit: decision bez JWT → 401; zfalšovaný/prošlý ticket → 400 (N5.4) |
| T9 | **Clickjacking consentu** | `frame-ancestors 'none'` + `X-Frame-Options: DENY` pro `app.fakvio.cz` | kontrola hlaviček po deployi (N5.8) |
| T10 | **Loopback impersonace** (lokální proces předstírá Claude Code) | Varování na consentu u loopback-only; PKCE; allowlist hostů CIMD | bUnit varování (N5.4) |
| T11 | **Eskalace tenantu / impersonace** | Tenant z `User.CompanyId`; SysAdmin/bez firmy grant nedostane; `X-Company-Id` ignorován pro OAuth principal | unit: SysAdmin consent odmítnut; OAuth + `X-Company-Id` → vlastní tenant (N5.3) |
| T12 | **Eskalace scope** | Scope z grantu, ne z requestu; refresh nesmí scope rozšířit (jen zúžit); `ApiKeyRequestGuard` beze změny; OAuth tokenem nejde spravovat klíče ani granty | unit: refresh s `scope=write` u read grantu → `invalid_scope` (N5.3) |
| T13 | **DoS / brute force** | Rate limiter; 256bit tokeny; indexované hash lookupy | unit/integration: 429 nad limit (N5.3) |
| T14 | **Únik tokenů v logu** | Log jen prefix; `/oauth/*` bez query v request logu; `Cache-Control: no-store` | review + test logování (N5.8) |
| T15 | **Chyba na produkci bez test env** | Flag default off, allowlist, testy „flag off = dnešní chování“, rychlý rollback | (N5.3, N5.6, N5.8) |

---

## 7. Důsledky

**Pozitivní:** claude.ai/ChatGPT connector „jen URL + přihlášení“; žádná nová závislost; okamžitá revokace
zůstává; API klíče nedotčené; bezpečnostní model (role ∩ scope, tenant z uživatele) se nemění.

**Negativní / náklady:** vlastní protokolový kód k údržbě a security review; jedna vlastní doména navíc
(`api.fakvio.cz`); nový BackgroundService; sdílené tajemství mezi dvěma web appy; `Login.razor`/`AuthCallback.razor`
dostanou návrat na consent; OAuth access tokeny zvyšují počet řádků v `ApiKey`.

**Odložené (vědomě):** DCR, step-up 403, consent picker firmy, `private_key_jwt`, per-area scopes, adresář konektorů.

**Mimo tento ADR, ale související:** MCP spec 2026-07-28 odstranila sessions a `initialize` ([changelog][spec-chg]);
SDK 2.x ji podporuje (host je už stateless). Kompatibilitu s aktuálním claude.ai ověřit v rámci E2E.

---

## 8. Implementační plán (upřesněné N5.2–N5.8)

| Task | Obsah (změny proti plánu W39 **tučně**) | Závisí |
|---|---|---|
| **N5.2** Storage | Entity `OAuthGrant`, `OAuthAuthorizationCode`, `OAuthRefreshToken`; `ApiKey.OAuthGrantId` (**bez `Source`, bez `OAuthClient`**); master migrace, unique indexy na hashe, FK cascade; `OAuthCleanupService`. Testy proti reálnému PG (vzor `ApiKeyDatabaseConstraintTests`). DEVGUIDE §2.11. | N5.1 Accepted |
| **N5.3** Token + metadata | `OAuthController` (`/.well-known/oauth-authorization-server`, `/oauth/token`, `/oauth/revoke`) + `IOAuthService`; rotace + reuse; **`iss`**; **proof hlavička a allowlist v `ApiKeyAuthenticator`/guardu; `oauth_grant_id` claim; ignorace `X-Company-Id`; `/me` vrací `Resource`**; rate limiter; flag. Unit testy T3–T5, T11–T13, flag off → 404. | N5.2 |
| **N5.4** Authorize + consent | `GET /oauth/authorize` (**Data Protection ticket**), `GET /api/oauth/consent/{ticket}`, `POST /api/oauth/consent/decision`; `OAuthConsent.razor`; **návrat na consent po loginu/SSO/2FA**; **`globalHeaders` ve `staticwebapp.config.json`**; resources CZ/EN. Testy T1, T2, T8, T10. | N5.3, N5.5 |
| **N5.5** CIMD resolver | `IOAuthClientResolver`: **allowlist hostů**, SSRF-safe `HttpClient` (`ConnectCallback`), cache. **DCR ne** (samostatný podmíněný task N5.5b jen při Q2 = ano). Testy T7. | N5.2 |
| **N5.6** MCP host | PRM na obou well-known cestách, challenge s `resource_metadata` + `scope`; **zvážit `McpAuthenticationHandler` z SDK 2.2.0 pro PRM/challenge**, jinak ručně (~30 řádků); **`AuthHeaderHandler` přidá proof hlavičku; gate kontroluje `Resource` z `/me`**; flag. Testy v `McpHttpTransportTests`: PRM bez auth 200, jiná cesta 401, flag off = dnešní odpověď. | N5.3 |
| **N5.7** Integrace | `GET /api/oauth/grants`, `POST …/{id}/revoke` (JWT only); sekce „Připojené aplikace“; **revokace grantů při změně/resetu hesla**. USERGUIDE §20.7. | N5.3 |
| **N5.8** Review + rollout | `/security-review`, projít threat model T1–T15 s odkazy na testy; **doména `api.fakvio.cz`**; app settings + Key Vault; runbook zapnutí/rollbacku (ADMINGUIDE §9, opravit zastaralý text o test env); E2E dle §5.2; doplnit zjištěné `client_id` URL; DEVGUIDE §4.9 odstranit „mimo scope“. | N5.4–N5.7 |

---

## 9. Otevřené otázky pro ownera

- **Q1 — Issuer doména:** zřídit `api.fakvio.cz` (CNAME na `fakvio-api` + managed cert) jako issuer? Doporučeno ano. Alternativa: `fakvio-api.azurewebsites.net` (funguje, ale horší důvěra a pozdější změna = reconnect všech).
- **Q2 — DCR:** souhlas, že v1 bude jen CIMD a DCR se přidá až podle E2E? (Claude i ChatGPT CIMD podporují.)
- **Q3 — Otevřenost klientům:** během early access jen `claude.ai` a `chatgpt.com` (včetně Claude Code a Codex)? Kdy a zda pustit libovolné CIMD klienty (Cursor, VS Code, …) s varováním?
- **Q4 — Životnosti:** access 1 h, refresh 30 dní klouzavě, absolutní strop grantu 180 dní (pak nový souhlas) — OK?
- **Q5 — Souběžný refresh:** Claude refreshuje proaktivně i reaktivně; souběh dvou refreshů by spustil detekci reuse a odpojil uživatele. V1 bez tolerance (bezpečnější); pokud E2E ukáže falešná odpojení, přidat krátké okno (≤ 10 s), kdy druhé použití vrátí `invalid_grant` **bez** revokace rodiny. Souhlas?
- **Q6 — Změna hesla:** revokovat při změně/resetu hesla všechny OAuth granty (navrženo ano) — a také API klíče? (Dnes se neruší; mimo scope N5, jen upozornění.)
- **Q7 — Proof hlavička (§4.4):** souhlas s omezením OAuth tokenů jen na cestu přes MCP host (sdílené tajemství mezi web appy)? Alternativa „token platí na celém API jako API klíč“ je jednodušší o jeden app setting, ale porušuje audience binding.
- **Q8 — Allowlist při rolloutu:** kdo kromě ownera (ID uživatelů/firem) a jaké kritérium pro `AllowAll=true` (návrh: 2 týdny bez incidentu a ≥ 5 aktivních grantů)?
- **Q9 — SysAdmin:** potvrdit, že SysAdmin OAuth grant nedostane a impersonace přes OAuth token je zakázaná.

---

## 10. Zdroje (ověřeno 2026-09-26)

- MCP 2026-07-28 Authorization — https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization
- Authorization Server Discovery — https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/authorization-server-discovery
- Client Registration (CIMD, DCR deprecated) — https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration
- Authorization Security Considerations — https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/security-considerations
- Changelog 2026-07-28 — https://modelcontextprotocol.io/specification/2026-07-28/changelog
- CIMD draft (SSRF §6) — https://datatracker.ietf.org/doc/html/draft-ietf-oauth-client-id-metadata-document-00
- RFC 9728 — https://datatracker.ietf.org/doc/html/rfc9728 · RFC 8414 — https://datatracker.ietf.org/doc/html/rfc8414 · RFC 8707 — https://www.rfc-editor.org/rfc/rfc8707.html · RFC 7009 — https://datatracker.ietf.org/doc/html/rfc7009 · RFC 9207 — https://datatracker.ietf.org/doc/html/rfc9207 · RFC 8252 §7.3 — https://datatracker.ietf.org/doc/html/rfc8252#section-7.3 · OAuth 2.1 draft 13 — https://datatracker.ietf.org/doc/html/draft-ietf-oauth-v2-1-13
- Claude — Authentication for connectors — https://claude.com/docs/connectors/building/authentication
- Claude — Lazy authentication (CIMD, loopback, step-up) — https://claude.com/docs/connectors/building/lazy-authentication
- Claude Code CIMD — https://claude.ai/oauth/claude-code-client-metadata
- OpenAI Apps SDK — Authentication — https://developers.openai.com/apps-sdk/build/auth
- ChatGPT Learn — MCP (Codex CIMD/loopback) — https://learn.chatgpt.com/docs/extend/mcp
- C# SDK releases (2.2.0 = poslední) — https://github.com/modelcontextprotocol/csharp-sdk/releases
- OpenIddict releases — https://github.com/openiddict/openiddict-core/releases
- ABP/OpenIddict bez DCR pro MCP — https://github.com/abpframework/abp/issues/24193
- Clerk CIMD (2026-08-05) — https://clerk.com/changelog/2026-08-05-client-id-metadata-documents

[spec-auth]: https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization
[spec-disc]: https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/authorization-server-discovery
[spec-reg]: https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration
[spec-sec]: https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/security-considerations
[spec-chg]: https://modelcontextprotocol.io/specification/2026-07-28/changelog
[claude-auth]: https://claude.com/docs/connectors/building/authentication
[claude-lazy]: https://claude.com/docs/connectors/building/lazy-authentication
[openai-auth]: https://developers.openai.com/apps-sdk/build/auth
[openai-learn]: https://learn.chatgpt.com/docs/extend/mcp
[sdk-rel]: https://github.com/modelcontextprotocol/csharp-sdk/releases
[openiddict]: https://github.com/openiddict/openiddict-core/releases
[clerk]: https://clerk.com/changelog/2026-08-05-client-id-metadata-documents
