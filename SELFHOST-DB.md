# SELFHOST-DB.md — přesun databáze z Azure PostgreSQL na vlastní server

Runbook pro jednorázový přesun produkční databáze Fakvio z **Azure Database for
PostgreSQL – Flexible Server** (autentizace Entra ID) na **vlastní PostgreSQL server**
(autentizace heslem).

> **Rozsah dokumentu je jen databáze.** Self-hosting samotné aplikace (Dockerfile,
> systemd unit, deploy pipeline, reverse proxy) je mimo scope — k tomu viz
> [varovný odstavec pro nový host](#varování-jakýkoli-nový-host-musí-nastavit-dvě-věci).

**Předpoklad:** kód už umí přepínatelnou DB autentizaci (story #131, tasky #132–#139).
Bez ní se přepnutí na heslo neobejde bez zásahu do kódu.

---

## Obsah

1. [Pre-flight (read-only, dny předem)](#1-pre-flight-read-only-dny-předem)
2. [Cutover](#2-cutover)
3. [Ověření (před prvním připojením aplikace)](#3-ověření-musí-projít-dřív-než-na-novou-db-ukáže-jakákoli-aplikace)
4. [Data Protection key ring — make-or-break](#4-data-protection-key-ring--make-or-break)
5. [Rollback](#5-rollback)
6. [Rizika, která selhávají tiše](#6-rizika-která-selhávají-tiše)
7. [Připojovací řetězec pro vlastní hosting](#7-připojovací-řetězec-pro-vlastní-hosting)

### Proměnné použité v celém dokumentu

Nastav si je jednou na začátku, všechny příkazy níže je používají doslova.
**Do žádného souboru v repozitáři je necommituj.**

```bash
# Zdroj — Azure. Heslo se nezadává, autentizace jde přes Entra ID token.
export PGPASSWORD_SRC="$(az account get-access-token \
  --resource-type oss-rdbms --query accessToken -o tsv)"
export SRC="host=zcloudpostgresql.postgres.database.azure.com port=5432 dbname=postgres user=zahalos_seznam.cz#EXT#@zahalosseznam.onmicrosoft.com sslmode=require"

# Cíl — administrativní připojení. Role `fakvio` ani databáze `fakvio` zatím
# NEEXISTUJÍ (zakládají se až v kroku 2.4), takže se všude až do 2.4 připojujeme
# jako superuser do údržbové databáze `postgres`.
export DST_ADMIN="host=novy-db-server.example.cz port=5432 dbname=postgres user=postgres sslmode=prefer"

# Cíl — aplikační připojení. Definuje se AŽ V KROKU 2.4, po založení role a DB.
# Tady je jen pro představu, jak bude vypadat:
#   export DST="host=novy-db-server.example.cz port=5432 dbname=fakvio user=fakvio password=*** sslmode=prefer"

export WORKDIR="$HOME/fakvio-migrace-$(date +%Y%m%d)"
mkdir -p "$WORKDIR"

# `psql` po chybě defaultně pokračuje dál a skončí s exit code 0 — u runbooku,
# jehož nejdůležitější kontrolou je `diff`, by to znamenalo „zelenou" na dvojici
# prázdných souborů. Zapneme ON_ERROR_STOP jednou pro všechna volání `psql` níže.
export PSQLRC="$WORKDIR/psqlrc"
printf '\\set ON_ERROR_STOP on\n' > "$PSQLRC"

# Roury typu `psql … | sort > soubor` jinak vracejí exit code posledního článku.
set -o pipefail
```

> `psql "$SRC"` proti Azure vyžaduje `PGPASSWORD="$PGPASSWORD_SRC"` — token má
> **platnost ~60 minut**, u dlouhého dumpu si ho obnov znovu.
>
> **Heslo v conninfo řetězci:** mezera, `'` nebo `\` v hesle parsování rozbijí.
> Buď hodnotu obal apostrofy (`password='he slo'`), nebo heslo do conninfo vůbec
> nedávej a použij `PGPASSFILE` / proměnnou `PGPASSWORD`.
>
> Runbook se pouští **příkaz po příkazu** a u každého se kontroluje očekávaný
> výsledek — proto tu není `set -e`; ten by v interaktivním shellu při první
> chybě zavřel celé sezení.

---

## 1. Pre-flight (read-only, dny předem)

Nic se nemění, jen se zjišťuje. Výstupy si **zapiš do souboru** — v části 3 se proti
nim diffuje.

### 1.1 Verze PostgreSQL na cíli musí být ≥ zdroj

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "SHOW server_version;"
psql "$DST_ADMIN" -Atc "SHOW server_version;"
```

**Očekávaný výsledek:** verze cíle je stejná nebo vyšší. `pg_restore` z novější verze
do starší **neprojde** — formát custom dumpu není zpětně kompatibilní.

### 1.2 Extensions — předinstalovat na cíli

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc \
  "SELECT extname, extversion FROM pg_extension ORDER BY 1;" \
  | tee "$WORKDIR/extensions-source.txt"
```

**Očekávaný výsledek:** typicky jen `plpgsql|1.0`. Cokoli navíc (`uuid-ossp`,
`pg_trgm`, `citext`, …) musí být na cíli nainstalované **před** restore. Samotná
instalace je až v [kroku 2.4](#24-příprava-cíle--role-databáze-extensions) — cílová
databáze `fakvio` v tuhle chvíli ještě neexistuje. Tady jen ověř, že balíček
příslušné extension je na serveru vůbec k dispozici:

```bash
psql "$DST_ADMIN" -Atc \
  "SELECT name, default_version FROM pg_available_extensions ORDER BY 1;" \
  | grep -E 'uuid-ossp|pg_trgm|citext'   # jen ty ze seznamu výše
```

Azure-specifické extensions (`azure_sys`, `pg_stat_statements` v Azure variantě)
se **nepřenášejí** — schémata `azure_sys` a `azure_maintenance` dump vylučuje
(viz krok 2.2).

### 1.3 Autoritativní seznam schémat

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "
SELECT nspname, pg_get_userbyid(nspowner) AS owner
FROM pg_namespace
WHERE nspname = 'public' OR nspname LIKE 'tenant\_%'
ORDER BY 1;" | tee "$WORKDIR/schemas-source.txt"
```

**Očekávaný výsledek:** `public` (master data) + jedno `tenant_{CompanyId}` schéma
na každou naprovisionovanou firmu. Vlastník bude Entra principal
(`…#EXT#@….onmicrosoft.com`) — to je normální a na cíli existovat nebude, proto
krok 2.5.

### 1.4 Timezone na obou serverech

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "SHOW timezone;"
psql "$DST_ADMIN" -Atc "SHOW timezone;"
```

**Očekávaný výsledek:** **obojí `UTC`.** Azure Flexible Server má `UTC` defaultně,
vlastní box běžně přebírá zónu OS (`Europe/Prague`). Když se liší, `now()`,
`CURRENT_DATE` a defaulty sloupců začnou po přesunu vracet jiné hodnoty.
Oprava na cíli — `postgresql.conf`:

```
timezone = 'UTC'
```

a restart serveru. Kontrola v connection stringu je v [části 7](#7-připojovací-řetězec-pro-vlastní-hosting).

### 1.5 Baseline row counts

Dotaz se **generuje ze zdrojového katalogu**, aby zahrnul všechna schémata i tabulky
bez ručního výčtu. Vygenerovaný soubor se pak pustí na obou serverech (část 3).

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "
SELECT string_agg(
         format('SELECT %L AS schema_name, %L AS table_name, count(*) AS row_count FROM %I.%I',
                schemaname, tablename, schemaname, tablename),
         E'\nUNION ALL\n' ORDER BY schemaname, tablename)
FROM pg_tables
WHERE schemaname = 'public' OR schemaname LIKE 'tenant\_%';
" > "$WORKDIR/rowcounts.sql"
echo ";" >> "$WORKDIR/rowcounts.sql"

PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atf "$WORKDIR/rowcounts.sql" \
  | LC_ALL=C sort > "$WORKDIR/rowcounts-baseline.txt"
wc -l "$WORKDIR/rowcounts-baseline.txt"
```

**Očekávaný výsledek:** neprázdný soubor, řádky ve tvaru
`public|Users|37`. Počet řádků = počet tabulek napříč všemi schématy.

> ### Proč `LC_ALL=C sort` a ne `ORDER BY` v dotazu
>
> Každý server řadí text podle **své** default collation (`datcollate`). Azure
> Flexible Server jede typicky `en_US.utf8`, čerstvě nainstalovaný vlastní box
> `C.UTF-8` nebo `cs_CZ.UTF-8`. Identifikátory Fakvia (`"Users"`,
> `"__EFMigrationsHistory"`, `tenant_1` vs. `tenant_10`) se pod těmito collation
> řadí **jinak** — dva soubory s identickými daty by pak `diff` nahlásil jako
> rozdíl a runbook by operátora zastavil uprostřed zdravého cutoveru.
> Řazení proto dělá až `sort` na klientovi, se stejnou (bajtovou) collation pro
> oba soubory. `ORDER BY 1 COLLATE "C"` řešení **není** — na ordinálu spadne na
> `collations are not supported by type integer`.
>
> Stejný `LC_ALL=C sort` se ze stejného důvodu používá i v částech 2.2 a 3.

> Baseline z pre-flightu je jen orientační (data se mezitím mění). **Závazný** je
> ten, který se pořídí v kroku 2.1 po zastavení aplikace.

### 1.6 Blokující kontrola Data Protection key ringu

**Toto je go/no-go brána celé migrace.** Podrobně v [části 4](#4-data-protection-key-ring--make-or-break),
ale spusť ji už teď — výsledek rozhoduje, jestli je cutover otázkou 20 minut,
nebo jestli je potřeba naplánovat ruční obnovu všech secretů a re-registraci 2FA.

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc \
  'SELECT left("Xml", 600) FROM public."DataProtectionKeys";'
```

### 1.7 Rozhodnout jméno databáze

Azure databáze se jmenuje doslova **`postgres`** (viz
`Fakvio.API/appsettings.json:8`). Na vlastním serveru je `postgres` konvenčně
údržbová databáze. **Rozhodni předem:**

| Volba | Důsledek |
|---|---|
| zachovat `Database=postgres` | connection string se mění jen v hostu/uživateli, ale aplikační data leží v maintenance DB |
| přejmenovat na `fakvio` | čistší, ale **musí se upravit connection string** ve všech hostech (API, Functions, MigrationTool) |

Tento runbook dál předpokládá **`fakvio`**.

---

## 2. Cutover

Od tohoto bodu je aplikace mimo provoz. Reálné okno: 15–40 minut podle velikosti dat.

### 2.1 Zastavit aplikaci

```bash
az functionapp stop --name <function-app-name> --resource-group <rg>
# a pokud běží i klasický API host:
az webapp stop --name <api-app-name> --resource-group <rg>
```

**Očekávaný výsledek:** příkaz projde bez chyby, `az functionapp show --name <…>
--resource-group <rg> --query state -o tsv` vrátí `Stopped`.

### 2.2 Ověřit, že do DB nikdo nepíše

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "
SELECT count(*) FROM pg_stat_activity
WHERE datname = 'postgres'
  AND pid <> pg_backend_pid()
  AND backend_type = 'client backend';"
```

**Očekávaný výsledek: `0`.** Když ne, počkej — Azure Functions dobíhají invokace
i po `stop`. Vypiš, kdo drží spojení:

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "
SELECT pid, usename, application_name, state, query_start
FROM pg_stat_activity
WHERE datname = 'postgres' AND pid <> pg_backend_pid()
  AND backend_type = 'client backend';"
```

Teprve při nule pořiď **závazný baseline** row counts:

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atf "$WORKDIR/rowcounts.sql" \
  | LC_ALL=C sort > "$WORKDIR/rowcounts-source.txt"
```

> Kontrola na nulu není bariéra zápisu — klient se stihne připojit zpět mezi ní,
> baselinem a dumpem. Jediní writeři jsou dvě Azure app zastavené v 2.1, takže
> stačí **dotaz na `pg_stat_activity` zopakovat těsně před `pg_dump`** (krok 2.3).
> Kdo chce tvrdou bariéru, přidá jako superuser
> `REVOKE CONNECT ON DATABASE postgres FROM PUBLIC;` a po dumpu ji vrátí.

### 2.3 Dump

Ještě jednou zopakuj kontrolu writerů z 2.2 (musí být pořád `0`) a hned pak dumpuj:

```bash
PGPASSWORD="$PGPASSWORD_SRC" pg_dump \
  --host=zcloudpostgresql.postgres.database.azure.com \
  --port=5432 \
  --username='zahalos_seznam.cz#EXT#@zahalosseznam.onmicrosoft.com' \
  --dbname=postgres \
  --format=custom \
  --no-owner \
  --no-privileges \
  --exclude-schema=azure_sys \
  --exclude-schema=azure_maintenance \
  --verbose \
  --file="$WORKDIR/fakvio.dump"
```

**Očekávaný výsledek:** `pg_dump` skončí s exit code 0, soubor `fakvio.dump` má
nenulovou velikost. Kontrola obsahu bez rozbalování:

```bash
pg_restore --list "$WORKDIR/fakvio.dump" | grep -c "TABLE DATA"
```

**Očekávaný výsledek:** číslo se rovná `wc -l "$WORKDIR/rowcounts-source.txt"`.
Custom-format dump vyrábí položku `TABLE DATA` i pro tabulku s nula řádky, takže
se **nic neodečítá** — každá tabulka má právě jednu položku. Nižší číslo znamená,
že se nějaká tabulka do dumpu nedostala.

> **`--no-owner --no-privileges` jsou povinné, ne kosmetické.**
> Všechny owner a ACL záznamy v Azure dumpu odkazují na Entra principaly
> (`…#EXT#@…`), které na vlastním serveru **nemohou existovat**. Bez těchto flagů
> `pg_restore` skončí sérií `role "…#EXT#@…" does not exist`.
>
> **`pg_dumpall -r` (globals/role) nepoužívat** — Entra role nejsou přenositelné
> a jejich import na vlastní server nedává smysl.

Zálohu si hned odlož na druhé místo (viz [část 5](#5-rollback)):

```bash
# Součet se počítá z adresáře dumpu, aby v .sha256 byla RELATIVNÍ cesta —
# jinak `sha256sum -c` na kopii ověří pořád originál, ne kopii.
( cd "$WORKDIR" && sha256sum fakvio.dump > fakvio.dump.sha256 && cat fakvio.dump.sha256 )
cp "$WORKDIR"/fakvio.dump{,.sha256} /mnt/zaloha/   # nebo jiné nezávislé úložiště
( cd /mnt/zaloha && sha256sum -c fakvio.dump.sha256 )
```

**Očekávaný výsledek:** `fakvio.dump: OK` — a to **v adresáři kopie**, ne v `$WORKDIR`.

### 2.4 Příprava cíle — role, databáze, extensions

Nejdřív ověř, že cílová databáze ještě **neexistuje** — restore do neprázdné DB
sice `--exit-on-error` shodí, ale až na prvním duplicitním objektu, tedy po
částečném zápisu:

```bash
psql "$DST_ADMIN" -Atc "SELECT datname FROM pg_database WHERE datname = 'fakvio';"
```

**Očekávaný výsledek: prázdný výstup.** Když něco vypíše, rozhodni se, jestli
databázi zahodit (`DROP DATABASE fakvio`), nebo restore přesměrovat jinam.

Jako superuser na novém serveru:

```bash
psql "$DST_ADMIN" <<'SQL'
CREATE ROLE fakvio WITH LOGIN PASSWORD 'ZMEN_ME';
CREATE DATABASE fakvio OWNER fakvio;
SQL
```

**Očekávaný výsledek:** `CREATE ROLE`, `CREATE DATABASE`.

`OWNER fakvio` není detail — vlastník databáze má `CREATE` (a tedy i `CREATE SCHEMA`)
implicitně, což je přesně to právo, které potřebuje provisioning dalších tenantů
(viz [riziko 6.1](#61-vlastnictví-schémat-a-granty-psané-pro-entra-principaly)).
Kdybys databázi zakládal pod jiným vlastníkem, doplň
`GRANT CREATE ON DATABASE fakvio TO fakvio;` ručně.

**Teprve teď** dává smysl aplikační conninfo — role i databáze existují:

```bash
export DST="host=novy-db-server.example.cz port=5432 dbname=fakvio user=fakvio password='ZMEN_ME' sslmode=prefer"
psql "$DST" -Atc "SELECT current_user, current_database();"   # očekávané: fakvio|fakvio
```

Extensions ze seznamu z kroku 1.2 (jako superuser, do DB `fakvio`):

```bash
psql "host=novy-db-server.example.cz port=5432 dbname=fakvio user=postgres" \
  -c 'CREATE EXTENSION IF NOT EXISTS "uuid-ossp";'   # jen ty z extensions-source.txt
```

### 2.5 Restore

```bash
pg_restore \
  --host=novy-db-server.example.cz --port=5432 \
  --username=fakvio --dbname=fakvio \
  --no-owner --no-privileges \
  --role=fakvio \
  --exit-on-error \
  --verbose \
  "$WORKDIR/fakvio.dump" 2>&1 | tee "$WORKDIR/restore.log"
echo "pg_restore exit code: ${PIPESTATUS[0]}"
```

**Očekávaný výsledek: `pg_restore exit code: 0`.** Bez `${PIPESTATUS[0]}` (nebo
`set -o pipefail` z bloku proměnných) by `$?` byl stav `tee`, tedy vždycky 0.
`--exit-on-error` je záměrné: tichý částečný restore je horší než hlasité selhání
— a zároveň dělá z exit code jedinou potřebnou kontrolu.

```bash
grep -c "^pg_restore: error" "$WORKDIR/restore.log"   # očekávané: 0
```

> Grepuje se **prefix `pg_restore: error`**, ne slovo „error" kdekoli na řádku:
> `--verbose` vypisuje jména objektů a tabulka jménem `ErrorLogs` by jinak
> shodila zdravý restore.

### 2.6 Srovnat vlastnictví schémat

Restore s `--no-owner` nechá schémata vlastněná tím, kdo restore spustil.
Explicitně to dorovnej — je to podmínka funkčního provisioningu dalších tenantů:

```bash
psql "$DST" <<'SQL'
DO $$
DECLARE s text;
BEGIN
  FOR s IN
    SELECT nspname FROM pg_namespace
    WHERE nspname = 'public' OR nspname LIKE 'tenant\_%'
  LOOP
    EXECUTE format('ALTER SCHEMA %I OWNER TO fakvio', s);
  END LOOP;
END $$;
SQL
```

Kontrola:

```bash
psql "$DST" -Atc "
SELECT nspname, pg_get_userbyid(nspowner)
FROM pg_namespace
WHERE nspname = 'public' OR nspname LIKE 'tenant\_%'
ORDER BY 1;"
```

**Očekávaný výsledek:** u **každého** řádku vlastník `fakvio`. Počet řádků se musí
shodovat s `$WORKDIR/schemas-source.txt` z kroku 1.3.

---

## 3. Ověření (musí projít **dřív**, než na novou DB ukáže jakákoli aplikace)

> ### Proč právě teď a ani o minutu později
>
> První start API i Functions volá `MigrateAsync()`
> (`Fakvio.API/Program.cs:132`, `Fakvio.Functions/Program.cs:152`).
> Když je migrační historie neúplná nebo rozbitá, EF Core začne
> **re-aplikovat migrace na plné tabulky** — `CREATE TABLE` na existující tabulku,
> `ADD COLUMN` na existující sloupec, v horším případě data-seeding podruhé.
> Náprava je pak restore ze zálohy, ne oprava za běhu.
>
> Kroky 3.1 a 3.2 musí projít **zelené, než se změní jediná app setting**.

### 3.1 Row counts per tabulka per schéma

Použij **tentýž** vygenerovaný soubor ze zdrojového katalogu (krok 1.5).
Tím se zároveň prokáže, že na cíli žádná tabulka nechybí — kdyby chyběla,
dotaz na ni spadne na `relation … does not exist`.

```bash
psql "$DST" -Atf "$WORKDIR/rowcounts.sql" \
  | LC_ALL=C sort > "$WORKDIR/rowcounts-target.txt"
diff "$WORKDIR/rowcounts-source.txt" "$WORKDIR/rowcounts-target.txt" \
  && echo "OK — row counts sedí"
```

**Očekávaný výsledek:** `diff` **prázdný**, vypíše se `OK — row counts sedí`.
Jakýkoli řádek na výstupu = **stop**, nepokračovat.

> Oba soubory jsou seřazené `LC_ALL=C sort` (viz [poznámku ke collation](#proč-lc_allc-sort-a-ne-order-by-v-dotazu)),
> takže rozdílné default collation obou serverů nemůžou vyrobit falešný mismatch.
> Zkontroluj taky, že soubory nejsou **prázdné** (`wc -l`) — prázdný vstup by
> `diff` odbavil jako shodu.

### 3.2 `__EFMigrationsHistory` v **každém** schématu

Fakvio má migrační historii **per tenant schéma**, ne jednu globální —
`TenantProvisioningService` mapuje `b.MigrationsHistoryTable("__EFMigrationsHistory", safeName)`
(`Fakvio.Infrastructure/Service/TenantProvisioningService.cs:636`).

```bash
# Generátor dotazu — opět ze zdrojového katalogu.
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "
SELECT string_agg(
         format('SELECT %L AS schema_name, \"MigrationId\" FROM %I.\"__EFMigrationsHistory\"',
                table_schema, table_schema),
         E'\nUNION ALL\n' ORDER BY table_schema)
FROM information_schema.tables
WHERE table_name = '__EFMigrationsHistory';
" > "$WORKDIR/migrations.sql"
echo ";" >> "$WORKDIR/migrations.sql"

PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atf "$WORKDIR/migrations.sql" \
  | LC_ALL=C sort > "$WORKDIR/migrations-source.txt"
psql "$DST" -Atf "$WORKDIR/migrations.sql" \
  | LC_ALL=C sort > "$WORKDIR/migrations-target.txt"

diff "$WORKDIR/migrations-source.txt" "$WORKDIR/migrations-target.txt" \
  && echo "OK — migrační historie sedí"
```

**Očekávaný výsledek:** `diff` **prázdný** a oba soubory neprázdné
(řazení opět klientské, viz [poznámku ke collation](#proč-lc_allc-sort-a-ne-order-by-v-dotazu)).

Nezávislá kontrola počtu schémat (chytne schéma, které se do dumpu nedostalo).
Čte se z `pg_class`, ne z `information_schema` — to ukazuje jen objekty viditelné
pro aktuální roli, takže by kontrola závisela na grantech místo na datech:

```bash
psql "$DST" -Atc "
SELECT
  (SELECT count(*) FROM pg_class c
   JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE c.relname = '__EFMigrationsHistory' AND c.relkind = 'r'
     AND (n.nspname = 'public' OR n.nspname LIKE 'tenant\_%'))       AS historii,
  (SELECT count(*) FROM public.\"CompanySystemSettings\"
   WHERE \"IsProvisioned\") + 1                                      AS ocekavano;"
```

**Očekávaný výsledek:** obě čísla **stejná**. `+1` je `public` (master schéma).

### 3.3 Sekvence

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc "
SELECT schemaname, sequencename, last_value FROM pg_sequences
WHERE schemaname = 'public' OR schemaname LIKE 'tenant\_%';" \
  | LC_ALL=C sort > "$WORKDIR/sequences-source.txt"

psql "$DST" -Atc "
SELECT schemaname, sequencename, last_value FROM pg_sequences
WHERE schemaname = 'public' OR schemaname LIKE 'tenant\_%';" \
  | LC_ALL=C sort > "$WORKDIR/sequences-target.txt"

diff "$WORKDIR/sequences-source.txt" "$WORKDIR/sequences-target.txt" \
  && echo "OK — sekvence sedí"
```

**Očekávaný výsledek:** `diff` prázdný (řazení opět klientské, viz
[poznámku ke collation](#proč-lc_allc-sort-a-ne-order-by-v-dotazu)). Kdyby
`last_value` na cíli zaostávalo, první insert spadne na duplicitní primární klíč.

### 3.4 Teprve teď přepnout aplikaci

Až když 3.1–3.3 prošly. Připojovací řetězec a **obě** konfigurační klíče viz
[část 7](#7-připojovací-řetězec-pro-vlastní-hosting).

Po startu ověř:

```bash
curl -s -H "Authorization: Bearer <sysadmin-jwt>"   https://<app>/api/diagnostic/health | jq '{authMode, authModeSource, masterDbCanConnect}'
```

> Endpoint je **SysAdmin only** (#138) — bez tokenu vrací 401. Token získáš stejně jako
> pro `credential-health` níž. Když je databáze nedostupná a login tedy neprojde,
> `authMode` se dá přečíst ze **startup logu** — oba hosty ho vypíšou hned po startu,
> kategorie `Fakvio.Infrastructure.Database`:
>
> ```text
> info: Fakvio.Infrastructure.Database[0]
>       Startup: database auth mode Password (source: Database:AuthMode)
> ```
>
> Řádek jde ven před prvním sáhnutím do databáze, takže je k dispozici i když je DB dole
> (v Azure: Log stream / Application Insights, lokálně stdout procesu). Připojovací řetězec
> v logu nikdy není — jen mód a zdrojový klíč.

**Očekávaný výsledek:**
```json
{ "authMode": "Password", "authModeSource": "Database:AuthMode", "masterDbCanConnect": true }
```

a pending migrations = 0. Pak go/no-go brána na secrety:

```bash
curl -s -H "Authorization: Bearer <sysadmin-jwt>" \
  https://<app>/api/system-configuration/credential-health | jq
```

**Očekávaný výsledek:** `{"healthy": true, "issues": []}`.

---

## 4. Data Protection key ring — make-or-break

Aplikace registruje Data Protection takto
(`Fakvio.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs:118-120`):

```csharp
services.AddDataProtection()
    .PersistKeysToDbContext<MasterDbContext>()
    .SetApplicationName("Fakvio");
```

Chybí `ProtectKeysWith*`, takže **at-rest ochrana sloupce `Xml` v tabulce
`public."DataProtectionKeys"` závisí na platformě, na které aplikace běžela** —
ne na konfiguraci. Přesun databáze přenese sloupec `Xml` beze změny; jestli je
po přesunu použitelný, rozhoduje jeho obsah.

### 4.1 Blokující pre-flight dotaz

**Bez tohoto výsledku se cutover neplánuje.**

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc \
  'SELECT left("Xml", 600) FROM public."DataProtectionKeys";'
```

### Větev A — klíče jsou plaintext ⇒ **přenositelné**

Výstup obsahuje holý `<masterKey>` s base64 `<value>` **bez wrapperu**:

```xml
<?xml version="1.0" encoding="utf-8"?>
<key id="..." version="1">
  <descriptor deserializerType="...AuthenticatedEncryptorDescriptorDeserializer...">
    <descriptor>
      <encryption algorithm="AES_256_CBC" />
      <validation algorithm="HMACSHA256" />
      <masterKey xmlns:p="...">
        <value>Base64Base64Base64...</value>
```

To je typický výstup **Linux hostu** — .NET tam nemá žádný default XML encryptor.

**Důsledek: nic se nerozbije.** Key ring je jen řádek v tabulce, přesune se s DB.
`SetApplicationName("Fakvio")` i purpose string `"Fakvio.Credentials.v1"`
(`Fakvio.Infrastructure/Service/CredentialProtector.cs:36`) jsou **compile-time
konstanty**, takže se s hostem nemění. Všechny zašifrované secrety zůstanou
dešifrovatelné. Část 4.3 přeskoč.

> Bezpečnostní poznámka mimo scope migrace: plaintext key ring v DB znamená, že kdo
> má čtecí přístup do `public."DataProtectionKeys"`, umí dešifrovat všechna uložená
> hesla. Na vlastním serveru je to argument pro `ProtectKeysWithCertificate` —
> ale to je samostatná změna kódu, **ne součást tohoto přesunu**.

### Větev B — klíče jsou DPAPI ⇒ **nepřenositelné**

Výstup obsahuje wrapper `<encryptedSecret>` s `decryptorType` obsahujícím `Dpapi`:

```xml
<masterKey p4:requiresEncryption="true" ...>
  <encryptedSecret decryptorType="Microsoft.AspNetCore.DataProtection.XmlEncryption.DpapiXmlDecryptor, ...">
    <value>AQAAANCMnd8BFdERjHoAwE/Cl+sB...</value>
```

To je typický výstup **Windows hostu** — default je DPAPI vázaná na účet a stroj.

**Důsledek: přesun DB přenese nepoužitelný ciphertext.** Klíče nejde dešifrovat
nikde jinde než na původním stroji pod původním účtem. Všechny secrety zašifrované
`CredentialProtector`em jsou po přesunu **ztracené** a musí se zadat ručně.

> **Selhání je tiché.** `CredentialProtector.Decrypt`
> (`Fakvio.Infrastructure/Service/CredentialProtector.cs:69`) chytá
> `CryptographicException` a vrací vstupní ciphertext jako „legacy plaintext".
> Aplikace tedy nastartuje, UI vypadá funkčně, přehledy jedou — a rozbije se až
> ve chvíli, kdy někdo pošle e-mail, spustí IMAP poll nebo zavolá AI. Proto je
> `credential-health` v části 3.4 povinná brána, ne volitelná kontrola.

### 4.2 Kontrola „aspoň jeden SysAdmin bez 2FA" — **před** cutoverem

Platí **jen pro větev B**, ale ověřuje se **předem**, protože po cutoveru už na to
může být pozdě: TOTP secrety uživatelů (`User.TotpSecretEncrypted`) jsou také
chráněné Data Protection API (`Fakvio.Infrastructure/Service/TwoFactorService.cs:87`).
Po ztrátě key ringu se **žádný uživatel s TOTP nepřihlásí** — druhý faktor nejde
ověřit a SysAdmin nemá UI pro force-reset cizí 2FA (viz `ADMINGUIDE.md` §9).

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc '
SELECT count(*) FROM public."Users"
WHERE "Role" = 2 AND "IsActive" = true AND "TwoFactorEnabled" = false;'
```

`"Role" = 2` je `EUserRole.SysAdmin` (`Fakvio.Domain/Enums/EUserRole.cs:23`,
ukládá se jako `int`). `"TwoFactorEnabled"` je non-nullable `bool`
(`Fakvio.Domain/Entities/User.cs:132`), takže větev `IS NULL` je zbytečná.

**Očekávaný výsledek: `>= 1`.** Když vyjde `0`, **cutover se nesmí provést** —
nikdo by se po něm nedostal do administrace. Náprava před cutoverem: založit
servisního SysAdmina bez 2FA, nebo jednomu stávajícímu 2FA dočasně vypnout.

Kolik uživatelů se bude muset znovu zaregistrovat:

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc '
SELECT count(*) FROM public."Users"
WHERE "TotpSecretEncrypted" IS NOT NULL AND "IsActive" = true;'
```

### 4.3 Seznam ručně obnovovaných secretů (jen větev B)

Autoritativní zdroj je `SystemConfigurationService.CheckCredentialHealthAsync`
(`Fakvio.Infrastructure/Service/SystemConfigurationService.cs:253-306`, samotný výčet
polí `:260-295`) — endpoint `credential-health` kontroluje přesně tato pole:

| Entita | Pole | Kde se obnovuje |
|---|---|---|
| `SystemConfiguration` (1 řádek) | `SmtpPassword` | SysAdmin → Systémová nastavení → SMTP |
| `SystemConfiguration` | `AiClaudeApiKey`, `AiOpenAiApiKey`, `AiGeminiApiKey` | SysAdmin → AI poskytovatelé |
| `SystemConfiguration` | `AzureBlobConnectionString` | SysAdmin → Systémová nastavení → úložiště |
| `CompanySystemSettings` (řádek na firmu) | `SmtpPassword` | Nastavení firmy → e-mail |
| `CompanySystemSettings` | `AiClaudeApiKey`, `AiOpenAiApiKey`, `AiGeminiApiKey` | Nastavení firmy → AI |
| `CompanySystemSettings` | `AzureBlobConnectionString` | Nastavení firmy → úložiště |
| `CompanySystemSettings` | `GoogleDriveAccessToken`, `GoogleDriveRefreshToken` | znovu projít OAuth flow Google Drive |
| `CompanySystemSettings` | `OneDriveAccessToken`, `OneDriveRefreshToken` | znovu projít OAuth flow OneDrive |
| `PaymentMatchingSystemSettings` (1 řádek) | `ImapPasswordEncrypted` | SysAdmin → IMAP + Párování plateb |

Plus **mimo `credential-health`**:

| Entita | Pole | Obnova |
|---|---|---|
| `User` (každý 2FA uživatel) | `TotpSecretEncrypted` | uživatel si musí **znovu naskenovat QR** a 2FA zaregistrovat |

> **Pozor: `credential-health: healthy` nestačí.** Endpoint TOTP secrety
> **nekontroluje** — projde zeleně i ve chvíli, kdy se žádný 2FA uživatel
> nepřihlásí. Stav 2FA se musí ověřit zvlášť dotazem z 4.2 a reálným přihlášením.

Kolik firem se to týká (spusť před cutoverem, ať víš rozsah):

```bash
PGPASSWORD="$PGPASSWORD_SRC" psql "$SRC" -Atc '
SELECT count(*) FROM public."CompanySystemSettings"
WHERE "SmtpPassword" IS NOT NULL
   OR "AiClaudeApiKey" IS NOT NULL
   OR "AiOpenAiApiKey" IS NOT NULL
   OR "AiGeminiApiKey" IS NOT NULL
   OR "AzureBlobConnectionString" IS NOT NULL
   OR "GoogleDriveRefreshToken" IS NOT NULL
   OR "OneDriveRefreshToken" IS NOT NULL;'
```

---

## 5. Rollback

**Azure server se během celé migrace nikdy nemodifikuje.** `pg_dump` je čtení,
`pg_restore` běží proti novému serveru. Azure data zůstávají nedotčená.

### Do point of no return

Dokud na nový server nepřistane **první uživatelský zápis**, je rollback
triviální — revert jedné app setting a restart, ~2 minuty:

```bash
az functionapp config appsettings set --name <function-app-name> --resource-group <rg> \
  --settings \
    'ConnectionStrings__DefaultConnection=Host=zcloudpostgresql.postgres.database.azure.com;Database=postgres;Port=5432;Username=zahalos_seznam.cz#EXT#@zahalosseznam.onmicrosoft.com;Ssl Mode=Require;' \
    'Database__AuthMode=AzureEntraId' \
    'UseAzureAdAuthentication=true'
az functionapp restart --name <function-app-name> --resource-group <rg>
```

**Očekávaný výsledek:** health hlásí `"authMode": "AzureEntraId"` a
`"masterDbCanConnect": true`.

> **Dokud App Settings nesou oba klíče, vrací se společně.** Když se revertuje jen jeden,
> `DatabaseOptions.Resolve` hodí `Conflicting database auth mode configuration`
> a aplikace nenaběhne vůbec — viz
> [část 7](#konfigurační-klíče--nový-klíč-stačí-legacy-nesmí-odporovat).

### Point of no return

**První zápis uživatele do nové databáze.** Od té chvíle by rollback znamenal
ztrátu dat vzniklých po přepnutí.

Proto je **pořadí závazné**:

1. přepnout konfiguraci,
2. ověřit (health, `credential-health`, smoke test dvou tenantů),
3. **teprve pak** pustit uživatele dovnitř.

Mezi 1 a 3 drž aplikaci v maintenance režimu / nepublikovanou.

### Po cutoveru

- **Azure server nechat běžet ještě 2 týdny.** Nemazat, nevypínat, nezmenšovat.
- **Dump držet na dvou nezávislých místech** a kontrolní součet z kroku 2.3
  ověřit **v adresáři každé kopie zvlášť** — jinak se pořád kontroluje originál:
  ```bash
  ( cd "$WORKDIR"   && sha256sum -c fakvio.dump.sha256 )
  ( cd /mnt/zaloha  && sha256sum -c fakvio.dump.sha256 )
  ```
- Teprve po dvou týdnech bezproblémového provozu řešit vyřazení Azure serveru.

---

## 6. Rizika, která selhávají tiše

Řazeno podle toho, jak dlouho trvá, než se problém projeví.

### 6.1 Vlastnictví schémat a granty psané pro Entra principaly

`TenantProvisioningService.GrantSchemaPermissionsAsync`
(`Fakvio.Infrastructure/Service/TenantProvisioningService.cs:432`) grantuje práva
`CURRENT_USER`. Jenže `GRANT ALL ON SCHEMA` **vyžaduje vlastnictví schématu**.
Když restore nechá schémata vlastněná jinou rolí, **příští provisioning tenanta
spadne na `must be owner of schema`** — a projeví se to teprve ve chvíli, kdy
někdo založí novou firmu, klidně za měsíc.

**Prevence:** krok 2.6 (`ALTER SCHEMA … OWNER TO fakvio`) + jeho kontrola.

**PostgreSQL 15+ — co se doopravdy změnilo:** roli `PUBLIC` bylo odebráno právo
`CREATE` na **schématu `public`**. Právo na `CREATE SCHEMA` se ale vždycky bralo
z **databáze**, a tam se nic nezměnilo — vlastník DB ho má implicitně. Dokud je
`fakvio` vlastníkem databáze (krok 2.4), žádný extra grant potřeba není. Zápis do
schématu `public` řeší krok 2.6, který ho převede na `fakvio`.

**Smoke test hned po cutoveru:** založ testovací firmu přes UI, ověř, že vznikne
`tenant_{id}` schéma, a firmu zase smaž.

### 6.2 `Ssl Mode` — Npgsql 8+ validuje certifikát

Azure connection string má `Ssl Mode=Require`. **Npgsql 8+ u `Require` certifikát
validuje** (dřívější chování odpovídá dnešnímu `Prefer`). Proti vlastnímu serveru
se self-signed certifikátem to padne na:

```
The remote certificate is invalid because of errors in the certificate chain
```

Volby pro vlastní hosting, od nejslabší po nejsilnější:

| Nastavení | Kdy použít |
|---|---|
| `Ssl Mode=Prefer` | DB na stejné privátní síti, TLS není povinné |
| `Ssl Mode=Require;Trust Server Certificate=true` | TLS ano, self-signed cert |
| `Ssl Mode=VerifyFull;Root Certificate=/cesta/ca.crt` | doporučené pro provoz přes veřejnou síť |

### 6.3 `Npgsql.EnableLegacyTimestampBehavior` a serverová `timezone`

Viz [varovný odstavec níže](#varování-jakýkoli-nový-host-musí-nastavit-dvě-věci).

### 6.4 Connection pooling — vlastní server má výrazně nižší strop

Vlastní PostgreSQL má default `max_connections = 100`, což je řádově méně než
Azure Flexible Server. Fakvio drží **jeden data source na tenant schéma**
(`INpgsqlDataSourceFactory.GetForSchema`), takže celková potřeba roste zhruba jako:

```
hosty × instance × (Root Maximum Pool Size + počet aktivních tenantů × 4)
```

`4` je `DatabaseOptions.SchemaDataSourceMaxPoolSize`
(`Fakvio.Infrastructure/Data/DatabaseOptions.cs:86`), konfigurovatelné klíčem
`Database:SchemaDataSourceMaxPoolSize`. Protože `MinPoolSize` je 0, je to **strop**,
ne ustálený stav — nečinný tenant spojení nedrží. Vzorec tedy počítá špičku.

Projeví se to tiše — pod nízkým provozem nic, pod špičkou náhlé
`connection pool exhausted` nebo `too many clients already`.

**Prevence:**
- explicitní `Maximum Pool Size` v connection stringu (viz část 7),
- zvednout `max_connections` v `postgresql.conf`,
- při více hostech / instancích nasadit **PgBouncer** v transaction pooling režimu.

### 6.5 Rozbitá migrační historie ⇒ re-aplikace migrací na plná data

Popsáno v [části 3](#3-ověření-musí-projít-dřív-než-na-novou-db-ukáže-jakákoli-aplikace).
Nejdražší způsob, jak zjistit, že se přeskočilo ověření.

### 6.6 Data Protection key ring

[Část 4](#4-data-protection-key-ring--make-or-break). Nejhorší z celého seznamu,
protože aplikace vypadá zdravě a spadne až na první IMAP/SMTP/AI operaci.

---

## 7. Připojovací řetězec pro vlastní hosting

**Jen proměnné prostředí. Do repozitáře se necommituje nic.**

```
Database__AuthMode=Password
UseAzureAdAuthentication=false
ConnectionStrings__DefaultConnection=Host=novy-db-server.example.cz;Port=5432;Database=fakvio;Username=fakvio;Password=***;Ssl Mode=Prefer;Timezone=UTC;Maximum Pool Size=40
```

Dvojité podtržítko `__` je oddělovač sekcí v .NET konfiguraci —
`Database__AuthMode` odpovídá klíči `Database:AuthMode`.

> `Ssl Mode=Prefer` v příkladu je **nejslabší** volba z tabulky v
> [části 6.2](#62-ssl-mode--npgsql-8-validuje-certifikát) — sedí na DB ve stejné
> privátní síti. Pokud spojení jde přes veřejnou síť, vyber z té tabulky výš.

### Varianta: databáze za Tailscale tunelem

Když databázový port **není ve veřejném internetu** a hostitel se k němu dostane jen přes
tailnet (tak je zapojené testovací prostředí, viz `Fakvio.Functions/Tailscale/README.md`),
liší se dvě věci: `Host`/`Port` míří na **lokální konec tunelu**, ne na databázový server,
a přibývá klíč s auth key. Zbytek zůstává stejný.

```
TAILSCALE_AUTHKEY=tskey-auth-…
Database__AuthMode=Password
UseAzureAdAuthentication=false
ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=15432;Database=fakvio_test;Username=fakvio;Password=***;Ssl Mode=Prefer;Timezone=UTC;Maximum Pool Size=20;Timeout=15
```

`Ssl Mode=Prefer` je tu navíc jediná praktická volba: provoz šifruje už WireGuard a
certifikát vystavený na `127.0.0.1` se ověřit nedá. `Timeout=15` proto, že první spojení
zahrnuje WireGuard handshake.

**`TAILSCALE_AUTHKEY` je spínač celé funkce** — když chybí, tunel se nepostaví a databáze
je nedostupná. Klíč má expiraci, takže platí to samé co pro hesla: patří do rotace.

### Konfigurační klíče — nový klíč stačí, legacy nesmí odporovat

`Database:AuthMode` je nový klíč; `UseAzureAdAuthentication` je **legacy bool**.
Od #138 už legacy klíč **není v žádném commitnutém config souboru** — v `appsettings.json`
je nově `Database:AuthMode`. Do konfigurace se tedy dostane jen tehdy, když ho někdo
explicitně nastaví (Azure App Settings, proměnná prostředí). Produkce ho v fázi 1 pořád
nese, proto pravidlo níž platí dál.

`DatabaseOptions.Resolve` (`Fakvio.Infrastructure/Data/DatabaseOptions.cs:161-199`)
řeší kombinace takto:

| `Database:AuthMode` | `UseAzureAdAuthentication` | Výsledek |
|---|---|---|
| `Password` | `false` | ✅ Password, `authModeSource = "Database:AuthMode"` |
| `AzureEntraId` | `true` | ✅ AzureEntraId, `authModeSource = "Database:AuthMode"` |
| `Password` | `true` (z App Settings) | ❌ **`InvalidOperationException` při startu** |
| `AzureEntraId` | `false` | ❌ **`InvalidOperationException` při startu** |
| nenastaveno | `true` / `false` | ✅ podle legacy, `authModeSource = "UseAzureAdAuthentication (legacy)"` |

Konfliktní kombinace hodí:

```
Conflicting database auth mode configuration: 'Database:AuthMode' = 'Password'
but legacy 'UseAzureAdAuthentication' = 'True' (AzureEntraId).
Delete the legacy 'UseAzureAdAuthentication' key once 'Database:AuthMode' is confirmed working.
```

To je **záměrný fail-fast** — brání tichému rozjetí konfigurace. Praktický důsledek:

- **při přepnutí na vlastní server nastav `Database__AuthMode=Password`**; pokud cílové
  prostředí legacy klíč ještě nese (produkce ano), nastav i `UseAzureAdAuthentication=false`,
  jinak start spadne na konfliktu,
- **při rollbacku vrať zpět obojí**, co jsi nastavil,
- **až bude nový klíč ověřený v provozu**, legacy klíč smaž z App Settings — z commitnutých
  configů ho odstranilo #138, takže od té chvíle stačí `Database:AuthMode`.

Kontrola, co aplikace skutečně vyhodnotila:

```bash
curl -s -H "Authorization: Bearer <sysadmin-jwt>"   https://<app>/api/diagnostic/health | jq '{authMode, authModeSource}'
```

### Varování: jakýkoli nový host musí nastavit dvě věci

Self-hosting aplikace je mimo scope tohoto dokumentu — ale pokud vznikne **nový
hostitelský proces** (systemd unit, container entrypoint, worker, vlastní CLI),
musí sám zajistit obojí:

**1. `Npgsql.EnableLegacyTimestampBehavior`.** Dnes je nastaven jen ve třech entry
pointech:

- `Fakvio.API/Program.cs:22`
- `Fakvio.Functions/Program.cs:41`
- `Fakvio.MigrationTool/Program.cs:32`

```csharp
// MUSÍ být před inicializací Npgsql type mappingu, tj. před prvním
// vytvořením NpgsqlDataSource / DbContextu — jinak se switch neprojeví.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
```

Bez něj začne každý `DateTime` s `Kind != Utc` házet
`Cannot write DateTime with Kind=Local to PostgreSQL type 'timestamp with time zone'`.

**2. Serverová `timezone = 'UTC'` v `postgresql.conf` _i_ `Timezone=UTC`
v connection stringu.** Obojí, ne jedno z toho — server řídí `now()` a
`CURRENT_DATE`, connection string řídí session daného spojení. Když se rozejdou,
data jsou uložená správně, ale reporty a filtry podle data vracejí posunuté
výsledky. Tiché a špatně dohledatelné.

---

## Související dokumentace

- `ADMINGUIDE.md` §9 — Data Protection, `credential-health`, 2FA z pohledu SysAdmina
- `DEVGUIDE.md` — přepínatelná DB autentizace (`Database:AuthMode`) a konfigurace
- `MIGRATION-PLAN-POSTGRESQL.md` — původní migrace SQLite → PostgreSQL (historie)
- `MULTITENANT.md` — schéma per tenant, provisioning
