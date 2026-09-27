using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.Reminder;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>Tests for <see cref="PaymentTools"/> (N3.5).</summary>
public class PaymentToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    // ── ListPayments ─────────────────────────────────────────────────────

    [Fact]
    public async Task ListPayments_ReturnsPagedResult()
    {
        _api.GetPaymentsPagedAsync(
                Arg.Any<EMatchStatus?>(), Arg.Any<EPaymentDirection?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<BankTransactionDto>(
                [new() { Id = 1, MatchStatus = EMatchStatus.Unmatched }], 1, 1, 20));

        var json = await PaymentTools.ListPayments(_api);

        JsonDocument.Parse(json).RootElement.GetProperty("items")[0].GetProperty("id").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task ListPayments_ParsesStatusAndDirection()
    {
        _api.GetPaymentsPagedAsync(
                Arg.Any<EMatchStatus?>(), Arg.Any<EPaymentDirection?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<BankTransactionDto>([], 0, 1, 20));

        await PaymentTools.ListPayments(_api, status: "Unmatched", direction: "Incoming");

        await _api.Received(1).GetPaymentsPagedAsync(
            EMatchStatus.Unmatched, EPaymentDirection.Incoming, null, null, 1, 20, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListPayments_UnknownStatus_ReturnsErrorWithoutCallingApi()
    {
        var json = await PaymentTools.ListPayments(_api, status: "Nonsense");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Unknown status");
        await _api.DidNotReceive().GetPaymentsPagedAsync(
            Arg.Any<EMatchStatus?>(), Arg.Any<EPaymentDirection?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── GetPayment ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetPayment_ReturnsPayment()
    {
        _api.GetPaymentByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(new BankTransactionDto { Id = 5, CounterpartyName = "Acme" });

        var json = await PaymentTools.GetPayment(_api, 5);

        JsonDocument.Parse(json).RootElement.GetProperty("counterpartyName").GetString().ShouldBe("Acme");
    }

    [Fact]
    public async Task GetPayment_NotFound_ReturnsError()
    {
        _api.GetPaymentByIdAsync(999, Arg.Any<CancellationToken>()).Returns((BankTransactionDto?)null);

        var json = await PaymentTools.GetPayment(_api, 999);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("not found");
    }

    // ── ListReminders ────────────────────────────────────────────────────

    [Fact]
    public async Task ListReminders_ByInvoiceId_UsesInvoiceEndpoint()
    {
        _api.GetRemindersByInvoiceAsync(7, Arg.Any<CancellationToken>())
            .Returns(new List<ReminderDto> { new() { Id = 1, InvoiceId = 7, Level = 1 } });

        var json = await PaymentTools.ListReminders(_api, invoiceId: 7);

        JsonDocument.Parse(json).RootElement[0].GetProperty("invoiceId").GetInt64().ShouldBe(7);
        await _api.DidNotReceive().GetRemindersPagedAsync(Arg.Any<ReminderFilterDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReminders_WithoutInvoiceId_UsesPagedEndpoint()
    {
        _api.GetRemindersPagedAsync(Arg.Any<ReminderFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ReminderDto>([new() { Id = 1 }], 1, 1, 20));

        var json = await PaymentTools.ListReminders(_api);

        JsonDocument.Parse(json).RootElement.GetProperty("items")[0].GetProperty("id").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task ListReminders_UnknownStatus_ReturnsErrorWithoutCallingApi()
    {
        var json = await PaymentTools.ListReminders(_api, status: "Nonsense");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Unknown status");
        await _api.DidNotReceive().GetRemindersPagedAsync(Arg.Any<ReminderFilterDto>(), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().GetRemindersByInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ── GetReminderSettings ──────────────────────────────────────────────

    [Fact]
    public async Task GetReminderSettings_ReturnsSettings()
    {
        _api.GetReminderSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new ReminderSettingsDto { IsEnabled = true, MaxReminderLevel = 3 });

        var json = await PaymentTools.GetReminderSettings(_api);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("isEnabled").GetBoolean().ShouldBeTrue();
        root.GetProperty("maxReminderLevel").GetInt32().ShouldBe(3);
    }
}
