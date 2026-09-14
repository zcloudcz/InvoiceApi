Pracuj jako profesionální .net vývojář (v angličtině), vše okomentuj tak, aby tomu rozumněl i absolutní .net junior.
Řešení by mělo být pokryté unit testy, případně integračními testy, aby bylo zajištěno, že všechny funkce fungují správně.
Zaměření je na výkon a efektivitu, zároveň by měl být kód čistý a dobře strukturovaný, aby byl snadno udržovatelný a rozšiřitelný v budoucnu.
Jde o produkční nasazení, takže je důležité dbát na bezpečnost, správu chyb a logování, aby bylo možné snadno identifikovat a řešit případné problémy v provozu.
Po každé interaci si ulož stav TODO do souboru TODO.md, abychom měli přehled o tom, co je ještě potřeba udělat a co již bylo dokončeno.

Implementační bloky:

UI musí být vícejazyčné. Můžou se použít resources.

UI komponenty, formuláře, nebo jejich části, které budou opakovaně použity, budou implementovány jako Blazor komponenty, což nám umožní snadno je znovu použít a udržovat konzistenci napříč celou aplikací. Jde například o comboboxy (pro enum jedno combo napříč aplikací, podle typu jedno combo - zákazník, šablona, apod.), 
ale i další vstupy mohou mít vlatní komponentu, pokud to dává smysl. Číslo účtu, datum, měna, atd. - to jsou všechny příklady vstupů, které by mohly mít vlastní komponentu pro zajištění konzistence a opětovné použitelnosti napříč aplikací.
Například faktury a Dobropisy musí mít stejné UI, aby bylo možné snadno přepínat mezi těmito dvěma typy dokumentů bez nutnosti měnit rozhraní.

Dále Grid jako takový musí být jedna komponenta s konzistentním chováním a ikonami.
Chování formulářů musí být také konzistentní, například pro ukládání změn, zobrazení chybových hlášení, potvrzení akce atd.

Pro šablony dokumentů faktur používáme Blazored.TextEditor (nuget, Quill WYSIWYG editor), který umožňuje vytvářet a upravovat HTML šablony přímo v Blazor UI.
Poznámka: BlazorHtmlEditor (nuget) je Monaco-based Razor code editor, NE WYSIWYG editor — proto používáme Blazored.TextEditor.
Tyto šablony budou uloženy v databázi a při generování faktury se do nich vloží konkrétní data (jako název firmy, adresa, položky faktury atd.) pomocí jednoduchých placeholderů.
Tento přístup nám umožní mít flexibilní a přizpůsobitelné šablony, které mohou být snadno aktualizovány bez nutnosti měnit kód backendu.

Pro export do PDF připravíme interface IPdfExport s implementací PdfExportService, který bude využívat knihovnu iTextSharp (nuget) pro generování PDF dokumentů z HTML šablon.

Dále připravíme službu pro emailing EmailService (IEmailService), která bude využívat SMTP klienta pro odesílání faktur přímo z aplikace.
Tato služba bude mít metodu SendInvoiceEmail, která přijme fakturu a emailovou adresu příjemce, vygeneruje PDF z HTML šablony a odešle email s přiloženým PDF dokumentem.
Dále bude i pro emaily potřeba šablona, kterou budeme moci upravovat v Blazor UI podobně jako u faktur.

Přehledy faktur a klientů budou implementovány pomocí MudBlazor komponent, které nám umožní rychle vytvořit moderní a responzivní uživatelské rozhraní.
Budeme potřebovat několik druhů přehledů, například:
- Přehled všech faktur s možností filtrování podle stavu, data, klienta atd.
- Přehled faktur pro konkrétního klienta, který zobrazí všechny faktury spojené s tímto klientem.
- Přehled klientů s možností zobrazení detailů a historie faktur pro každého klienta.


Pro mapování DTO a entit použijeme ZMapper (nuget).

Důležité je aby vývoj aplikace byl pokud možno komponentní a modulární, což nám umožní snadno přidávat nové funkce a upravovat stávající bez nutnosti zásahu do celého kódu.
Dále bude důležité zajistit, aby všechny komponenty byly dobře testovatelné a aby byly pokryty unit testy, které ověří správnost jejich funkcí.

## Background work pattern

