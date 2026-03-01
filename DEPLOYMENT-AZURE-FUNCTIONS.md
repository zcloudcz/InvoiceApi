# Azure Functions — Konfigurace a nasazení

> **Runtime:** Azure Functions v4 Isolated Worker (.NET 10)
> **Funkce:** 3 (1 HTTP catch-all + 2 timer triggers)

---

## 1. Kde nastavit SQL Connection String

### Azure Portal (produkce)

**Function App → Settings → Environment variables → Connection strings**

Přidat 2 connection stringy typu `SQLAzure`:

| Název | Hodnota | Popis |
|-------|---------|-------|
| `MasterConnection` | `Server=tcp:<server>.database.windows.net,1433;Database=fakvio_master;User ID=<user>;Password=<pass>;Encrypt=True;TrustServerCertificate=False;` | Hlavní DB (uživatelé, firmy, systémová konfigurace) |
| `TenantTemplateConnection` | `Server=tcp:<server>.database.windows.net,1433;Database=fakvio_tenant_template;User ID=<user>;Password=<pass>;Encrypt=True;TrustServerCertificate=False;` | Šablonová tenant DB (migrace, fallback) |

> **POZOR:** Connection stringy se v Azure Functions nastavují v sekci **Connection strings**, NE v Application settings. Kód je čte přes `configuration.GetConnectionString("MasterConnection")`.

### Azure CLI alternativa

```bash
az functionapp config connection-string set \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP> \
    --connection-string-type SQLAzure \
    --settings \
        MasterConnection="Server=tcp:<server>.database.windows.net,1433;Database=fakvio_master;User ID=<user>;Password=<pass>;Encrypt=True;TrustServerCertificate=False;" \
        TenantTemplateConnection="Server=tcp:<server>.database.windows.net,1433;Database=fakvio_tenant_template;User ID=<user>;Password=<pass>;Encrypt=True;TrustServerCertificate=False;"
```

### Lokální vývoj (`local.settings.json`)

Soubor `Fakvio.Functions/local.settings.json` (NENÍ deployován do Azure):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"
  },
  "ConnectionStrings": {
    "MasterConnection": "Server=localhost;Database=fakvio_master;Trusted_Connection=true;TrustServerCertificate=true",
    "TenantTemplateConnection": "Server=localhost;Database=fakvio_tenant_template;Trusted_Connection=true;TrustServerCertificate=true"
  },
  "JwtSettings": {
    "Secret": "your-dev-secret-here-min-32-chars-long!!!",
    "Issuer": "Fakvio",
    "Audience": "FakvioClient"
  }
}
```

### Jak connection stringy fungují v kódu

```
HTTP Request s JWT tokenem (obsahuje CompanyId claim)
    │
    ▼
MasterDbContext (používá MasterConnection vždy)
    │
    ▼ Hledá CompanySystemSettings pro CompanyId
    │
    ├── Nalezeno + IsProvisioned + IsActive:
    │     → Vezme MasterConnection, přepíše Database na tenant DB jméno
    │     → (nebo použije custom connection string pokud je nastavený)
    │
    └── Nenalezeno / neprovisioned / neaktivní:
          → Fallback na TenantTemplateConnection
