using AresService;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Common;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Pure rules behind the e-mail send options: default resolution, merge on "save as default", CC parsing.</summary>
public class EmailAttachmentDefaultsTests
{
    private static BillingSettings Bs(EEmailAttachMode pdf = EEmailAttachMode.Inherit,
        EEmailAttachMode isdoc = EEmailAttachMode.Inherit, EEmailAttachMode ubl = EEmailAttachMode.Inherit) =>
        new() { EmailAttachPdf = pdf, EmailAttachIsdoc = isdoc, EmailAttachUbl = ubl };

    [Fact]
    public void NothingConfigured_SystemDefault_PdfYesIsdocYesUblAutomatic()
    {
        var resolved = EmailAttachmentDefaults.Resolve(null, null);

        resolved.ShouldBe(new ResolvedEmailAttachments(true, true, null));
    }

    [Theory]
    [InlineData(EEmailAttachMode.Inherit, EEmailAttachMode.Inherit, true)]   // system default
    [InlineData(EEmailAttachMode.Inherit, EEmailAttachMode.No, false)]       // company says no
    [InlineData(EEmailAttachMode.Yes, EEmailAttachMode.No, true)]            // client beats company
    [InlineData(EEmailAttachMode.No, EEmailAttachMode.Yes, false)]           // client beats company
    public void Pdf_ClientThenIssuerThenSystemDefault(EEmailAttachMode client, EEmailAttachMode issuer, bool expected)
    {
        EmailAttachmentDefaults.Resolve(Bs(pdf: client), Bs(pdf: issuer)).Pdf.ShouldBe(expected);
    }

    [Theory]
    [InlineData(EEmailAttachMode.Inherit, EEmailAttachMode.Inherit, null)]   // automatic
    [InlineData(EEmailAttachMode.Inherit, EEmailAttachMode.Yes, true)]
    [InlineData(EEmailAttachMode.No, EEmailAttachMode.Yes, false)]
    [InlineData(EEmailAttachMode.Yes, EEmailAttachMode.No, true)]
    public void Ubl_NullMeansAutomatic(EEmailAttachMode client, EEmailAttachMode issuer, bool? expected)
    {
        EmailAttachmentDefaults.Resolve(Bs(ubl: client), Bs(ubl: issuer)).Ubl.ShouldBe(expected);
    }

    [Fact]
    public void FieldsResolveIndependently()
    {
        var resolved = EmailAttachmentDefaults.Resolve(Bs(pdf: EEmailAttachMode.No), Bs(isdoc: EEmailAttachMode.No));

        resolved.ShouldBe(new ResolvedEmailAttachments(false, false, null));
    }

    [Fact]
    public void ApplyChoices_OnlyWritesWhatChangedOrWasAlreadyExplicit()
    {
        var target = Bs(isdoc: EEmailAttachMode.No);
        var before = new ResolvedEmailAttachments(true, false, null);

        // pdf: confirms the default (true) -> stays Inherit; isdoc: client already explicit -> follows the choice;
        // ubl: differs from the effective false -> Yes
        EmailAttachmentDefaults.ApplyChoices(target, before, ublEffectiveBefore: false, attachPdf: true, attachIsdoc: true, attachUbl: true);

        target.EmailAttachPdf.ShouldBe(EEmailAttachMode.Inherit);
        target.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.Yes);
        target.EmailAttachUbl.ShouldBe(EEmailAttachMode.Yes);
    }

    [Fact]
    public void ApplyChoices_NullChoicesLeaveTheFieldsAlone()
    {
        var target = Bs(EEmailAttachMode.Yes, EEmailAttachMode.No, EEmailAttachMode.Yes);

        EmailAttachmentDefaults.ApplyChoices(target, new ResolvedEmailAttachments(true, false, true), true, null, null, null);

        target.ShouldSatisfyAllConditions(
            () => target.EmailAttachPdf.ShouldBe(EEmailAttachMode.Yes),
            () => target.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.No),
            () => target.EmailAttachUbl.ShouldBe(EEmailAttachMode.Yes));
    }
}

