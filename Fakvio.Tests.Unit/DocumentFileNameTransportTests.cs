using System.Net;
using System.Net.Http.Headers;
using AresService;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.McpServer.Client;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Saving the four file name formats: stored by ClientService, rejected by the controller
/// when a placeholder is unknown (400 instead of silently dropping it later).
/// </summary>
public class DocumentFileNameFormatSettingsTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ClientService _service;

    public DocumentFileNameFormatSettingsTests()
    {
        _context = new TenantDbContext(new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _service = new ClientService(_context, Substitute.For<IAresService>(), Substitute.For<ILogger<ClientService>>());
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private async Task<long> CreateClientWithFormatsAsync()
    {
        var created = await _service.CreateClientAsync(new CreateClientDto
        {
            RegistrationNumber = "10000099",
            CompanyName = "Format Co",
            FetchFromAres = false,
            BillingSettings = new CreateBillingSettingsDto
            {
                InvoiceFileNameFormat = "{{DocumentNumber}}",
                CreditNoteFileNameFormat = "CN_{{DocumentNumber}}"
            }
        });
        return created.Id;
    }

    [Fact]
    public async Task CreateClient_StoresFormats_AndGetClientReturnsThem()
    {
        var id = await CreateClientWithFormatsAsync();

        var client = await _service.GetClientByIdAsync(id);

        client!.BillingSettings!.InvoiceFileNameFormat.ShouldBe("{{DocumentNumber}}");
        client.BillingSettings.CreditNoteFileNameFormat.ShouldBe("CN_{{DocumentNumber}}");
        client.BillingSettings.ProformaFileNameFormat.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateClient_NullKeeps_BlankClears_ValueIsTrimmed()
    {
        var id = await CreateClientWithFormatsAsync();

        var updated = await _service.UpdateClientAsync(id, new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto
            {
                InvoiceFileNameFormat = null,               // not sent -> keep
                CreditNoteFileNameFormat = "",              // blank -> clear (inherit)
                ProformaFileNameFormat = "  PF_{{Year}}  "  // trimmed
            }
        });

        updated!.BillingSettings!.InvoiceFileNameFormat.ShouldBe("{{DocumentNumber}}");
        updated.BillingSettings.CreditNoteFileNameFormat.ShouldBeNull();
        updated.BillingSettings.ProformaFileNameFormat.ShouldBe("PF_{{Year}}");
    }

    [Fact]
    public async Task UpdateBillingSettings_ReplacesAllFormats()
    {
        var id = await CreateClientWithFormatsAsync();

        var updated = await _service.UpdateBillingSettingsAsync(id, new CreateBillingSettingsDto
        {
            TaxReceiptFileNameFormat = "TR_{{DocumentNumber}}"
        });

        updated!.BillingSettings!.TaxReceiptFileNameFormat.ShouldBe("TR_{{DocumentNumber}}");
        updated.BillingSettings.InvoiceFileNameFormat.ShouldBeNull(); // full replace, like the prefixes
    }

    // ── Controller validation ────────────────────────────────────────────

    private static (ClientController Controller, IClientService Service) CreateController()
    {
        var service = Substitute.For<IClientService>();
        return (new ClientController(service, Substitute.For<ILogger<ClientController>>()), service);
    }

    [Fact]
    public async Task UpdateClient_UnknownPlaceholder_Returns400_AndDoesNotSave()
    {
        var (controller, service) = CreateController();

        var result = await controller.UpdateClient(1, new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { InvoiceFileNameFormat = "{{DocumentNumber}}_{{Oops}}" }
        });

        var bad = result.Result.ShouldBeOfType<BadRequestObjectResult>();
        bad.Value!.ToString()!.ShouldContain("Oops");
        await service.DidNotReceive().UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateClient_ValidFormats_ArePassedToTheService()
    {
        var (controller, service) = CreateController();
        service.UpdateClientAsync(1, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 1 });

        var result = await controller.UpdateClient(1, new UpdateClientDto
        {
            BillingSettings = new UpdateBillingSettingsDto { InvoiceFileNameFormat = "{{Year}}-{{DocumentNumber}}" }
        });

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task CreateClient_UnknownPlaceholder_Returns400()
    {
        var (controller, service) = CreateController();

        var result = await controller.CreateClient(new CreateClientDto
        {
            CompanyName = "X",
            BillingSettings = new CreateBillingSettingsDto { ProformaFileNameFormat = "{{Nope}}" }
        });

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await service.DidNotReceive().CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateBillingSettings_UnknownPlaceholder_Returns400()
    {
        var (controller, service) = CreateController();

        var result = await controller.UpdateBillingSettings(1, new CreateBillingSettingsDto
        {
            TaxReceiptFileNameFormat = "{{Nope}}"
        });

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await service.DidNotReceive().UpdateBillingSettingsAsync(Arg.Any<long>(), Arg.Any<CreateBillingSettingsDto>(), Arg.Any<CancellationToken>());
    }
}

/// <summary>
/// The server is the single source of truth for document file names (Content-Disposition).
/// Both clients — the Blazor UI service and the MCP API client — must hand that name on as-is.
/// </summary>
public class DocumentFileNameClientTests
{
    private static HttpResponseMessage PdfResponse(string? fileName = null, string? fileNameStar = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        if (fileName != null || fileNameStar != null)
        {
            response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileName = fileName,
                FileNameStar = fileNameStar
            };
        }

        return response;
    }

    private sealed class FixedResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response);
    }

    // ── Blazor UI service ────────────────────────────────────────────────

    private static FakvioService CreateUiService(HttpResponseMessage response)
    {
        var httpClient = new HttpClient(new FixedResponseHandler(response)) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        return new FakvioService(factory, NullLogger<FakvioService>.Instance, Substitute.For<AuthenticationStateProvider>());
    }

    [Fact]
    public async Task Ui_ExportToPdf_ReturnsTheServerFileName()
    {
        var file = await CreateUiService(PdfResponse("Faktura_2026001.pdf")).ExportToPdfAsync(1);

        file!.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        file.FileName.ShouldBe("Faktura_2026001.pdf");
    }

    [Fact]
    public async Task Ui_ExportToPdf_NonAsciiName_PrefersFileNameStar()
    {
        // Czech client names end up in file names; the RFC 5987 form carries them intact.
        var file = await CreateUiService(PdfResponse(fileName: "Faktura_Nov_k.pdf", fileNameStar: "Faktura_Novák.pdf")).ExportToPdfAsync(1);

        file!.FileName.ShouldBe("Faktura_Novák.pdf");
    }

    [Fact]
    public async Task Ui_ExportToPdf_NoHeader_FileNameIsNull_SoTheCallerUsesItsFallback()
    {
        var file = await CreateUiService(PdfResponse()).ExportToPdfAsync(1);

        file!.FileName.ShouldBeNull();
    }

    [Fact]
    public async Task Ui_ExportUbl_PrefersTheServerName_OverTheCallersFallback()
    {
        var result = await CreateUiService(PdfResponse("Faktura_2026001.xml")).ExportUblAsync(1, "1.xml");

        result.FileName.ShouldBe("Faktura_2026001.xml");
    }

    // ── MCP API client ───────────────────────────────────────────────────

    private static FakvioApiClient CreateMcpClient(HttpResponseMessage response) =>
        new(new HttpClient(new FixedResponseHandler(response)) { BaseAddress = new Uri("https://test.local/") });

    [Fact]
    public async Task Mcp_ExportInvoicePdf_ReturnsTheServerFileName()
    {
        var file = await CreateMcpClient(PdfResponse("Invoice_2026001.pdf")).ExportInvoicePdfAsync(7);

        file.Content.ShouldBe(new byte[] { 1, 2, 3 });
        file.FileName.ShouldBe("Invoice_2026001.pdf");
    }

    [Fact]
    public async Task Mcp_ExportInvoiceIsdoc_NoHeader_FallsBackToIdBasedName()
    {
        var file = await CreateMcpClient(PdfResponse()).ExportInvoiceIsdocAsync(7);

        file.FileName.ShouldBe("7.isdoc");
    }

    [Fact]
    public async Task Mcp_ExportInvoiceUbl_NonAsciiName_PrefersFileNameStar()
    {
        var file = await CreateMcpClient(PdfResponse(fileNameStar: "Faktura_Novák.xml")).ExportInvoiceUblAsync(7);

        file.FileName.ShouldBe("Faktura_Novák.xml");
    }
}