```

---

## 2. Všechna nastavení

### Povinná (Application settings)

Nastavují se v **Function App → Settings → Environment variables → App settings**:

| Klíč | Příklad | Popis |
|------|---------|-------|
| `JwtSettings__Secret` | `MojeSuprTajneHesloMinimalne32ZnakuDlouhe!` | JWT podpisový klíč (min 32 znaků) |
| `JwtSettings__Issuer` | `Fakvio` | JWT issuer (default: `Fakvio`) |
| `JwtSettings__Audience` | `FakvioClient` | JWT audience (default: `FakvioClient`) |

> **Formát:** V Azure App Settings se `:` nahrazuje `__` (dvojité podtržítko).
> Tzn. `JwtSettings:Secret` → `JwtSettings__Secret`

### Volitelná (Application settings)

| Klíč | Popis |
|------|-------|
| `SmtpSettings__Host` | SMTP server (např. `smtp.gmail.com`) |
| `SmtpSettings__Port` | SMTP port (default: `587`) |
| `SmtpSettings__Username` | SMTP přihlášení |
| `SmtpSettings__Password` | SMTP heslo |
| `SmtpSettings__SenderEmail` | Odesílatel (From) |
| `SmtpSettings__SenderName` | Jméno odesílatele |
| `SmtpSettings__UseSsl` | `true` / `false` |
| `OAuth__Google__ClientId` | Google OAuth (prázdné = vypnuto) |
| `OAuth__Google__ClientSecret` | Google OAuth secret |
| `OAuth__Microsoft__ClientId` | Microsoft OAuth (prázdné = vypnuto) |
| `OAuth__Microsoft__ClientSecret` | Microsoft OAuth secret |
| `OAuth__Facebook__AppId` | Facebook OAuth (prázdné = vypnuto) |
| `OAuth__Facebook__AppSecret` | Facebook OAuth secret |
| `OAuth__Seznam__ClientId` | Seznam.cz OAuth (prázdné = vypnuto) |
| `OAuth__Seznam__ClientSecret` | Seznam.cz OAuth secret |

### Automaticky nastavené (nenastavovat ručně)

| Klíč | Popis |
|------|-------|
| `AzureWebJobsStorage` | Storage account (nastaví Azure při vytvoření Function App) |
| `FUNCTIONS_WORKER_RUNTIME` | `dotnet-isolated` (nastaví Azure) |
| `APPINSIGHTS_INSTRUMENTATIONKEY` | Application Insights (nastaví Azure pokud je propojený) |

---

## 3. Architektura

```
                    ┌──────────────────────────────────────┐
                    │       Azure Functions Host            │
                    │                                      │
  HTTP Request ────►│  CatchAll ({*route})                 │
                    │    └── ASP.NET Core Pipeline          │
                    │         ├── JWT Authentication        │
                    │         ├── ImpersonationMiddleware   │
                    │         ├── TenantContextMiddleware   │
                    │         └── API Controllers           │
                    │                                      │
  Timer (5s) ──────►│  LogFlush → AppLog tabulka           │
  Timer (1h) ──────►│  LogCleanup → smaže staré logy      │
                    └──────────────────┬───────────────────┘
                                       │
                            ┌──────────▼──────────┐
                            │     Azure SQL        │
                            │  ┌────────────────┐  │
                            │  │  Master DB     │  │
                            │  │  (uživatelé,   │  │
                            │  │   firmy)       │  │
                            │  └────────────────┘  │
                            │  ┌────────────────┐  │
                            │  │  Tenant DB(s)  │  │
                            │  │  (faktury,     │  │
                            │  │   klienti)     │  │
                            │  └────────────────┘  │
                            └──────────────────────┘
```

---

## 4. Databáze — migrace

Azure Functions **nemigruje** automaticky. Migrace spustit ručně před prvním nasazením:

```bash
# Master DB
dotnet ef database update \
    --project Fakvio.Infrastructure \
    --startup-project Fakvio.API \
    --context MasterDbContext \
    --connection "Server=tcp:<server>.database.windows.net,1433;Database=fakvio_master;User ID=<user>;Password=<pass>;Encrypt=True;TrustServerCertificate=False;"

# Tenant Template DB
dotnet ef database update \
    --project Fakvio.Infrastructure \
    --startup-project Fakvio.API \
    --context TenantDbContext \
    --connection "Server=tcp:<server>.database.windows.net,1433;Database=fakvio_tenant_template;User ID=<user>;Password=<pass>;Encrypt=True;TrustServerCertificate=False;"
