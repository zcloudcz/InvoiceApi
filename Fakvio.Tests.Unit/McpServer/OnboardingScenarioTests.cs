using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.NumberSequence;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// N3.7 — story N3's acceptance criterion end-to-end: a brand new company with no number
/// sequence walks <c>get_readiness → list_number_sequences → create_number_sequence →
/// create_invoice → complete_invoice</c> entirely through MCP, with no dead end at "read-only
/// settings" (the state before story N3).
///
/// <para>
/// Junior note: this is deliberately a scenario test over a mocked <see cref="IFakvioApiClient"/>,
/// not an integration test against a real API — the other test files in this folder already cover
/// each tool's own behaviour; what this one adds is proof that the tools compose into the flow the
/// story promises. A manual run against a local <c>Fakvio.API</c> over stdio (`.mcp.json.sample`)
/// is the other half of the AC and is logged in the PR description, not here — a unit test cannot
/// prove the wiring in <c>Program.cs</c>/`McpServerRegistration` end to end without a live process.
/// </para>
/// </summary>
public class OnboardingScenarioTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    [Fact]
    public async Task NewCompanyWithoutNumberSequence_CanOnboardEntirelyThroughMcp()
    {
        // ── Step 1: get_readiness reports the missing number sequence ───────
        _api.GetReadinessAsync(null, Arg.Any<CancellationToken>())
            .Returns(
                new ReadinessReportDto
                {
                    Issues =
                    [
                        new ReadinessIssueDto
                        {
                            Code = ReadinessCodes.NumberSequenceMissing,
                            Severity = EReadinessSeverity.Blocking,
                            MissingFields = ["Invoice"],
                            FixRoute = "/number-sequences"
                        }
                    ]
                },
                // Second call, after the sequence is created: nothing left to fix.
                new ReadinessReportDto { Issues = [] });

        var firstReadiness = await ReadinessTools.GetReadiness(_api);
        var firstReport = JsonDocument.Parse(firstReadiness).RootElement;
        firstReport.GetProperty("isReady").GetBoolean().ShouldBeFalse();
        firstReport.GetProperty("issues")[0].GetProperty("code").GetString()
            .ShouldBe(ReadinessCodes.NumberSequenceMissing);

        // ── Step 2: list_number_sequences shows no sequences, but a usable format ───
        _api.GetNumberSequencesAsync(Arg.Any<EDocumentType?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<NumberSequenceDto>());
        _api.GetNumberSequenceFormatsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new List<NumberSequenceFormatDto> { new() { Id = 1, Name = "yyyyNNNN" } });

        var listed = await SettingsTools.ListNumberSequences(_api);
        var listedRoot = JsonDocument.Parse(listed).RootElement;
        listedRoot.GetProperty("sequences").GetArrayLength().ShouldBe(0);
        var formatId = listedRoot.GetProperty("formats")[0].GetProperty("id").GetInt64();

        // ── Step 3: create_number_sequence fixes the gap ────────────────────
        _api.CreateNumberSequenceAsync(Arg.Any<CreateNumberSequenceDto>(), Arg.Any<CancellationToken>())
            .Returns(new NumberSequenceDto
            {
                Id = 1,
                Name = "Faktury",
                DocumentType = EDocumentType.Invoice,
                IsDefault = true,
                NumberSequenceFormatId = formatId
            });

        var created = await SettingsTools.CreateNumberSequence(
            _api, name: "Faktury", documentType: "Invoice", numberSequenceFormatId: formatId);
        JsonDocument.Parse(created).RootElement.GetProperty("isDefault").GetBoolean().ShouldBeTrue();

        // Readiness now reports the tenant as ready (second configured GetReadinessAsync answer).
        var secondReadiness = await ReadinessTools.GetReadiness(_api);
        JsonDocument.Parse(secondReadiness).RootElement.GetProperty("isReady").GetBoolean().ShouldBeTrue();

        // ── Step 4: create_invoice — a non-VAT-payer issuer, no VAT rate lookup needed ──
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, CompanyName = "Acme s.r.o.", IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 100, Status = EInvoiceStatus.Draft });

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Web development", Quantity = 10, Unit = "hrs", UnitPrice = 1500 }
        };
        var invoiceJson = await InvoiceTools.CreateInvoice(_api, clientId: 5, items: items);
        var invoiceRoot = JsonDocument.Parse(invoiceJson).RootElement;
        invoiceRoot.GetProperty("id").GetInt64().ShouldBe(100);
        invoiceRoot.GetProperty("status").GetString().ShouldBe("Draft");

        // ── Step 5: complete_invoice issues the document ────────────────────
        _api.CompleteInvoiceAsync(100, Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 100, Status = EInvoiceStatus.Completed, DocumentNumber = "FAK2026001" });

        var completedJson = await InvoiceTools.CompleteInvoice(_api, 100);
        var completedRoot = JsonDocument.Parse(completedJson).RootElement;
        completedRoot.GetProperty("status").GetString().ShouldBe("Completed");
        completedRoot.GetProperty("documentNumber").GetString().ShouldBe("FAK2026001");
    }
}
