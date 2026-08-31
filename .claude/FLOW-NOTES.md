# Poznatky z provozu AgenticTeam

Živý dokument. Zapisuj sem, co běh flow **stálo čas** nebo **málem prošlo**, ne to,
co proběhlo hladce. Každý bod má mít důkaz z konkrétního běhu, ne dojem.
Co se osvědčí, patří zpátky do šablony `../AgenticTeam/`.

---

## Běh 2026-08-23 — 22 mergů do `develop`, ~45 dispatchů

Dvě témata paralelně (`db-switch`, `ai-first`) + `focus:override` výjimky.
Pět stories zanalyzováno a rozpadnuto na 39 tasků.

### 1. GraphQL kvóta je skutečné úzké hrdlo, ne šum

GitHub dává 5000 GraphQL bodů/hod na účet. **Projects v2 nemá REST alternativu**,
takže každý přesun karty je GraphQL. REST (issues, PRs, labely, komentáře) má vlastní
kvótu a ta zůstala prakticky nedotčená.

Při ~10 agentech v letu se kvóta vyčerpala opakovaně, pokaždé na ~20-40 minut.
Následek: karta zůstane ve špatném sloupci, zatímco kód je hotový a pushnutý.

**Co pomohlo — měřitelně:**
- Předávat agentovi v dispatchi **hotová ID** (`item`, `project`, `field`, `option`),
  ať nemusí volat `gh project item-list` / `field-list`. Ty lookupy jsou to drahé,
  ne samotná mutace. Jedna mutace s ID ≈ 1-3 body, `item-list --limit 200` ≈ desítky.
- Dávkovat přesuny do **jedné mutace s aliasy** (`m1: updateProjectV2ItemFieldValue…
  m2: … m3: …`). Devět karet za jedno volání.
- Instruovat: „zkus jednou, na fail zapiš zamýšlený přechod do reportu, neretry-loopuj."
  Orchestrátor pak srovná board jedním batchem.

**Zbývá vyřešit:** agenti končící s nedokončeným board přesunem vytvářejí nesoulad
label × sloupec. Dnes to třikrát srovnával orchestrátor ručně. Kandidát: nechat
`/tick-warden` běžet automaticky po každém resetu kvóty.

### 1b. Otevřená architektonická otázka: musí board vůbec žít v Projects v2?

Bod 1 popisuje symptom. Pod ním je návrhová otázka, kterou stojí za to rozhodnout
vědomě, ne ji každý běh znovu obcházet.

**Asymetrie, ze které to celé plyne:** drahá jsou *čtení* (`item-list`,
`field-list` = desítky bodů), zápisy jsou levné (jednotky). REST kvóta přitom
zůstala celý den prakticky nedotčená (~4900/5000), zatímco GraphQL jsme vyčerpali
opakovaně. A `gh issue edit` / `gh pr ready` / `gh pr comment` jdou přes GraphQL,
kdežto `gh api repos/...` dělá totéž přes REST.

**Agenti navíc board čtou zbytečně** — kartu jim přiděluje orchestrátor v promptu,
oni si ji pak dohledávají jen kvůli ID.

Varianty, od nejlevnější:

1. **Předávat ID v dispatchi** (`item`/`project`/`field`/`option`). Ověřeno v druhé
   půlce dne, okamžitý efekt, nulová infrastruktura. — *udělat vždy*
2. **Snapshot boardu vlastněný orchestrátorem.** Jedno čtení na pass do lokálního
   JSON, agenti čtou soubor. Staleness nevadí, kartu stejně přiděluje orchestrátor.
3. **Dávkové zápisy** jednou aliasovanou mutací na fázi. Pozor: potřebuje durable
   frontu, jinak smrt běhu zahodí nedokončené přesuny.
4. **Status jako label místo Projects v2.** Flow potřebuje z boardu jedinou
   informaci — ve kterém sloupci karta je. To umí label, a labely jdou přes REST,
   tedy do nevyužité kvóty. Projects v2 zůstane **read-only zrcadlo pro člověka**,
   synchronizované jednou na konci drainu.
5. Vlastní board (soubor/SQLite v repu).

