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
