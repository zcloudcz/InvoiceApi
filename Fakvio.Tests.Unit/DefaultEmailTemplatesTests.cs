using System.Text.RegularExpressions;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Fakvio.Application.Service;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Default e-mail templates must exist in Czech AND English for every document/system e-mail type,
/// the Master and Tenant seeds must agree, and the language must decide which one is picked.
/// Regression for: Czech clients received English invoice / credit note / reminder / invitation e-mails
/// because those seeds were English text stored with Language = "cs".
/// </summary>
public class DefaultEmailTemplatesTests
{
    /// <summary>Every e-mail type that must have both a Czech and an English default.</summary>
    private static readonly EContentTemplateType[] AllLocalizedTypes =
    [
        EContentTemplateType.InvoiceEmail,
        EContentTemplateType.CreditNoteEmail,
        EContentTemplateType.AdvanceInvoiceEmail,
        EContentTemplateType.TaxReceiptForAdvanceEmail,
        EContentTemplateType.InvitationEmail,
        EContentTemplateType.ReminderEmail,
    ];

    public static TheoryData<EContentTemplateType> LocalizedTypes => new(AllLocalizedTypes);

    private static List<ContentTemplate> TenantSeed() => ReadSeed(new TenantDbContext(
        new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));

    private static List<ContentTemplate> MasterSeed() => ReadSeed(new MasterDbContext(
        new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options));

    /// <summary>Reads the HasData rows straight from the EF model (what a fresh database receives).</summary>
    private static List<ContentTemplate> ReadSeed(DbContext db)
    {
        using (db)
        {
            // The runtime model drops seed data; the design-time model keeps it.
            return db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ContentTemplate))!.GetSeedData()
                .Select(row => new ContentTemplate
                {
                    Id = (long)row["Id"]!,
                    Name = (string)row["Name"]!,
                    Subject = row.TryGetValue("Subject", out var s) ? (string?)s : null,
                    HtmlBody = (string)row["HtmlBody"]!,
                    TemplateType = (EContentTemplateType)row["TemplateType"]!,
                    IsDefault = (bool)row["IsDefault"]!,
                    IsActive = (bool)row["IsActive"]!,
                    Language = (string)row["Language"]!,
                })
                .ToList();
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\{(.+?)\}\}").Select(m => m.Groups[1].Value).ToHashSet();

    [Theory]
    [MemberData(nameof(LocalizedTypes))]
    public void Seed_HasOneActiveDefault_PerLanguage_ForEveryLocalizedType(EContentTemplateType type)
    {
        foreach (var seed in new[] { TenantSeed(), MasterSeed() })
        {
            foreach (var language in new[] { "cs", "en" })
            {
                // Exactly one default per (type, language) — the "one default per type+language" rule.
                var defaults = seed.Where(t => t.TemplateType == type && t.Language == language && t.IsDefault && t.IsActive);
                defaults.Count().ShouldBe(1, $"{type}/{language}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(LocalizedTypes))]
    public void Seed_CzechAndEnglish_UseTheSamePlaceholders(EContentTemplateType type)
    {
        var seed = TenantSeed();
        var cs = seed.Single(t => t.TemplateType == type && t.Language == "cs");
        var en = seed.Single(t => t.TemplateType == type && t.Language == "en");

        Placeholders(cs.Subject + cs.HtmlBody).ShouldBe(Placeholders(en.Subject + en.HtmlBody), ignoreOrder: true);
    }

    [Fact]
    public void Seed_CzechTemplates_AreNoLongerEnglish()
    {
        var cs = TenantSeed().Where(t => t.Language == "cs" && t.TemplateType is
            EContentTemplateType.InvoiceEmail or EContentTemplateType.CreditNoteEmail
            or EContentTemplateType.InvitationEmail or EContentTemplateType.ReminderEmail).ToList();

        cs.Count.ShouldBe(4);
        foreach (var t in cs)
        {
            t.Subject.ShouldNotContain("from {{CompanyName}}");
            t.HtmlBody.ShouldNotContain("Dear customer");
            t.HtmlBody.ShouldNotContain("Thank you");
        }

        TenantSeed().Single(t => t.TemplateType == EContentTemplateType.InvoiceEmail && t.Language == "cs")
            .Subject.ShouldBe("Faktura {{InvoiceNumber}} od {{CompanyName}}");
    }

    [Fact]
    public void Seed_MasterAndTenant_AreIdentical()
    {
        // Master is the source copied into every new tenant, Tenant is the migration-seeded default: they must not drift.
        // Only the localized e-mail types are compared (PDF bodies are huge and unrelated; the 2FA e-mail is Master-only).
        var localized = AllLocalizedTypes.ToHashSet();
        List<string> Keys(IEnumerable<ContentTemplate> seed) => seed
            .Where(t => localized.Contains(t.TemplateType))
            .OrderBy(t => t.Id)
            .Select(t => $"{t.Id}|{t.TemplateType}|{t.Language}|{t.IsDefault}|{t.IsActive}|{t.Subject}|{t.HtmlBody}")
            .ToList();

        var tenant = Keys(TenantSeed());
        var master = Keys(MasterSeed());

        tenant.Count.ShouldBe(12); // 6 types x (cs + en)
        master.Count.ShouldBe(tenant.Count);
        for (var i = 0; i < tenant.Count; i++)
            master[i].ShouldBe(tenant[i]);
    }

    /// <summary>
    /// The language-aware lookup picks the Czech default for "cs" and the English one for "en" even though both are
    /// defaults of the same type — adding the English default must not hide the Czech one (and vice versa).
    /// </summary>
    [Theory]
    [MemberData(nameof(LocalizedTypes))]
    public async Task GetDefaultByType_PicksTemplateByLanguage(EContentTemplateType type)
    {
        await using var tenantDb = new TenantDbContext(
            new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await using var masterDb = new MasterDbContext(
            new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        tenantDb.ContentTemplate.AddRange(TenantSeed());
        await tenantDb.SaveChangesAsync();

        var resolver = Substitute.For<ITenantResolver>();
        resolver.GetCurrentCompanyId().Returns(1L);
        var service = new ContentTemplateService(tenantDb, masterDb, resolver, Substitute.For<ILogger<ContentTemplateService>>());

        var cs = await service.GetDefaultByTypeAsync(type, "cs");
        var en = await service.GetDefaultByTypeAsync(type, "en");
        var de = await service.GetDefaultByTypeAsync(type, "de"); // no German: falls back to any-language default

        cs!.Language.ShouldBe("cs");
        en!.Language.ShouldBe("en");
        cs.Id.ShouldNotBe(en.Id);
        de!.Language.ShouldBe("cs", "the any-language fallback must keep resolving to the Czech default (lowest Id)");
    }
}