**ROZHODNUTO 2026-08-23 (owner): jde se variantou 4.** Status bude label,
Projects v2 zůstane read-only zrcadlo pro člověka. Zadáno jako story **#288**
(`focus:override`, aby ji board vůbec vytáhl — viz #287). Body 1-3 mezitím
platí a jsou zakódované v `BOARD-OPS.md` sekci „Budget: GraphQL is the scarce
resource" a v `AGENT-RULES.md` §5b; po dokončení #288 část z nich zanikne.

**Variantu 5 nestavět** — ne kvůli složitosti, ale protože varianta 4 dá totéž bez
jediného řádku nové infrastruktury, a vlastní stav by přinesl přesně ten problém,
který máme s MEMORY.md (bod 8b): sdílený zapisovatelný stav bez zámku.

**Podmínka pro každou variantu:** člověk musí board dál vidět. Řešení, které vezme
vizuální přehled, je horší než ta kvóta.

### 2. Ops se dávkuje — opraveno během běhu

Původně jeden dispatch = jeden merge. Merge **musí** být sekvenční (každý posune
`develop` a mění merge base dalšího PR), ale to je omezení paralelismu, ne průchodnosti.

Změněno v `.claude/agents/agent-ops.md` (+ `.codex` mirror, + `ticks.md`): agent si
sám dohledá další `role:ops` a projede frontu v jednom běhu, pro každý PR znovu od
Step 0 proti develop, který sám právě vyrobil. Kickback jednoho PR nezastaví zbytek.

První dávkový běh: **3 merge + 1 správný kickback** v jednom dispatchi.

### 3. Zastaralá `origin/develop` ref — tři falešné poplachy za den

Tři agenti nezávisle ohlásili „develop se nekompiluje". Ve dvou případech byl už
dávno opravený; jeden agent kvůli tomu poslal do cizího souboru **BOM** a stálo to
celé review kolo navíc.

**Zavedeno:** `git fetch` před jakoukoli diagnózou integrační větve — v MEMORY.md
sekci „Známé pasti". Patří i do `agent-dev.md` a `agent-reviewer.md`.

### 4. `dotnet test` vrací exit 0 i při chybě kompilace

Objevil tester #275: běh proti nekompilující hlavě vypsal `error CS1503` a **skončil
s exit code 0**. Tři měření dne tak reportovala falešnou zelenou.

Celý flow staví verdikty na číslech ze suity. Tohle je díra v samotné bráně.
Issue **#282** — build kontrolovat zvlášť, ne věřit exit code.

### 5. Dvě zelené PR = rozbitý develop (a trial merge to nechytí)

`#267` přidal **volitelný parametr doprostřed signatury** `GetReportAsync`.
`#265` se větvilo předtím a neslo mocky na starý podpis. Textově nekolidovaly,
oba trial merge byly zelené — rozbití vzniklo až kombinací.

Trial merge měří proti `develop` **před** tím druhým PR, takže tuhle třídu principiálně
neodhalí. Táž třída se opakovala u `#260 × #270` (ctor 3→4 parametry, 6 volání)
a tam navíc **git nenahlásil ani konflikt**, protože osmý rozbitý soubor byl *nový*
soubor z #270, který se mergnul čistě.

**Návrh k prozkoumání:** před mergem N-tého PR udělat trial merge proti *ostatním
otevřeným PR s role:ops/role:tester*, ne jen proti develop. Levnější varianta:
při změně veřejné signatury povinně grepnout volající napříč **otevřenými větvemi**,
ne jen v develop.

### 6. Referenční implementace musí dozrát dřív než sourozenci

`#217` byl určen jako referenční implementace confirm vzoru pro 7 doménových tasků.
Pustil jsem `#218` paralelně — a rozešel se v tom, co patří za `confirm`
(gate jen na delete vs. na všechny zápisy). Výsledek: kickback a přepracování.

**Zavedeno:** zbylých 5 tasků drženo, dokud reference neprojde review.
Obecně: **task označený jako reference se nesmí paralelizovat se svými kopiemi.**

### 6b. Slepá skvrna se vrací na stejný řádek

`ChatService` má **dvě** call sites pro tool-result framing: nestreamovanou a native
**streaming Path A**. Ta druhá byla slepým místem u #212 kolo 1 (reviewer ji našel:
„framing nedojde ke všem providerům"). Dev ji opravil.

O dvě PR později, u #217, přežila mutace na `ChatService.cs:311` — **znovu streaming
Path A**, znovu nepokrytá. Dev reportoval „11/11 mutací zabito", což platilo jen na
jednom ze dvou call sites.

Poučení není „ten dev chyboval", ale: **kde má kód dvě symetrické cesty a jedna se
těžko testuje, ta druhá bude chybět opakovaně, dokud ji něco nedrží strukturálně.**
Default provider je Claude a ten streamuje, takže je to zároveň ta produkční cesta.

Návrh: u symetrických dvojic (streaming × nestreaming, API host × Functions host,
singleton × AdHoc provider) žádat v dispatchi explicitně **mutaci na obou větvích**,
ne jen počet zabitých mutací.

### 6a. Mutační testování umí měřit zastaralou binárku — a nikdo to nepozná

**Nejzávažnější metodický nález dne.** Mutační ověřování je v tomhle flow hlavní
důkaz, že test není vata. Jenže:

Dev u #217 vracel zmutovaný soubor přes `mv` ze zálohy. Obnovený soubor tím dostal
**starší mtime než `obj/`**, MSBuild usoudil, že není co překládat, a „čistý" běh
po mutaci měřil **binárku z předchozího buildu**. Výsledek: reportoval „11/11 mutací
zabito", zatímco reviewer tutéž mutaci na `ChatService.cs:311` nechal přežít.

Nešlo o nedbalost — harness vypadal správně a čísla byla konzistentní sama se sebou.
Právě to je nebezpečné: **falešná zelená v důkazním nástroji**, ne v testu.

Souvisí s #282 (`dotnet test` exit 0 při chybě kompilace) — dvě nezávislé cesty,
jak dostat důvěryhodně vypadající číslo z běhu, který neměřil, co si myslíte.

**Pravidla, která z toho plynou:**
- Mutaci vracet přes `git checkout -- <file>`, ne `mv`/`cp` ze zálohy.
- Po restore `touch` na soubor, nebo build s vynuceným rebuildem.
- Strom před finálním měřením ověřit `git diff` (prázdný = opravdu čistý).
- Když reviewer a dev nesouhlasí v počtu zabitých mutací, **první podezřelý je
  build cache, ne úsudek jednoho z nich.**

### 6c. Počítadla v dokumentaci jsou konfliktní magnet

Paritní tabulka chat ↔ MCP nese součty („14 toolů, 13 pokryto, 23 mezer"). Při
paralelním drainu na ni sáhlo pět PR a **každé napočítalo jiné číslo** — správně,
protože mezitím dosedaly další tooly. Důsledky za jeden den:
- #258 zapsalo 36/11, o hodinu později nepravda;
- #259 při mergi přepočítalo a našlo, že se překlápí 6 řádků, ne 3;
- #273 napočítalo 37/15 místo zadaných 37/12;
- jeden dev číslo z komentářů raději smazal, jiný ho vrátil zpátky.

Není to nedbalost, je to **strukturální vada dokumentu**: absolutní počet v textu
má jediný správný okamžik — commit, kdy se píše.

**Návrh:** v dokumentaci držet jen tabulku řádků (ta se auto-merguje aditivně)
a počty buď vypustit, nebo je generovat z testu
(`ExpectedRealToolCatalogLines.Length` už tu hodnotu zná). Konvence „kdo merguje
druhý, přepočítá" funguje, ale stojí kolo navíc pokaždé.

### 6d. Kde se pálí tokeny (otázka od ownera, s čísly z běhu)

Řádově ~50 dispatchů. Konkrétní místa, seřazená podle velikosti:

1. **MEMORY.md — největší položka, opravená během běhu.** Při 468 KB `Read`
   ořízne na ~25k tokenů, ale to platí **každý dispatch**: ≈1,2M tokenů,
   zhruba pětina běhu, za opakované čtení pracovní paměti. Po prořezání na
   4,6 KB je to ~1k. Viz bod 8 — prořezávat, jakmile přeroste ~15 KB.
2. **Trojí měření téhož baseline.** Na jeden PR běží full suite 6×: dev
   (baseline+merged), reviewer (totéž), tester (totéž) — a reviewer s testerem
   často měří **stejný develop SHA**. Návrh: `.claude/baseline-cache.json`
   klíčovaný SHA (`{"bb1ca70": {"unit":"2624/0/4","integration":"105/21/3"}}`).
   Netrefí-li SHA, měří se normálně. Ušetří ~1 full běh na agenta.
3. **Mutační sweep přes celou suitu.** 12 mutantů × 2600 testů, když cílený
   `--filter` na dotčené třídy dá stejný důkaz. Někteří to dělají, není to pravidlo.
4. **Dispatch prompty orchestrátora.** Ke konci 600-900 slov s opakovanou
   boilerplate (flake rodiny, kvóta, #282, board ID) ≈ 1,5-2k × 40 = ~70k.
   Po přesunu pastí do MEMORY.md lze zkrátit na polovinu.
5. **Codex — chybí data, ne verdikt.** ~40k tokenů na delegaci. Většina jeho
   „blocking" nálezů dnes v triáži neobstála (#277 oba zamítnuty, #246 dva,
   #256 dva). **Ale nejcennější nález dne — „invariant má troje dveře" u #280 —
   je jeho.** Proto ne „vypnout", ale **sledovat poměr přeživších nálezů**;
   dnes to nikdo neměří, takže se o tom nedá rozhodnout.

Body 1 a 4 hotové. Bod 2 má nejlepší poměr přínos/práce (jeden JSON).
Bod 5 není úspora, je to podklad pro příští rozhodnutí.

### 7. Session limit — 7 agentů zemřelo naráz

Účet narazil na session limit, sedm agentů skončilo v půlce. Obnova byla čistá,
protože **remote byl autoritativní** — žádný agent nenechal half-pushed stav;
nejhorší ztráta byl rozdělaný lokální worktree.

Potvrzuje to zásadu: agent nesmí považovat lokální rozpracovanost za stav.
Push nebo nic.

### 8. MEMORY.md narostl na 468 KB

Každý agent ho čte na startu. Při ~45 dispatchích to je enormní plýtvání kontextem —
a soubor CLAUDE.md ho definuje jako *kompaktní pracovní paměť*.

Prořezáno na **4,6 KB** (100×), historie do `MEMORY-archive-2026-08-23.md`.
Nová podoba nese jen: stav témat, co čeká na člověka, **známé pasti prostředí**
a board ID. Ta sekce s pastmi se okamžitě vyplatila — agenti přestali honit
známé flaky a hlásit falešné poplachy.

**Doporučení:** prořezávat, jakmile soubor přeroste ~15 KB. Řádek na roli a kolo,
ne odstavec.

### 9. Self-PR ruší eskalační žebřík

Na single-account repu GitHub nedovolí formální `APPROVE` ani `REQUEST_CHANGES`
na vlastní PR. Konvence markeru na první řádce (`AgentReviewer verdict: …`) funguje,
ale `KICKBACK_COUNT` počítaný z `state=="CHANGES_REQUESTED"` **vrací navždy 0**,
takže `quality:recurring` ladder nikdy nenaskočí. Issue **#264**.

### 8b. MEMORY.md je sdílený zapisovatelný soubor bez zámku

Každá role do něj na konci připisuje. Při 6-10 agentech v letu si zápisy lezou
do cesty: reviewer #278 hlásí, že jeho edit rozsekl větu, kterou právě psal
paralelní agent, a reviewer #273 totéž pozoroval z druhé strany (svou řádku slil
v pořádku, ale viděl cizí vsuvku uprostřed věty).

Zatím to nezpůsobilo ztrátu informace, jen poškozený text — ale je to shoda
náhod, ne vlastnost návrhu. `Edit` na sdílený soubor bez zámku prostě nemá
konfliktní detekci, kterou má git.

**Nezavírat to jako „psát opatrněji".** Kandidáti na skutečné řešení:
- role zapisuje do vlastního souboru (`MEMORY.d/<role>-<issue>.md`), orchestrátor
  slévá — git-friendly, žádný zámek;
- nebo append-only log (každý zápis nový řádek na konec, nikdy edit uprostřed);
- nebo zápis do MEMORY.md výhradně orchestrátorem z reportů agentů.

Souvisí: soubor dnes narostl na 468 KB právě proto, že do něj deset rolí sypalo
odstavce (bod 8).

### 9b. Komentář vs. formální review — dvě různá API

Orchestrátor předává ops „approval marker, komentář `<id>`". Jenže ten ID může být
buď **issue comment** (`repos/:o/:r/issues/<pr>/comments`) nebo **formální PR review**
(`pulls/<pr>/reviews`). Jsou to oddělené kolekce a jedna druhou nevidí.

U #260 ops hledal marker v issue komentářích, nenašel ho, a musel dojít na
`get_reviews`. Zdrželo to a v horším případě by to vypadalo jako chybějící approval.

**Návrh:** v dispatchi psát typ, ne jen číslo („formal review 5002929784" ×
„issue comment 5387410310"), a v `agent-ops.md` gate hledat marker v **obou**
kolekcích, ne v jedné.

### 10. Úklid worktreeů nikdo nevlastní

Za den se v `C:\TEMP\agentic-worktrees\` nahromadilo ~70 osiřelých stromů; některé
drží zámek běžící `dotnet` proces, takže je agent ani nemůže smazat. Hlásili to
čtyři agenti, žádný nemá mandát to řešit. Kandidát na rozšíření `agent-warden`.

---

## Co fungovalo a stojí za udržení

- **Mutační ověřování testů.** Opakovaně odhalilo, že test je vata: placebo kulturní
  test, který chytne rozdíl až pod `th-TH`; „ekvivalentní mutant" u `ToUtc` pod UTC;
  komentář slibující záruku, kterou kód nedává (tvrdší mutant přežil všech 16 testů).
  Bez mutací by všechny tři prošly jako pokrytí.
- **Reviewer měří sám, nepřebírá čísla dev reportu.** Několikrát tím padlo nepřesné
  tvrzení („8 smyček → 1" byly ve skutečnosti 4; „+21 testů" proti posunutému baseline).
- **Předat agentovi inventář známých flaků v dispatchi.** Přestali je honit a začali
  jen dokládat shodnost na obou stromech.
- **Rozhodnutí orchestrátora podat i s odůvodněním a výzvou „argumentuj proti".**
  U pravidla 7 reviewer nezávisle potvrdil a přidal tři silnější argumenty, než jsem
  měl já. Kdybych poslal jen verdikt, dostal bych poslušnost místo kontroly.
- **Ops jako jediná sekvenční role.** Merge konflikty se objevují až po předchozím
  mergi; kdyby běžely paralelně, řešily by se pořád dokola.

---

## Běh 2026-08-23 — retro (první běh role `agent-retro`)

Nezávislé čtení téhož dne **jen z artefaktů**: 22 mergů (`eef3fb8` 11:15 → `bb1ca70`
19:31), PR #244–#286, 23 zavřených tasků, 5 stories rozpadnutých v dávce 09:42–09:44.
Sekce výše psal orchestrátor během běhu; tahle ji nenahrazuje. Číslování `R*`, aby se
to nepletlo, a kde s ní nesouhlasím, je to řečeno nahlas.

### R1. #179 to celé popsal už 2026-08-20 — a tři dny se nestalo nic

**Kategorie: zapsáno dřív a stejně se to opakovalo.** Issue **#179** (20. 8.)
diagnostikovalo mrtvé brány na single-account repu a předepsalo tři konkrétní opravy
v `agent-reviewer.md` / `BOARD-OPS.md` / `agent-ops.md`. Definice rolí se do 23. 8.
nezměnily (mtime na `.claude/agents/`: vše 19. 8., pak až `agent-ops.md` v 18:28 kvůli
dávkování mergů). Výsledek za jeden den:

- `MEMORY.md` nese třikrát `KICKBACK_COUNT=0` (#284, #280, #281 kolo 1) — hodnotu,
  kterou ten dotaz na tomhle repu **nemůže vrátit jinak**; u #280 kolo 2 pak reviewer
  napsal „KICKBACK_COUNT zůstává 1", tedy číslo, které si musel pamatovat mimo systém;
- markery skončily ve **dvou kolekcích**: formální review u #185/#188/#244/#256/#258/
  #259/#273/#277/#280/#284 (10×), issue komentář u #246/#260/#278/#281 (4×);
- a založil se **#264**, duplikát #179, protože #179 nikdo neviděl.

**Kořen není v té opravě, ale v tom, že se k ní nikdo nedostane.** #179, #180, #264
i #282 mají `area:quality` a **žádné `theme:*` ani `focus:override`**, takže je podle
`BOARD-OPS.md` §Focus / Eligibility `/tick*` nikdy nevytáhne — a agent si výjimku
udělit nesmí (správně). Procesní vada je tím strukturálně neopravitelná tímhle flow.
Jediná role, která ji umí spotřebovat, do dneška neexistovala. → issue **#287**.

### R2. Brána na CI je prázdná množina — a to u všech 22 mergů

**V sekci výše to není a je to nejzávažnější nález dne.** `agent-ops` Step 0 vyžaduje,
aby „každá položka `statusCheckRollup[]` měla `conclusion` ∈ {SUCCESS, NEUTRAL,
SKIPPED}". Na tomhle repu je to pole **prázdné** (`MEMORY.md`: „CI na PR neexistuje"),
takže je ta podmínka *vakuózně pravdivá* a projde vždy. `agent-tester` Step 2 má tentýž
problém: `gh pr checks --watch` nevrátí nic a návod k tomu říká „nepředpokládej, že
lokální zelená znamená vzdálenou" — tady je to obráceně, lokální je jediná, co existuje.

`agent-ops` přitom **nespouštěl žádný build ani testy**. Sečteno: v okamžiku mergu byla
testovací brána doložená jedním labelem a jedním sloupcem na boardu — dvěma nejméně
spolehlivými artefakty v systému (labely přepisuje i pickup a warden, board padá na
kvótě). Není to hypotéza; potvrdilo se to v R3 a R4.

### R3. `develop` se 16 minut nepřekládal a jeden PR se do něj mezitím mergnul

`640f7c6` (#265) v 18:01 rozbil překlad `Fakvio.Tests.Unit`; **`b91a94c` (#258) se
mergnul v 18:12** — do větve, která se nepřekládá, tedy s důkazem změřeným proti
něčemu, co v tu chvíli neexistovalo. Opraveno až `7d685e7` v 18:17.

**Tady nesouhlasím s bodem 5 výše.** Ten to podává jako třídu, kterou trial merge
„principiálně neodhalí", a navrhuje drahé obcházení (trial merge proti ostatním
otevřeným PR, nebo grep volajících napříč větvemi). Levnější a spolehlivější řešení
napsal do #276 ten, kdo to našel: *„klasický případ, který `agent-ops` chytí jen tím,
že po mergi znovu buildne."* Ops je jediná sekvenční role a už teď běží ve smyčce —
stojí to jeden build na merge, ne kombinatoriku přes otevřené PR. Zavedeno jako
`agent-ops` Step 1c.

### R4. Když flow narazí na vlastní zaparkované pravidlo, obejde se

Následek R3 ve třech krocích, každý mimo proces:

1. #276 (P1, rozbitý `develop` pro všechny) bylo založené **bez `theme:*` i bez
   `focus:override`** — v těle je to i napsané: „orchestrátor ho sám nevytáhne, přidej
   prosím jedno z toho". Tedy R1 znovu, tentokrát na P1 blokujícím celý repozitář.
2. Opravilo se to přímým commitem do `develop` (`7d685e7`) — bez PR, bez review,
   bez řádky v `release-notes.md`.
3. Totéž potkalo `997e9ba` (přepočet paritní tabulky v DEVGUIDE) o osm minut později.

`AGENT-RULES` §1 přitom přímé commity do integrační větve zakazuje. Zákaz byl ale
nepodmíněný, zatímco `agent-ops` Step 2a přímý commit **přikazuje** (release-notes) —
a ta nesrovnalost dala krytí i těm dvěma ostatním. Výjimka je teď v §1 pojmenovaná
jmenovitě a uzavřená.

### R5. `release-notes.md` chybí #152 — 1 z 23

Každý merge dne má následný `chore(release-notes): record #N`, kromě **#152 / PR #175**
(11:15). `CLAUDE.md` říká „chybějící záznam znamená chybějící merge", takže tichá díra
nekazí jen dokumentaci, ale i to jediné čtení, podle kterého se dá zpětně říct, co
vyjelo. Jedna instance, ale nekontroluje to nic ⇒ mechanismus se opakuje.

### R6. Co v záznamu **není** — a mělo by být

Tohle je pro šablonu cennější než kterákoli oprava výše. Závěry, ke kterým jsem se
propracoval, ale **artefakty je neunesou**:

1. **Neexistuje stopa po dispatchi.** Bod 6d výše počítá tokeny na „~50 dispatchů" —
   v artefaktech není ani jeden. Naplánovaný tick log
   (`%LOCALAPPDATA%\AgenticTeam\C__GIT_ZCLOUD_InvoiceApi.log`) má poslední zápis
   **2026-04-27**, takže celý den běžel interaktivně bez logu. Všechna čísla v 6d jsou
   pro pozdějšího čtenáře neověřitelná — škoda, protože jsou to nejzajímavější čísla
   v celém dokumentu.
2. **U #244 se nedá zjistit, jestli testovací brána vůbec proběhla.** Label `role:tester`
   držel 10:43→10:53, pak přeskočil na `role:ops` — a na PR **není jediný komentář ani
   review od testera**. Podle `agent-tester.md` to bylo *korektní chování*: PASS neměl
   předepsaný žádný artefakt. (#245 komentář má, ale jako prózu bez markeru.)
3. **Nedá se určit, kdo udělal ty dva přímé commity** (R4). Všechny commity dne mají
   autora `Martin Zahálka`; jediný, který se hlásí jako `agent-ops`, je `a67f89b`.
   Git identita není per-role, takže „agent to obešel" × „majitel to spravil ručně"
   jsou z historie nerozlišitelné.
4. **Přínos Codexu (bod 6d/5) se z artefaktů spočítat nedá.** Jeho nálezy jsou vidět jen
   tam, kde je reviewer zmínil v próze. Poměr přeživších nálezů, o kterém 6d mluví, nemá
   zdroj dat — bez změny v tom, co reviewer zapisuje, ho nepůjde změřit ani příště.
5. **`MEMORY.md` je gitignorovaný a `FLOW-NOTES.md` netrackovaný.** Kolo po kole jsem
   rekonstruoval hlavně z `MEMORY.md` — dokumentu, který **není v gitu**
   (`.gitignore:370`) a který se týž den ořízl z 468 KB na 4,6 KB. Cokoli, co role
   nezapsala i do komentáře na PR, tím zmizelo. Sám tenhle dokument je na tom stejně
   (`?? .claude/FLOW-NOTES.md`).

### R7. Polovina procesních souborů není na `develop`

`.claude/AGENT-RULES.md`, `.claude/agents/agent-warden.md`, celý `.codex/` a pět příkazů
(`tick-devs`, `tick-tests`, `tick-warden`, `new-story`, `token-stats`) **na
`origin/develop` neexistují**. Žijí jen ve větvi `chore/agentic-team-resync`, odštěpené
`a88e6e4` (20. 8.) a dodnes nemergnuté. Ověřeno i na čtyřech dnešních feature větvích
(#215, #139, #218, #217) — `AGENT-RULES.md` není v žádné z nich.

Devové a testeři přitom pracují ve worktree nad feature větví a `agent-tester.md`
Step 0.2 jim říká „přečti `.claude/AGENT-RULES.md` §9 a §10".

**Co tenhle nález neunese:** že to dnes něco pokazilo. Step 0.2 běží *před* založením
worktree (Step 0.5), takže čtení nejspíš proběhlo v hlavním checkoutu, kde ty soubory
jsou. Stav je ale zjevně nechtěný a křehký — a hlavně: **mé dnešní úpravy
`AGENT-RULES.md` a `BOARD-OPS.md` sedí na nemergnuté větvi.** Dokud se
`chore/agentic-team-resync` nedostane do `develop`, je tenhle retro z poloviny neúčinný.

### Co fungovalo a stojí za udržení (doplněk k seznamu výše)

- **Reviewer si sáhne na cizí mutaci a přehraje ji.** U #211 / PR #273 reviewer sám
  přepočítal paritu na mergnutém stromě a **dal za pravdu devovi proti vlastnímu
  kickbacku** („37/12 z kickbacku bylo moje stará čísla"). Role, která umí odvolat
  vlastní nález, je dražší o jedno kolo a levnější na celý běh.
- **Odmítnutý reviewerův návrh se zapisuje i s důvodem.** #237 / PR #278: dev hoist
  `_saving` odmítl, reviewer to přijal s poznámkou „můj návrh z kola 1 byl neúplný,
  early return na `!_formValid` je mimo `try/finally`". Bez toho záznamu by příští
  reviewer navrhl totéž.
- **Triáž Codexových „blokujících" nálezů místo jejich automatického přebírání.** U #277
  oba zamítnuty s věcným argumentem (404 je YAGNI, 400-substring má obrácené znaménko)
  — a přesto je nejcennější nález dne (#280, „invariant má troje dveře") jeho. Filtr
  drží obojí směry.
- **Dev, který našel cizí regresi, ji nespravil potichu.** #276 vzniklo právě proto, že
  dev u #275 odmítl přibalit nesouvisející opravu. Správně — jen to pak nemělo kam jít
  (R4).

### Procesní změny tímhle retrem (5 z 5)

1. `.claude/agents/agent-reviewer.md` + `.codex` mirror — `KICKBACK_COUNT` z markeru,
   ne ze `state`; kickback jako `gh pr review --comment` s pevnou první řádkou;
   `--request-changes` z návodu pryč (na self-PR vrací 422). *Instance: R1.*
2. `.claude/BOARD-OPS.md` — „Counting tester kickbacks" → **„Verdict markers"**: všechny
   čtyři markery *včetně kolekce*, obě counter query na jednom místě. „Review gate on
   single-account repos" zkrácena, protože už jen odkazuje. *Instance: R1.*
3. `.claude/agents/agent-tester.md` + mirror — povinný marker `AgentTester verdict: PASS`
   s čísly a SHA; prázdný `statusCheckRollup` výslovně **není** zelená. Smazán výčet
   test frameworků pro osm jazyků (repo je .NET). *Instance: R2, R6/2.*
4. `.claude/agents/agent-ops.md` + mirror — nový **Step 1c: po každém mergi buildni
   integrační větev**, při selhání zastav frontu a eskaluj; Step 0 gate na oba markery
   a na prázdný rollup; Step 2a ověří, že řádka v `release-notes.md` opravdu dosedla.
   Smazán mrtvý `BEHIND=` blok (počítal proměnnou, kterou nikdo nepoužije) a historka
   z 2026-08-22. *Instance: R2, R3, R5.*
5. `.claude/AGENT-RULES.md` — §1 pojmenoval jedinou výjimku ze zákazu přímých commitů
   (ops + release-notes) a uzavřel ji; §10 přestal předepisovat počítání review states.
   *Instance: R4, R1.*

Délka instrukcí: `agent-reviewer` +12/−12 (nula), `agent-tester` +13/−8, `AGENT-RULES`
+11/−6. U `BOARD-OPS` a `agent-ops` nejde můj podíl z `git diff` oddělit (orchestrátor
je editoval týž den před mým během) — odhadem +40/−25, resp. +45/−15. Čistý přírůstek
tedy ~60 řádků napříč pěti soubory a **nedostal jsem se na nulu**: čtyři z pěti změn
přidávají bránu tam, kde dosud žádná nebyla, a to se v méně slovech napsat nedá. Za
mazání se počítá jen to, co bylo prokazatelně mrtvé (`BEHIND=`, výčet frameworků,
historka z 08-22, duplicitní definice markeru).

### Co jsem vědomě nezměnil

- **`agent-retro.md` — ať čte otevřené `area:quality` issues, ne jen commity běhu.**
  Nejvyšší páka, jakou tu vidím (R1 by se bez toho neopravilo ani příště), ale je to
  6. úprava a strop je 5. **První na řadě příště.**
- **Schéma `MEMORY.md`.** `BOARD-OPS` §„MEMORY.md format" předepisuje `## Plan`,
  `## Progress`, `## Open questions`; živý soubor nemá ani jednu z nich. `agent-ops`
  Step 3 přitom instruuje „vyprázdni obsah pod těmi nadpisy" — proti dnešnímu souboru je
  ten úklid **neproveditelný**, což je mechanismus za bodem 8 (468 KB), který bod 8
  nepojmenovává. Nesahám na to, protože oprava je zapletená s návrhovým rozhodnutím
  z bodu 8b (`MEMORY.d/` × append-only × zápis jen orchestrátorem) a to není moje volba.
- **Žádnou bránu jsem nezměkčil.** Nabízelo se: `type:task` chybí u ~20 dětí z dávky
  09:42–09:44 (#212, #217, #218, #220, #222, #224–#243), zatímco děti story #148 ho mají.
  Nedoložil jsem, že to dnes něco stálo, tak to nejde do nálezů a nemění se kvůli tomu nic.
- **Úklid worktreeů (bod 10) a kvóta (bod 1)** zůstávají orchestrátorovi — obojí je
  zapsané, obojí má navrženého vlastníka, a duplikovat to sem by dokument jen nafouklo.
