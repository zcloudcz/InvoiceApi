# Azure Blob Storage — Konfigurační průvodce

Tento dokument popisuje, kde a jak nakonfigurovat Azure Blob Storage pro file attachments v aplikaci Fakvio.

---

## 1. Vytvoření Azure Storage Accountu

### V Azure Portalu
1. Přihlas se na [portal.azure.com](https://portal.azure.com)
2. **Create a resource** → **Storage account**
3. Vyplň:
   - **Subscription**: tvá Azure subscription
   - **Resource group**: nová nebo existující (např. `fakvio-rg`)
   - **Storage account name**: globálně unikátní (např. `fakvioblobstorage`) — pouze malá písmena a čísla
   - **Region**: nejbližší (např. `West Europe`)
   - **Performance**: **Standard** (postačuje pro file attachments)
   - **Redundancy**: **LRS** (Locally Redundant Storage) — nejlevnější, nebo **GRS** pro geo-redundanci
4. **Review + create** → **Create**

### Získání connection stringu
1. Po vytvoření jdi na **Storage account** → **Security + networking** → **Access keys**
2. Klikni **Show** u **key1** → **Connection string**
3. Zkopíruj celý connection string ve formátu:
   ```
   DefaultEndpointsProtocol=https;AccountName=fakvioblobstorage;AccountKey=xxxxx==;EndpointSuffix=core.windows.net
   ```

> **POZOR**: Connection string obsahuje plný přístupový klíč — nikdy ho necommituj do gitu, neukládej do nezašifrovaných souborů a nesdílej.

---

## 2. Kde se konfiguruje (3-tier fallback)

Aplikace hledá Azure Blob connection string ve 3 úrovních. První nalezený se použije:

```
1. CompanySystemSettings.AzureBlobConnectionString   ← per-tenant (nejvyšší priorita)
        ↓ (pokud null/empty)
2. SystemConfiguration.AzureBlobConnectionString     ← system-wide v master DB
        ↓ (pokud null/empty)
3. appsettings.json: "AzureBlobStorage:ConnectionString"  ← fallback
```

**Doporučení**:
- **Produkce**: použij úroveň 2 (SysAdmin UI) — connection string je v DB šifrovaný přes `ICredentialProtector`
- **Vývoj**: použij úroveň 3 (`appsettings.Development.json`)
- **Per-tenant override**: použij úroveň 1 jen pokud konkrétní zákazník chce vlastní storage account (např. data residency requirements)

---

## 3. Úroveň 3: appsettings.json (vývoj)

### Fakvio.API/appsettings.Development.json

```json
{
  "AzureBlobStorage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=fakvioblobstorage;AccountKey=xxxxx==;EndpointSuffix=core.windows.net"
  }
}
```

### Pro Azure Functions (Fakvio.Functions/local.settings.json)

```json
{
  "Values": {
    "AzureBlobStorage__ConnectionString": "DefaultEndpointsProtocol=https;AccountName=fakvioblobstorage;AccountKey=xxxxx==;EndpointSuffix=core.windows.net"
  }
}
```

### Pro produkční Azure App Service

V Azure Portalu → **App Service** → **Configuration** → **Application settings** → **+ New application setting**:

| Name | Value |
|---|---|
| `AzureBlobStorage__ConnectionString` | `DefaultEndpointsProtocol=https;...` |

> Pozn.: Dvojité podtržítko `__` v Azure App Service settings je ekvivalent dvojtečky `:` v `appsettings.json`.

---

## 4. Úroveň 2: SystemConfiguration (SysAdmin UI) — DOPORUČENO

Toto je nejlepší produkční přístup — connection string se ukládá do master DB šifrovaně pomocí ASP.NET Data Protection API.

### Postup:
1. Přihlas se jako **SysAdmin**
2. Jdi na **System Settings** (v menu pod ozubeným kolečkem)
3. Najdi sekci **Azure Blob Storage** (čtvrtá karta)
4. Klikni **Edit**
5. Vyplň:
   - **Connection String**: zkopírovaný connection string z Azure Portalu
   - **Container Prefix**: `tenant` (default — nech prázdné, pokud nepotřebuješ jiné jméno)
6. Klikni **Save**

### Co se stane:
- Connection string se zašifruje pomocí `ICredentialProtector` (Data Protection API)
- Uloží se do `SystemConfiguration.AzureBlobConnectionString` v master DB
- API ho dešifruje při každém požadavku — nikdy se nevrací zpět do UI
- V UI se zobrazuje jen `********` jako indikátor, že je nakonfigurovaný

---

## 5. Úroveň 1: CompanySystemSettings (per-tenant override)

Pro případ, kdy konkrétní zákazník chce vlastní Azure storage account.

### Postup (zatím přes API/DB):
- Endpoint: `PUT /api/company/{id}/settings`
- Field: `AzureBlobConnectionString` (string)
- Field: `AzureBlobContainerPrefix` (string, optional)

> Pozn.: Per-company UI section pro Azure Blob ještě není implementována — momentálně se nastavuje přímo přes API nebo DB. Pokud to potřebuješ, dej vědět a doplním.

---

## 6. Container naming

Automaticky vytvořené containery mají formát:

```
{prefix}-{companyId}
```

### Příklady:
- Default prefix `tenant`, CompanyId 42 → container `tenant-42`
- Default prefix `tenant`, CompanyId 100 → container `tenant-100`
- Custom prefix `fakvio-prod`, CompanyId 42 → container `fakvio-prod-42`

### Pravidla pro container prefix:
- **Pouze malá písmena, čísla, pomlčky** (Azure restrikce)
- **3–63 znaků** celkem (včetně prefixu, pomlčky a CompanyId)
- **Začíná písmenem nebo číslem**

### Lazy creation:
Containery se vytvoří automaticky při prvním uploadu — není potřeba nic dělat ručně. `CreateIfNotExistsAsync` je idempotentní operace.

---

## 7. Blob struktura

Soubory jsou v containeru organizovány hierarchicky:

```
tenant-42/
├── Invoice/
│   ├── 101/
│   │   ├── a1b2c3d4-e5f6-7890-abcd-ef1234567890.pdf  ← contract.pdf
│   │   └── b2c3d4e5-f6a7-8901-bcde-f12345678901.jpg  ← scan.jpg
│   └── 102/
│       └── c3d4e5f6-a7b8-9012-cdef-123456789012.pdf
├── Client/
│   └── 55/
│       └── d4e5f6a7-b8c9-0123-defa-234567890123.pdf
└── ReceivedInvoice/
    └── 200/
        └── e5f6a7b8-c9d0-1234-efab-345678901234.pdf
```

**Path format**: `{EntityName}/{RecordId}/{FileGuid}{extension}`

- `EntityName` = jméno entity (Invoice, Client, ReceivedInvoice, …)
- `RecordId` = primární klíč záznamu
- `FileGuid` = generovaný Guid (zabraňuje kolizím)
- `extension` = původní přípona souboru

---

## 8. Bezpečnost

### Šifrování at rest
- Connection string v DB je šifrovaný přes `ICredentialProtector` (ASP.NET Data Protection API)
- Klíče Data Protection se v Azure produkci ukládají do Azure Key Vault (doporučeno) nebo do file system

### Tenant isolation
- Každý tenant má **vlastní container** → fyzická izolace dat
- API kontroluje `ITenantResolver.GetCurrentCompanyId()` před každou operací
- JWT claim `CompanyId` určuje, do kterého containeru se přistupuje

### Authorization
- Všechny endpointy `/api/file-attachment/*` vyžadují `[Authorize]`
- Žádné public/anonymous přístupy k souborům
- Žádné SAS tokeny — všechny soubory jsou servírovány přes API jako proxied download

### Whitelist přípon
Povolené přípony (definováno v `FileAttachmentService.AllowedExtensions`):
```
.pdf, .doc, .docx, .xls, .xlsx, .csv, .txt, .rtf, .odt, .ods,
.png, .jpg, .jpeg, .gif, .bmp, .svg, .webp, .tiff,
.zip, .rar, .7z,
.xml, .json, .html
```

Spustitelné soubory (`.exe`, `.bat`, `.sh`, `.ps1`, …) jsou **odmítnuty**.

### Limit velikosti
- **Maximum**: 50 MB na soubor (konstanta `MaxFileSizeBytes` ve `FileAttachmentService` a `FileAttachmentController`)
- Validace na klientovi (Blazor), v API controlleru i v service vrstvě
- Pro změnu uprav obě konstanty `MaxFileSizeBytes` (v service i controlleru)

---

## 9. Šifrování klíčů Data Protection (produkce)

Pro produkci v Azure se doporučuje persistovat Data Protection klíče do **Azure Key Vault**. Bez toho se klíče po restartu app service ztratí a všechny zašifrované hodnoty (včetně blob connection stringu, SMTP hesel, AI API klíčů) přestanou jít dešifrovat.

### Postup:
1. Vytvoř **Azure Key Vault**
2. Přiřaď **Managed Identity** App Service → roli **Key Vault Crypto User**
3. V `Program.cs` přidej:
   ```csharp
   builder.Services.AddDataProtection()
       .PersistKeysToAzureBlobStorage(...)
       .ProtectKeysWithAzureKeyVault(...);
   ```

> Pozn.: Tento setup je obecný pro celou aplikaci, ne specifický pro file attachments — týká se i SMTP/AI/OAuth credentials.

---

## 10. Test konfigurace

### Quick test přes UI:
1. Otevři libovolnou existující fakturu (`/invoices/{id}`)
2. Přepni do **Edit** módu
3. Scrolluj dolů — uvidíš sekci **Přílohy** (📎 ikona)
4. Klikni **Nahrát** a vyber libovolný PDF
5. Pokud upload projde a soubor se objeví v tabulce → konfigurace je OK ✅
6. Klikni ⬇ → soubor se stáhne s původním názvem

### Test přes Azure Storage Explorer:
1. Stáhni [Azure Storage Explorer](https://azure.microsoft.com/en-us/features/storage-explorer/)
2. Připoj se ke svému storage accountu
3. Po prvním uploadu uvidíš automaticky vytvořený container `tenant-{companyId}`
4. Uvnitř bude struktura `Invoice/{id}/{guid}.pdf`

### Logy
Aplikace loguje všechny blob operace na úrovni `Information`:
```
Uploaded blob Invoice/42/a1b2....pdf to container tenant-42 (245678 bytes)
Uploaded file attachment: contract.pdf (245678 bytes) for Invoice #42
Deleted blob Invoice/42/a1b2....pdf from container tenant-42
```

V Azure App Insights filtruj podle `category contains FileStorage` nebo `FileAttachment`.

---

## 11. Náklady

Azure Blob Storage je velmi levný pro file attachment use case:

| Položka | Cena (West Europe, Standard LRS, 2026) |
|---|---|
| Storage | ~ €0.018 / GB / měsíc (Hot tier) |
| Write operations | ~ €0.05 / 10,000 operations |
| Read operations | ~ €0.004 / 10,000 operations |
| Outbound data transfer | prvních 100 GB / měsíc zdarma, pak ~ €0.08 / GB |

### Příklad:
- 100 zákazníků
- Průměrně 50 souborů na zákazníka (PDF faktury, scany)
- Průměrná velikost 500 KB
- → **2.5 GB celkem** = ~ **€0.05 / měsíc** za storage

I při intenzivním používání zůstávají náklady v jednotkách EUR měsíčně.

### Tip na úsporu:
Pro starší soubory (faktury z minulých let, neaktivní zákazníci) můžeš nastavit **Lifecycle Management** v Azure → Hot tier → Cool tier → Archive tier. Šetří až 90 % nákladů na storage.

---

## 12. Troubleshooting

### "Azure Blob Storage connection string is not configured"
→ Connection string není nastaven ani v jedné ze 3 úrovní. Zkontroluj:
1. SysAdmin → System Settings → Azure Blob Storage (úroveň 2)
2. `appsettings.json` → `AzureBlobStorage:ConnectionString` (úroveň 3)

### "The specified container does not exist"
→ Container se vytváří automaticky při prvním uploadu. Pokud vidíš tuto chybu při downloadu, znamená to, že upload selhal. Zkontroluj logy na Azure straně (storage account → Monitoring → Insights).

### "AuthorizationFailure: Server failed to authenticate the request"
→ Connection string je špatně. Stáhni nový z Azure Portalu (Storage account → Access keys → key1 → Connection string).

### "RequestBodyTooLarge"
→ Soubor je větší než limit Kestrelu/IIS. Zkontroluj `MaxFileSizeBytes` (50 MB) v `FileAttachmentService` a `FileAttachmentController`. Pro zvýšení limitu uprav obě konstanty + případně přidej do `Program.cs`:
```csharp
builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = 100_000_000);
```

### "File extension '.exe' is not allowed"
→ Záměrné — spustitelné soubory jsou blacklistovány. Pro povolení další přípony uprav `AllowedExtensions` ve `FileAttachmentService.cs`.

### Container má nevalidní jméno
→ CompanyId nebo prefix obsahuje nepovolené znaky. Container name musí splňovat:
- Pouze malá písmena, čísla, pomlčky
- 3–63 znaků
- Začíná písmenem/číslem
Zkontroluj `Container Prefix` v System Settings.

---

## 13. Migrace na jiný storage provider

`IFileStorage` je čistá abstrakce — pro přechod na jiný provider stačí implementovat nové třídy:

### Možné implementace:
- `LocalFileStorage` — file system (vývoj/testing)
- `S3FileStorage` — Amazon S3 (`AWSSDK.S3` NuGet)
- `MinioFileStorage` — self-hosted S3-compatible (`Minio` NuGet)
- `GoogleCloudStorageFileStorage` — GCS

### Postup:
1. Vytvoř novou třídu implementující `IFileStorage`
2. Zaregistruj ji v `ServiceCollectionExtensions.AddFakvioCore()` namísto `AzureBlobFileStorage`
3. Žádné další změny — `FileAttachmentService`, controller a UI fungují beze změn

---

## 14. Migration checklist před produkčním nasazením

- [ ] Azure Storage Account vytvořen
- [ ] Connection string získán z Access Keys
- [ ] EF migrace `AddBlobStorageSettings` aplikována na master DB (`dotnet ef database update -c MasterDbContext`)
- [ ] EF migrace `AddFileAttachment` aplikována na všechny tenant schemata (přes TenantProvisioningService nebo MigrationTool)
- [ ] Connection string nastaven v SysAdmin → System Settings → Azure Blob Storage
- [ ] Test upload/download/delete na testovací entitě
- [ ] Data Protection klíče persistovány do Key Vault (doporučeno)
- [ ] Backup policy / lifecycle management nastaven (volitelné)
- [ ] Monitoring v App Insights ověřen (logy `FileStorage`, `FileAttachment`)