Aplikace běží na jediném hostiteli **Fakvio.API** (ASP.NET Core na Azure App Service). Pravidelné úlohy jsou implementovány jako `BackgroundService` / `IHostedService` registrované v DI — žádné Azure Functions ani `[TimerTrigger]`.

### Pravidlo

Každá pravidelná úloha (poll, dunning, log flush, log cleanup, …) má dvoudílnou strukturu:

- **Stateless service** (např. `IImapPollService` v `Fakvio.Application.Service`) která
  obsahuje veškerou logiku jednoho cyklu. Žádný stav, žádné `Thread.Sleep` — jen "udělej
  jednu iteraci a vrať se".
- **Tenká `BackgroundService` obálka** v `Fakvio.Infrastructure` (např. `ImapPollWorker`)
  která ten servis volá v `while (!ct.IsCancellationRequested)` smyčce a registruje se
  přes `AddHostedService<TWorker>` v `Program.cs`.

Pro vzájemné vyloučení napříč instancemi (App Service replicas) použij **PostgreSQL advisory lock** (`AdvisoryLock.TryAcquireAsync`). Žádný Blob lease, žádný Redis — databáze už je k dispozici a lock je session-bound (crash-safe).

App Service má **Always On** povoleno — bez něj by idle recycle ukončil BackgroundService.

### Existující pracovníci
- `LogFlushService` (BackgroundService) — vykládá buffered logy do DB každých 20 sekund.
- `LogCleanupService` (BackgroundService) — každou hodinu maže Debug/Info logy starší 48 h.
- `ImapPollWorker` (BackgroundService) — čte emaily IMAP v intervalu `PollIntervalMinutes` (default 30 min); advisory lock.
- `ReminderWorker` (BackgroundService) — dunning denně v 06:00 UTC, per-tenant scope, jedno selhání ostatní nezastaví; advisory lock.

## Dokumentace — povinná údržba

Repozitář má tři průvodce, které musí zůstat synchronizované s kódem, a jeden
záznam změn, který se plní automaticky:

- **`DEVGUIDE.md`** — pro vývojáře a AI agenty. Aktualizuj při každé technické změně (nový pattern, nový provider, nový endpoint kategorie…). Viz §13 v DEVGUIDE pro kompletní seznam povinných případů.
- **`USERGUIDE.md`** — pro uživatele (tenant firmy). **Aktualizuj při každé změně viditelné uživateli**: nová stránka, nová akce, nový stav, nový export, změna chování formuláře.
- **`ADMINGUIDE.md`** — pro SysAdmina. **Aktualizuj při každé změně viditelné SysAdminovi**: nové nastavení, nový provider, nová správa tenantů, změna bezpečnostní konfigurace.
- **`release-notes.md`** — záznam dokončených změn, jeden řádek na každý task mergnutý do `develop`. **Nepíše ho vývojář ani reviewer, ale `agent-ops` při mergi** (viz `~/.claude/agents/agent-ops.md` Step 2a) — je to jediná sekvenční role, takže jako jediná může připisovat do sdíleného souboru bez konfliktu s paralelními větvemi. Ručně do něj nezasahuj; chybějící záznam znamená chybějící merge.

PR bez odpovídající aktualizace průvodce (pokud se změna týká jeho obsahu) **neprochází review**.

## AgenticTeam workflow

Tento repozitář používá AgenticTeam (Story → Task → Dev → Review → Test → Ops) řízený přes
GitHub Project v2 board. Detaily a přechody stavů viz `.claude/BOARD-OPS.md`.

### Promoční stupně (repo-specifické — globální role je neznají)

Role se berou z `~/.claude/agents/` a počítají se dvěma stupni `develop → master`. Tady jsou
tři a toto pravidlo má přednost:

- `develop` — integrační větev. Jediný cíl feature PR a jediná větev, kam merguje `agent-ops`.
- `TEST-ENV` — staging. Plní ho jen `/release` (develop → TEST-ENV); ten karty nepřesouvá.
- `master` — produkce. Plní ho jen `/release-prod` (TEST-ENV → master); jen ten přesouvá karty
  z `Implemented` do `Approved`.
- Žádná role (dev, tester, ops, warden) nepushuje ani nemerguje do `TEST-ENV` ani `master`.
  Kde globální role píše „run /release to ship to master", platí „/release → TEST-ENV,
  pak /release-prod → master".