```

Nové tenant databáze se vytvářejí přes API: `POST /api/company/{id}/provision`

---

## 5. CORS

V Azure Functions se CORS nastavuje v **Azure Portal**, NE v kódu.

**Function App → API → CORS**

Přidat:
- Produkce: `https://app.yourdomain.com`
- Vývoj: `http://localhost:5145`, `https://localhost:7212`
- Zaškrtnout: **Enable Access-Control-Allow-Credentials**

```bash
# Nebo přes CLI:
az functionapp cors add \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP> \
    --allowed-origins "https://app.yourdomain.com"
```

---

## 6. Blazor WASM propojení

V Blazor WASM projektu nastavit API URL v `wwwroot/appsettings.json`:

```json
{
  "ApiSettings": {
    "BaseUrl": "https://<FUNC_APP_NAME>.azurewebsites.net"
  }
}
```

URL struktura je **stejná** jako u standalone API (`/api/invoice`, `/api/client`, atd.) — `host.json` má `routePrefix: ""`.

---

## 7. Nasazení

### Varianta A: Azure Functions Core Tools

```bash
cd Fakvio.Functions
func azure functionapp publish <FUNC_APP_NAME> --dotnet-isolated
```

### Varianta B: dotnet publish + zip deploy

```bash
dotnet publish Fakvio.Functions -c Release -o ./publish
cd publish
# PowerShell:
Compress-Archive -Path * -DestinationPath ../functions.zip -Force
az functionapp deployment source config-zip \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP> \
    --src ../functions.zip
```

### Varianta C: Visual Studio

Pravý klik na `Fakvio.Functions` → **Publish** → **Azure Function App (Windows)**

---

## 8. Ověření

```bash
# Zkontrolovat nasazené funkce
az functionapp function list \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP> \
    --output table

# Měly by se zobrazit: CatchAll, LogFlush, LogCleanup

# Test — měl by vrátit 401 (JWT chybí = funguje)
curl -s -o /dev/null -w "%{http_code}" \
    https://<FUNC_APP_NAME>.azurewebsites.net/api/dashboard
```

---

## 9. Troubleshooting

| Problém | Příčina | Řešení |
|---------|---------|--------|
| **404 na všech URL** | `routePrefix` není prázdný | Zkontrolovat `host.json`: `"routePrefix": ""` |
| **500 při startu** | Chybí connection string nebo JWT secret | Zkontrolovat Environment variables v portálu |
| **SQL connection timeout** | Firewall blokuje | Přidat `AllowAzureServices` pravidlo (0.0.0.0) |
| **CORS error z Blazoru** | CORS nenastavený | Přidat origin v Portal → API → CORS |
| **Timer triggers neběží** | Chybí storage account | Zkontrolovat `AzureWebJobsStorage` |
| **JWT validace selhává** | Jiný secret než v API | Sjednotit `JwtSettings__Secret` |

### Kontrola konfigurace

```bash
# Zobrazit app settings
az functionapp config appsettings list \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP> \
    --output table

# Zobrazit connection strings
az functionapp config connection-string list \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP> \
    --output table

# Live logy
az functionapp log tail \
    --name <FUNC_APP_NAME> \
    --resource-group <RESOURCE_GROUP>
```

---

## 10. Bezpečnost (produkce)

- [ ] JWT Secret: silný, 64+ znaků, NE dev secret
- [ ] HTTPS Only: Portal → TLS/SSL settings → HTTPS Only = On
- [ ] TLS 1.2 minimum
- [ ] SQL Firewall: pouze Azure Services + deployment IP
- [ ] CORS: pouze konkrétní domény, ne `*`
- [ ] Key Vault pro secrets (volitelné, doporučené):

```bash
# Uložit secret do Key Vault
az keyvault secret set --vault-name <KV_NAME> --name JwtSecret --value "<SECRET>"

# Odkázat z App Settings (formát):
JwtSettings__Secret=@Microsoft.KeyVault(SecretUri=https://<KV_NAME>.vault.azure.net/secrets/JwtSecret/)
```