public class EmailAddressListTests
{
    [Theory]
    [InlineData("a@x.cz, b@y.cz;c@z.cz", new[] { "a@x.cz", "b@y.cz", "c@z.cz" })]
    [InlineData("a@x.cz\nb@y.cz  c@z.cz,,", new[] { "a@x.cz", "b@y.cz", "c@z.cz" })]
    [InlineData("  ", new string[0])]
    [InlineData(null, new string[0])]
    public void Split_AcceptsCommaSemicolonWhitespaceAndNewlines(string? raw, string[] expected)
    {
        EmailAddressList.Split(raw).ShouldBe(expected);
    }

    [Fact]
    public void Normalize_DropsBlanksAndCaseInsensitiveDuplicates()
    {
        EmailAddressList.Normalize([" A@x.cz ", "a@X.cz", "", "b@x.cz"]).ShouldBe(["A@x.cz", "b@x.cz"]);
    }

    [Theory]
    [InlineData("jan@firma.cz", true)]
    [InlineData("jan.novak+faktury@sub.firma.co.uk", true)]
    [InlineData("Jan <jan@firma.cz>", false)]
    [InlineData("jan@", false)]
    [InlineData("jan", false)]
    [InlineData("a@b.cz\r\nBcc: x@y.cz", false)]
    [InlineData("", false)]
    public void IsValidAddress_OnlyBareAddresses(string address, bool expected)
    {
        EmailAddressList.IsValidAddress(address).ShouldBe(expected);
    }

    [Fact]
    public void ValidateCc_NullAndEmptyAreFine_TooManyAndMalformedAreNot()
    {
        EmailAddressList.ValidateCc(null).ShouldBeNull();
        EmailAddressList.ValidateCc([]).ShouldBeNull();
        EmailAddressList.ValidateCc(Enumerable.Range(1, EmailAddressList.MaxCc).Select(i => $"u{i}@x.cz").ToList()).ShouldBeNull();

        EmailAddressList.ValidateCc(Enumerable.Range(1, EmailAddressList.MaxCc + 1).Select(i => $"u{i}@x.cz").ToList())
            .ShouldNotBeNull().ShouldContain("10");
        EmailAddressList.ValidateCc(["ok@x.cz", "broken"]).ShouldNotBeNull().ShouldContain("broken");
    }
}

/// <summary>FileAttachmentService.DownloadSelectedAsync: the ownership + size guard for stored files in an e-mail.</summary>
public class FileAttachmentSelectionTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly FileAttachmentService _service;

    public FileAttachmentSelectionTests()
    {
        _context = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _service = new FileAttachmentService(_context, _storage, Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<FileAttachmentService>>());
        _storage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });

        _context.FileAttachment.AddRange(
            File(1, "Invoice", 10, "mine.pdf", 100),
            File(2, "Invoice", 11, "other-invoice.pdf", 100),
            File(3, "Client", 10, "client-file.pdf", 100),
            File(4, "Invoice", 10, "big.zip", 30 * 1024 * 1024));
        _context.SaveChanges();
    }

    private static FileAttachment File(long id, string entity, long record, string name, long size) => new()
    {
        Id = id, EntityName = entity, RecordId = record, OriginalFileName = name, FileSizeBytes = size, BlobPath = $"1/{id}"
    };

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task OwnFiles_AreReturnedWithContentAndMetadata()
    {
        var result = await _service.DownloadSelectedAsync("Invoice", 10, [1], 20 * 1024 * 1024);

        result.Single().Meta.OriginalFileName.ShouldBe("mine.pdf");
        result.Single().Content.ShouldBe([1, 2, 3]);
    }

    [Theory]
    [InlineData(2L)]   // belongs to another invoice
    [InlineData(3L)]   // same record id but another entity type
    [InlineData(999L)] // does not exist
    public async Task ForeignOrUnknownId_IsRejected_AndNoBlobIsRead(long id)
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.DownloadSelectedAsync("Invoice", 10, [1, id], 20 * 1024 * 1024));

        await _storage.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default);
    }

    [Fact]
    public async Task TooLargeSelection_IsRejectedFromMetadata_BeforeAnyBlobIsRead()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.DownloadSelectedAsync("Invoice", 10, [1, 4], 20 * 1024 * 1024));

        ex.Message.ShouldContain("20 MB");
        await _storage.DidNotReceiveWithAnyArgs().DownloadAsync(default!, default);
    }

    [Fact]
    public async Task UnreadableBlob_IsAnErrorNotASilentSkip()
    {
        _storage.DownloadAsync("1/1", Arg.Any<CancellationToken>()).Returns<byte[]>(_ => throw new FileNotFoundException());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.DownloadSelectedAsync("Invoice", 10, [1], 1024));

        ex.Message.ShouldContain("mine.pdf");
    }

    [Fact]
    public async Task DuplicateIdsAreIgnored()
    {
        var result = await _service.DownloadSelectedAsync("Invoice", 10, [1, 1, 1], 1024);

        result.Count.ShouldBe(1);
    }
}

