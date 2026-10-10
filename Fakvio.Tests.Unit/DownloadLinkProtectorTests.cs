using System.Text.Json;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the signed download token (DownloadLinkProtector) and the per-kind parameter validation
/// used by both the mint and the redeem endpoint. Uses an ephemeral in-memory key ring.
/// </summary>
public class DownloadLinkProtectorTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private static DownloadTokenPayload Payload(long companyId = 7) => new(
        companyId, 3, new CreateDownloadLinkDto { Kind = EDownloadKind.InvoicePdf, Id = 42 });

    [Fact]
    public void Roundtrip_ReturnsSamePayload()
    {
        var sut = new DownloadLinkProtector(DataProtectionProvider.Create("test"), Ttl);

        var token = sut.Protect(Payload(), out var expiresAt);
        var back = sut.Unprotect(token);

        back.ShouldNotBeNull();
        back.CompanyId.ShouldBe(7);
        back.UserId.ShouldBe(3);
        back.Parameters.Kind.ShouldBe(EDownloadKind.InvoicePdf);
        back.Parameters.Id.ShouldBe(42);
        expiresAt.ShouldBeInRange(DateTime.UtcNow.AddMinutes(9), DateTime.UtcNow.AddMinutes(11));
    }

    [Fact]
    public async Task Unprotect_Expired_ReturnsNull()
    {
        var sut = new DownloadLinkProtector(DataProtectionProvider.Create("test"), TimeSpan.FromMilliseconds(1));
        var token = sut.Protect(Payload(), out _);

        await Task.Delay(100);

        sut.Unprotect(token).ShouldBeNull();
    }

    [Fact]
    public void Unprotect_Tampered_ReturnsNull()
    {
        var sut = new DownloadLinkProtector(DataProtectionProvider.Create("test"), Ttl);
        var token = sut.Protect(Payload(), out _);

        // Flip one character in the middle of the ciphertext.
        var i = token.Length / 2;
        var tampered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];

        sut.Unprotect(tampered).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Unprotect_Garbage_ReturnsNull(string garbage)
    {
        new DownloadLinkProtector(DataProtectionProvider.Create("test"), Ttl).Unprotect(garbage).ShouldBeNull();
    }

    [Fact]
    public void Unprotect_TokenFromAnotherPurpose_ReturnsNull()
    {
        // A perfectly valid, unexpired token from the same key ring - but minted for a different feature
        // (e.g. the OAuth consent ticket). It must not be accepted as a download link.
        var provider = DataProtectionProvider.Create("test");
        var foreign = provider.CreateProtector("Fakvio.OAuth.AuthorizeTicket.v1")
            .ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(Payload()), Ttl);

        new DownloadLinkProtector(provider, Ttl).Unprotect(foreign).ShouldBeNull();
    }

    [Fact]
    public void Unprotect_TokenFromAnotherKeyRing_ReturnsNull()
    {
        var token = new DownloadLinkProtector(DataProtectionProvider.Create("ring-a"), Ttl).Protect(Payload(), out _);

        new DownloadLinkProtector(DataProtectionProvider.Create("ring-b"), Ttl).Unprotect(token).ShouldBeNull();
    }

    // ── Validate (closed whitelist + per-kind parameters) ───────────────

    [Fact]
    public void Validate_UnknownKind_IsRejected()
        => DownloadLinkController.Validate(new CreateDownloadLinkDto { Kind = (EDownloadKind)999, Id = 1 }).ShouldNotBeNull();

    [Theory]
    [InlineData(EDownloadKind.InvoicePdf)]
    [InlineData(EDownloadKind.ReceivedInvoiceIsdoc)]
    [InlineData(EDownloadKind.AttachmentFile)]
    public void Validate_SingleKindWithoutId_IsRejected(EDownloadKind kind)
        => DownloadLinkController.Validate(new CreateDownloadLinkDto { Kind = kind }).ShouldNotBeNull();

    [Fact]
    public void Validate_BulkKind_RejectsEmptyTooManyAndNonPositiveIds()
    {
        DownloadLinkController.Validate(new() { Kind = EDownloadKind.InvoiceBulkPdf, Ids = [] }).ShouldNotBeNull();
        DownloadLinkController.Validate(new() { Kind = EDownloadKind.InvoiceBulkPdf, Ids = [0] }).ShouldNotBeNull();
        DownloadLinkController.Validate(new()
        {
            Kind = EDownloadKind.InvoiceBulkPdf,
            Ids = Enumerable.Range(1, DownloadLinkController.MaxBulkIds + 1).Select(i => (long)i).ToList()
        }).ShouldNotBeNull();
        DownloadLinkController.Validate(new() { Kind = EDownloadKind.InvoiceBulkPdf, Ids = [1, 2] }).ShouldBeNull();
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("Invoice/1")]
    [InlineData("")]
    public void Validate_AttachmentsZip_RejectsNonIdentifierEntityName(string entityName)
        => DownloadLinkController.Validate(new() { Kind = EDownloadKind.AttachmentsZip, Id = 1, EntityName = entityName }).ShouldNotBeNull();

    [Fact]
    public void Validate_VatEpoWithoutPeriod_IsRejected()
    {
        DownloadLinkController.Validate(new() { Kind = EDownloadKind.VatEpoReturn, Year = 2026 }).ShouldNotBeNull();
        DownloadLinkController.Validate(new()
        {
            Kind = EDownloadKind.VatEpoReturn, Year = 2026, Period = 3, PeriodType = EVatPeriodType.Monthly
        }).ShouldBeNull();
    }
}
