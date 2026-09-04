# Tailscale userspace tunel → privátní PostgreSQL

Testovací Function App (`zcloudinvoicingapi-test`) se připojuje k vlastní PostgreSQL
na Hostingeru, jejíž port **není ve veřejném internetu**. Cesta k ní vede přes tailnet.
Tenhle adresář obsahuje všechno, co tunel staví.

---

## 1. Proč zrovna takhle („Cesta B")

Azure Functions sandbox **neumí vytvořit TUN zařízení**, takže normální (kernelový)
režim Tailscale je vyloučený. `tailscaled` proto běží v **userspace-networking** režimu:
místo síťového rozhraní nabídne **SOCKS5 proxy na localhostu**.

Jenže **Npgsql SOCKS5 neumí** a nemá ani rozšiřitelný socket factory, takže se na proxy
nedokáže napojit sám. Mezi ně se proto vkládá **`Socks5Forwarder`** — obyčejný TCP
listener na `127.0.0.1:15432`, který za Npgsql odbaví SOCKS5 handshake a pak už jen
přelévá bajty oběma směry.

Alternativa („Cesta A") je Tailscale **subnet router** na straně Azure — VM navíc,
~$4–8/měsíc. Sáhne se po ní jen tehdy, pokud sandbox tuhle variantu zablokuje
(viz [Známá omezení](#6-známá-omezení-a-rizika)).

## 2. Řetěz při startu

Vše se spouští v `Fakvio.Functions/Program.cs`, **po `Build()`** (potřebujeme reálný
`ILogger` a `IHostApplicationLifetime`) a **před migračním blokem** (to je první místo,
kde aplikace otevře socket do databáze). `IHostedService` by byl pozdě — hostované
služby startují až v `RunAsync()`, tedy až po migraci.

Pořadí kroků je **probe-first**: nejdřív se zkusí, jestli už někdo neodpovídá na
`127.0.0.1:1055`. Když ano, běží démon jiného workeru na téže instanci a tenhle worker
**nesahá na binárky ani nespouští druhého démona** — jen zaloguje
`Tailscale: tailscaled already running …` a pokračuje `tailscale up` (je idempotentní),
forwarderem a probem. Proč to tak musí být, viz [Známá omezení](#6-známá-omezení-a-rizika).

```
tailscaled --tun=userspace-networking --socks5-server=localhost:1055
        ↓ (uzel v tailnetu, SOCKS5 na 127.0.0.1:1055)
tailscale --socket=/tmp/tailscaled.sock up --authkey=… --hostname=$TAILSCALE_HOSTNAME
        ↓ (uzel autentizovaný)
Socks5Forwarder  127.0.0.1:15432  →  SOCKS5  →  100.69.241.17:5544
        ↓
Npgsql (ConnectionStrings__DefaultConnection = Host=127.0.0.1;Port=15432;…)
```

Nakonec se ještě zkusí **reachability probe** (max 10 pokusů po 2 s, každý s vlastním
4s timeoutem). Neúspěch je jen `Warning` — EF Core má `EnableRetryOnFailure`, takže migrace
dostane další šanci. Celý řetěz (přihlášení uzlu i probe) má **jeden společný rozpočet
100 s**; když se do něj nevejde, start pokračuje bez tunelu, aby hostitele nezabil platformní
timeout.

**Bez `TAILSCALE_AUTHKEY` se nic z toho nestane.** Zaloguje se jediný řádek
`Tailscale: TAILSCALE_AUTHKEY not set, tunnel disabled` a běh pokračuje beze změny —
proto lokální vývoj, produkce i unit testy fungují dál stejně.

Když start tunelu selže, hostitel **nespadne**: výjimka se odchytí a zaloguje jako

```text
Startup: Tailscale tunnel failed — database unreachable until resolved
```

Timer triggery i `/api/diagnostic/health` tedy zůstanou dostupné. Pozor na to, co health
v tomhle stavu odpoví: je chráněný JWT tokenem SysAdmina a přihlášení potřebuje tu samou
databázi, takže bez dřív vydaného tokenu dostaneš **401**, ne 503. Řádek v logu výš je proto
spolehlivější signál než HTTP kód.

## 3. Soubory

| Soubor | Co dělá |
|---|---|
| `TailscaleTunnel.cs` | Spustí démona, autentizuje uzel, nastartuje forwarder, ověří dosažitelnost. |
| `Socks5Forwarder.cs` | TCP listener na loopbacku → SOCKS5 CONNECT → cíl. Bez SOCKS5 by Npgsql neměl kudy. |
| `tsbin/` (gitignored) | `tailscale` + `tailscaled`. **Nejsou v repu** — stahuje je deploy workflow. |

Binárky se za běhu kopírují do `/tmp/tsbin` a teprve tam dostanou spustitelný bit:
package mount na Flex Consumption může být read-only. Ze stejného důvodu má démon
`--socket=/tmp/tailscaled.sock` (výchozí `/var/run/tailscale/` je nezapisovatelný) —
a **stejný přepínač musí nést i každé volání CLI**, jinak mluví na jinou cestu a zatuhne.

## 4. Auth key a ACL (dělá se jednou, v Tailscale admin konzoli)

1. **Tag** — v *Access controls* si zaveď tag pro funkce, např. `tag:fakvio-func`,
   a povol si ho jako vlastníka (`tagOwners`).
2. **Auth key** — *Settings → Keys → Generate auth key*:
   - ✅ **Reusable** — každý cold start je nový uzel, jednorázový klíč by vydržel jeden start.
   - ✅ **Ephemeral** — uzel se po zhasnutí instance sám odstraní; jinak se tailnet zaplní
     mrtvými `fakvio-func-N`.
   - ✅ **Tags:** `tag:fakvio-func`.
   - Expiraci nastav vědomě — viz [Známá omezení](#6-známá-omezení-a-rizika).
3. **ACL** — funkce potřebuje jediné spojení, na port databáze:

   ```jsonc
   {
     "acls": [
       {
         "action": "accept",
         "src": ["tag:fakvio-func"],
         "dst": ["100.69.241.17:5544"]
       }
     ]
   }
   ```

4. Na databázovém serveru musí `pg_hba.conf` pouštět tailnet rozsah `100.64.0.0/10` —
   a to řádkem svázaným s dvojicí role/databáze (`host fakvio_test fakvio_test 100.64.0.0/10
   scram-sha-256`), ne širokým `host all all`. Právě tenhle řádek spolu s
   `REVOKE CONNECT … FROM PUBLIC` odděluje přihlášení do testu od produkce — přesné příkazy
   a ověření jsou v `SELFHOST-DB.md` §7.

## 5. App Settings

Function App → *Settings → Environment variables*. Dvojité podtržítko = oddělovač sekcí.

| Klíč | Hodnota | Poznámka |
|---|---|---|
| `TAILSCALE_AUTHKEY` | `tskey-auth-…` | **Chybí ⇒ tunel je vypnutý.** Jediný spínač celé funkce. |
| `ConnectionStrings__DefaultConnection` | `Host=127.0.0.1;Port=15432;Database=fakvio_test;Username=fakvio_test;Password=***;Ssl Mode=Prefer;Timezone=UTC;Maximum Pool Size=20;Timeout=15` | Míří na **forwarder**, ne na databázi. Role je **per prostředí**: `fakvio_test` k `fakvio_test`, `fakvio_prod` k `fakvio_prod`. |
| `Database__AuthMode` | `Password` | |
| `UseAzureAdAuthentication` | `false` | Musí souhlasit s předchozím řádkem, jinak start spadne na fail-fast kontrole (`SELFHOST-DB.md` §7). |
| `TAILSCALE_HOSTNAME` | `fakvio-func-prod` / `fakvio-func-test` | Jméno uzlu v tailnetu. Prod a test sdílejí tailnet, takže **každé prostředí musí mít vlastní**; bez klíče se použije `fakvio-func-prod`. |
| `TAILSCALE_TARGET_HOST` | *(volitelné)* výchozí `100.69.241.17` | Musí být **IPv4 tailnet adresa**; MagicDNS jméno kód odmítne — userspace režim resolver do procesu nezapojuje. |
| `TAILSCALE_TARGET_PORT` | *(volitelné)* výchozí `5544` | |

`Ssl Mode=Prefer`, protože WireGuard provoz už šifruje a certifikát vystavený na
`127.0.0.1` se stejně nedá ověřit. `Timeout=15`, protože první spojení zahrnuje
WireGuard handshake.

**Rollback:** smazat `TAILSCALE_AUTHKEY` a vrátit placeholder connection string, restart.
Redeploy není potřeba — bez klíče je kód nečinný.

## 6. Známá omezení a rizika

- **Cold start je delší o ~3–8 s** (start démona + přihlášení uzlu). V nejhorším případě,
  když se `tailscale up` třikrát vyčerpá do timeoutu, přibude až ~100 s; pak je namístě
  snížit počet pokusů.
- **Scale-out = uzel na instanci.** Každá instance se přihlásí zvlášť, Tailscale jim dá
  jména `fakvio-func`, `fakvio-func-1`, … Díky *ephemeral* klíči se po zhasnutí uklidí samy.
- **Expirace auth key rozbije budoucí cold starty tiše.** Běžící instance jedou dál, ale
  nová se nepřihlásí a v logu bude `Tailscale: up failed`. Klíč je potřeba rotovat dřív,
  než vyprší — nastav si na to připomínku podle zvolené expirace.
- **`/tmp` a spouštění potomků.** Kdyby sandbox `/tmp` připojil s `noexec` nebo blokoval
  child procesy, tunel se nepostaví, hostitel poběží dál a databáze bude nedostupná
  (health 401 bez tokenu, 503 s tokenem — viz část 2).
  Řešením je pak „Cesta A" (subnet router).
- **Na jedné instanci běží víc worker procesů.** Flex Consumption škáluje worker procesy
  uvnitř jedné instance (v traces se to pozná podle opakovaného `Host.Triggers.Warmup`).
  Všechny sdílejí jeden sandbox, tedy i `/tmp` a loopback. Důsledky, se kterými kód počítá
  (issue #321):
  - Kopie do `/tmp/tsbin` se **přeskočí**, když tam soubor už je ve stejné délce. Přepis
    souboru, který běžící `tailscaled` drží otevřený, Linux odmítne s `ETXTBSY`
    (`System.IO.IOException: Text file busy`) — dřív to shodilo celý start tunelu **před**
    spuštěním forwarderu a instance pak jela bez databáze.
  - `IOException` při kopii je jen `Warning`, pokud po ní v cíli leží **úplný soubor**
    (existuje a má stejnou délku jako zdroj) — spuštěný soubor je z definice funkční
    binárka. Kontroluje se čerstvě, až v okamžiku chyby: sourozenec stačil kopírovat
    i mezi naší kontrolou a naší kopií. Usečnutá kopie (např. plný disk) fatální zůstává.
  - Když `127.0.0.1:15432` už drží forwarder jiného workeru, `Socks5Forwarder.Start` vrátí
    `null` a jen to zaloguje. Loopback je sdílený, takže connection string toho druhého
    forwarderu využije i tenhle worker.
  - Selhání **kteréhokoli kroku** stavby tunelu (kopie, spuštění démona i `tailscale up`)
    není fatální, pokud SOCKS port žije — uzel už přihlásil ten, kdo démona spustil,
    a forwarder musí nastartovat tak jako tak. Démon, který závod prohraje, končí dřív,
    než vítěz sváže port 1055, takže se na něj krátce počká (až 15 × 1 s, uvnitř
    stovkového rozpočtu). Fatální zůstává jen chybějící **zdrojová** binárka
    v balíčku — to není závod, ale rozbitý deploy.
- **Výstup `tailscaled` je na úrovni `Debug`**, takže při výchozí `Information` v
  `host.json` není vidět. Při ladění dočasně zvyš úroveň pro kategorii
  `Fakvio.Functions.Tailscale`.
- **Worker `ILogger` do App Insights zatím nedoletí** — v `traces` jsou jen host kategorie
  a `Host.Function.Console` (= stdout worker procesu), kategorie `Fakvio.*` chybí; viz
  **issue #322**. Proto se milníky tunelu (`disabled`, `tailscaled already running`, `up OK`,
  `forwarder …`, `target reachable`, `failed: …`) píšou **i na `Console.Out`** s prefixem
  `Tailscale:`. V Azure se hledají takhle:

  ```kusto
  traces | where message startswith "Tailscale:" | order by timestamp desc
  ```

  Auth key se do těchhle řádků nikdy nedostane — výstup CLI je před logováním redigovaný.
- **Balíček je o ~50 MB větší**, deploy je tedy o něco pomalejší.

## 7. Lokální ověření (WSL / Linux)

Aplikaci s tunelem lze zkusit i mimo Azure — postup je stejný jako v Azure, jen se
proměnné nastavují v shellu:

```bash
# 1) binárky (stejná verze jako ve workflow)
mkdir -p Fakvio.Functions/tsbin
curl -fsSL -o ts.tgz https://pkgs.tailscale.com/stable/tailscale_1.102.3_amd64.tgz
tar -xzf ts.tgz --strip-components=1 -C Fakvio.Functions/tsbin \
  tailscale_1.102.3_amd64/tailscale tailscale_1.102.3_amd64/tailscaled

# 2) konfigurace
export TAILSCALE_AUTHKEY='tskey-auth-…'
export ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=15432;Database=fakvio_test;Username=fakvio_test;Password=***;Ssl Mode=Prefer;Timezone=UTC'
export Database__AuthMode=Password

# 3) spuštění a kontrola logu
dotnet run --project Fakvio.Functions
```

V logu musí být `Tailscale: up OK`, `Tailscale: forwarder 127.0.0.1:15432 -> …`
a `Tailscale: target reachable`. Samotný tunel bez aplikace se dá ověřit i ručně:

```bash
psql "host=127.0.0.1 port=15432 dbname=fakvio_test user=fakvio_test sslmode=prefer"
```

## 8. Bump verze Tailscale

Verze i kontrolní součet jsou **napsané v obou workflow souborech**
(`.github/workflows/testenv_zcloudinvoicingapi.yml` a `master_zcloudinvoicingapi.yml`,
blok `env:`). Postup:

```bash
V=1.104.0
curl -fsSL https://pkgs.tailscale.com/stable/tailscale_${V}_amd64.tgz.sha256
```

Vypsaný hash a novou verzi zapiš **do obou souborů zároveň** (`TAILSCALE_VERSION`,
`TAILSCALE_SHA256`) a aktualizuj i příklad v části 7 výše. Hash se schválně **nestahuje
za běhu** — to by z kontroly udělalo prázdné gesto.