/// <summary>ClientService: the three e-mail attachment defaults follow "null = keep, Inherit = reset".</summary>
public class ClientServiceEmailAttachmentDefaultsTests : IDisposable
{
    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public ClientServiceEmailAttachmentDefaultsTests()
    {
        _context = NewContext();
        _service = new ClientService(_context, Substitute.For<IAresService>(), Substitute.For<ILogger<ClientService>>());
        using var seed = NewContext();
        seed.Client.Add(new Client { Id = 5, CompanyName = "C", RegistrationNumber = "1", IsActive = true });
        seed.SaveChanges();
    }

    private TenantDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(_dbName).Options);

    public void Dispose() => _context.Dispose();

    private BillingSettings Stored()
    {
        using var db = NewContext();
        return db.BillingSettings.AsNoTracking().Single(b => b.ClientId == 5);
    }

    [Fact]
    public async Task PartialUpdate_SetsOnlySentFields_AndInheritResetsOne()
    {
        await _service.UpdateClientAsync(5, new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { EmailAttachPdf = EEmailAttachMode.No, EmailAttachUbl = EEmailAttachMode.Yes }
        });
        var first = Stored();
        first.EmailAttachPdf.ShouldBe(EEmailAttachMode.No);
        first.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.Inherit);
        first.EmailAttachUbl.ShouldBe(EEmailAttachMode.Yes);

        await _service.UpdateClientAsync(5, new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { EmailAttachUbl = EEmailAttachMode.Inherit }
        });
        var second = Stored();
        second.EmailAttachPdf.ShouldBe(EEmailAttachMode.No);        // not sent -> kept
        second.EmailAttachUbl.ShouldBe(EEmailAttachMode.Inherit);   // explicit reset
    }

    [Fact]
    public async Task FullReplace_WritesAllThreeValues()
    {
        await _service.UpdateBillingSettingsAsync(5, new CreateBillingSettingsDto
        {
            EmailAttachPdf = EEmailAttachMode.Yes, EmailAttachIsdoc = EEmailAttachMode.No, EmailAttachUbl = EEmailAttachMode.Yes
        });

        var stored = Stored();
        stored.EmailAttachPdf.ShouldBe(EEmailAttachMode.Yes);
        stored.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.No);
        stored.EmailAttachUbl.ShouldBe(EEmailAttachMode.Yes);
    }

    [Fact]
    public async Task ReadModel_ExposesTheValues()
    {
        await _service.UpdateBillingSettingsAsync(5, new CreateBillingSettingsDto { EmailAttachIsdoc = EEmailAttachMode.No });

        var dto = await _service.GetClientByIdAsync(5);

        dto!.BillingSettings!.EmailAttachIsdoc.ShouldBe(EEmailAttachMode.No);
    }
}

/// <summary>InvoiceController: new send-email fields reach the service; defaults endpoint; bulk send uses the client's default recipient.</summary>
public class InvoiceControllerEmailOptionsTests
{
    private readonly IEmailService _email = Substitute.For<IEmailService>();
    private readonly IInvoiceService _invoices = Substitute.For<IInvoiceService>();

    private InvoiceController Build() => new(
        _invoices, Substitute.For<IPdfExportService>(), Substitute.For<IDocumentFileNameService>(),
        Substitute.For<IIsdocExportService>(), Substitute.For<IUblExportService>(), _email,
        Substitute.For<IQrPaymentService>(), Substitute.For<ICloudStorageOrchestrator>(),
        Substitute.For<IPaymentMatchingService>(), Substitute.For<ILogger<InvoiceController>>());

