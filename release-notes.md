# Fakvio — Release notes

Záznam dokončených změn. **Jeden záznam na každý task, který byl mergnut do
`develop`.** Zapisuje `agent-ops` v okamžiku mergu — viz `.claude/agents/agent-ops.md`
Step 2b. Ručně sem nepiš; když záznam chybí, chybí i merge.

Řazeno **nejnovější nahoře**. Sekce `## Nevydáno` drží to, co je v `develop`, ale
ještě nebylo promováno na `master`. Při `/release` se přejmenuje na verzi s datem
a nad ní vznikne nová prázdná `## Nevydáno`.

Formát řádku:

    - **#<issue>** — co se změnilo a proč to zajímá čtenáře. (PR #<pr>, `<commit>`)

Píše se **dopad, ne diff**. „Opraveno `FindAsync` bez `Include`" nikomu nic neřekne;
„splatnost faktury ignorovala nastavení klienta a vždy použila 14 dní" ano.

---

## Nevydáno

### Opravy

- **#156** — Chat posílal uživateli do prohlížeče celý stack trace serverové výjimky.
  Nyní dostane jen bezpečnou hlášku s referenčním ID, podle kterého se chyba dohledá
  v `/logs`. Neznámá konverzace vrací 404 místo 500. (PR #172, `f6a4f37`)
- **#155** — Když číselná řada chyběla nebo byla vypnutá, doklad tiše dostal náhradní
  číslo `INV2026001` mimo řadu. Nově se akce nedokončí a uživatel se dozví, co doplnit.
  Souběžná kolize čísel se hlásí jako přechodná s výzvou akci zopakovat. (PR #171, `d3c9b4c`)
- **#153** — Při zakládání firmy se vystaviteli nezkopíroval bankovní účet ani fakturační
  nastavení. (PR #170, `aa6a26c`)
- **#134** — Každé zřízení tenanta nechávalo otevřené spojení do databáze; při rušení
  schématu se navíc neuvolnil jeho datový zdroj. (PR #169, `4f75e39`)

### Změny pro vývojáře

- **#178** — Výstupy source generátorů pod `Generated/` se přestaly verzovat. Každý build
  je přepisoval a vyráběl fantomové diffy. (PR #182, `eff1f5b`)
- **#137** — Čtyři testy připojení k databázi, které padaly v každém běhu, jsou nově za
  bránou `FAKVIO_DB_SMOKE=1`. Lokální běh je poprvé zelený. (PR #168, `eedfeb9`)
- **#135** — `dotnet ef` funguje proti Azure přes design-time factory. (PR #167, `b34e53f`)
- **#133** — Composition root staví databázová spojení přes `INpgsqlDataSourceFactory`,
  což je základ pro přepínání mezi Entra ID a heslem. (PR #143, `ad0cd1f`)
- **#132** — `DatabaseOptions` + `INpgsqlDataSourceFactory`. (PR #142, `a88e6e4`)
