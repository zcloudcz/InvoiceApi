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

## API + Functions duplication (background work pattern)

Aplikace má dva paralelní hostovací modely, které musí dělat to samé:

1. **Fakvio.API** — klasický ASP.NET Core host. Podporuje `BackgroundService` / `IHostedService`,
   takže pravidelné úkoly tam jdou vyřešit přes `AddHostedService<TWorker>`. Používá se pro
   lokální vývoj a klasický (VM / App Service) deploy.

2. **Fakvio.Functions** — Azure Functions Isolated Worker. **NEpodporuje** `BackgroundService`
   spolehlivě (na consumption planu se škáluje na nulu, hosted service by neběžel mezi
   invokacemi). Pravidelné úkoly tam musí být řešené přes `[TimerTrigger]` Function.

### Pravidlo

Každá pravidelná úloha (poll, dunning, log flush, log cleanup, …) **musí mít obě varianty**:

- **Stateless service** (např. `IImapPollService` v `Fakvio.Application.Service`) která
  obsahuje veškerou logiku jednoho cyklu. Žádný stav, žádné `Thread.Sleep` — jen "udělej
  jednu iteraci a vrať se".
- **Tenká `BackgroundService` obálka** v `Fakvio.Infrastructure` (např. `ImapPollWorker`)
  která ten servis volá v `while (!ct.IsCancellationRequested)` smyčce. Použije se
  v API hostu.
- **Tenká `[TimerTrigger]` Function** v `Fakvio.Functions` (např. `PaymentMatchingFunctions.RunImapPoll`)
  která ten samý servis volá. Použije se v Azure Functions deployi.

### Důsledky

- Logika cyklu žije **jen na jednom místě** (ten stateless service). Worker i Function jsou
  jen drivery a mají &lt; 50 řádků kódu.
- Cron v Function je obvykle **kratší než žádaný interval** (např. tick každých 5 minut
  pro úlohu která má běžet každých 30) a service uvnitř kontroluje `LastRunAt` aby
  brzy-spuštěné cykly přeskočil. Tím získáme dynamický interval řízený z DB i v Functions,
  kde CRON není dynamicky měnitelný bez redeploye.
- Pro vzájemné vyloučení napříč hostiteli (replicas, paralelní API + Function deploy)
  použij **PostgreSQL advisory lock** (`AdvisoryLock.TryAcquireAsync`). Žádný Blob lease,
  žádný Redis — databáze už je k dispozici a lock je session-bound (crash-safe).
- Pokud přidáváš novou úlohu, vždy přidej **obě** strany. Když chybí Functions varianta,
  Azure deploy úlohu prostě neběží a ticha. Když chybí worker varianta, lokální vývoj
  v `dotnet run` nikdy nevidí cyklus pracovat. Obě je třeba mít, jinak hrozí "u mě to jede".

### Existující dvojice
- Logging: `LogFlushService`/`LogCleanupService` (Infrastructure) ↔ `TimerFunctions.FlushLogs/CleanupLogs`.
- Reminders: dunning `IReminderService.ProcessOverdueInvoicesAsync` ↔ `ReminderFunctions.ProcessReminders`.
- Payment matching: `ImapPollWorker` (BackgroundService) ↔ `PaymentMatchingFunctions.RunImapPoll`,
  oba volají `IImapPollService.RunCycleAsync`. SysAdmin "Run now" v UI volá totéž
  přes HTTP `POST /api/sysadmin/payment-matching/run-now`.

## AgenticTeam workflow

Tento repozitář používá AgenticTeam (Story → Task → Dev → Review → Test → Ops) řízený přes
GitHub Project v2 board. Detaily a přechody stavů viz `.claude/BOARD-OPS.md`.