    [Fact]
    public async Task Send_PassesCcAttachmentChoicesFilesAndSaveFlag()
    {
        var dto = new SendInvoiceEmailDto
        {
            RecipientEmail = "a@x.cz", CcEmails = ["c1@x.cz", "C1@x.cz", "c2@x.cz"],
            AttachPdf = false, AttachIsdoc = true, AttachUbl = true, FileAttachmentIds = [4, 5], SaveAttachmentDefaults = true
        };

        var result = await Build().SendInvoiceEmail(7, dto, CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        await _email.Received(1).SendInvoiceEmailAsync(7, "a@x.cz", Arg.Any<CancellationToken>(),
            Arg.Is<SendInvoiceEmailOptions?>(o => o != null
                && o.CcEmails!.SequenceEqual(new[] { "c1@x.cz", "c2@x.cz" })
                && o.AttachPdf == false && o.AttachIsdoc == true && o.AttachUbl == true
                && o.FileAttachmentIds!.SequenceEqual(new long[] { 4, 5 })
                && o.SaveAttachmentDefaults));
    }

    [Fact]
    public async Task Send_WithoutRecipient_PassesBlankSoTheServiceUsesTheClientsContact()
    {
        var result = await Build().SendInvoiceEmail(7, new SendInvoiceEmailDto(), CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        await _email.Received(1).SendInvoiceEmailAsync(7, string.Empty, Arg.Any<CancellationToken>(), Arg.Any<SendInvoiceEmailOptions?>());
    }

    [Fact]
    public async Task Send_InvalidCc_Returns400AndSendsNothing()
    {
        var result = await Build().SendInvoiceEmail(7, new SendInvoiceEmailDto { RecipientEmail = "a@x.cz", CcEmails = ["nope"] }, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
        await _email.DidNotReceiveWithAnyArgs().SendInvoiceEmailAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Send_ForeignAttachmentId_FromTheService_Returns400()
    {
        _email.SendInvoiceEmailAsync(7, "a@x.cz", Arg.Any<CancellationToken>(), Arg.Any<SendInvoiceEmailOptions?>())
            .Returns<Task>(_ => throw new ArgumentException("Attachment id(s) 9 do not belong to this invoice."));

        var result = await Build().SendInvoiceEmail(7, new SendInvoiceEmailDto { RecipientEmail = "a@x.cz", FileAttachmentIds = [9] }, CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Send_TooLargeOrNoRecipient_FromTheService_Returns400()
    {
        _email.SendInvoiceEmailAsync(7, string.Empty, Arg.Any<CancellationToken>(), Arg.Any<SendInvoiceEmailOptions?>())
            .Returns<Task>(_ => throw new InvalidOperationException("No recipient: ..."));

        var result = await Build().SendInvoiceEmail(7, new SendInvoiceEmailDto(), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task EmailDefaults_ReturnsTheServiceResult_And404ForUnknownInvoice()
    {
        var defaults = new InvoiceEmailDefaultsDto { AttachPdf = false };
        _email.GetSendDefaultsAsync(7, Arg.Any<CancellationToken>()).Returns(defaults);
        _email.GetSendDefaultsAsync(8, Arg.Any<CancellationToken>()).Returns<InvoiceEmailDefaultsDto>(_ => throw new KeyNotFoundException());

        var ok = await Build().GetEmailDefaults(7);
        var missing = await Build().GetEmailDefaults(8);

        ok.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeSameAs(defaults);
        missing.Result.ShouldBeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task BulkSend_SendsWithABlankRecipient_SoTheClientsDefaultContactAndAttachmentsApply()
    {
        _invoices.GetInvoiceByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 1, ClientName = "Client Ltd" });

        var result = await Build().BulkSendEmail(new BulkOperationRequest { InvoiceIds = [1] }, CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<BulkOperationResult>().SuccessCount.ShouldBe(1);
        // Never the client NAME as an address (the old behaviour), and no per-send attachment overrides.
        await _email.Received(1).SendInvoiceEmailAsync(1, string.Empty, Arg.Any<CancellationToken>(), null);
    }
}
