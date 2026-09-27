using System.Text;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Infrastructure.Import;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the client-CSV-import endpoints on <see cref="ImportController"/> (N6.3).
/// Uses NSubstitute mocks for both import services, so these are pure HTTP-layer tests.
/// </summary>
public class ImportControllerClientTests
{
    private readonly IInvoiceImportService _invoiceImportService = Substitute.For<IInvoiceImportService>();
    private readonly IClientCsvImportService _clientCsvImportService = Substitute.For<IClientCsvImportService>();

    private ImportController CreateController() =>
        new(_invoiceImportService, _clientCsvImportService, Substitute.For<ILogger<ImportController>>());

    private static IFormFile MakeFile(string content, string fileName = "clients.csv")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", fileName);
    }

    [Fact]
    public async Task PreviewClients_NoFile_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.PreviewClients(null, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task PreviewClients_ValidFile_ReturnsPreviewFromService()
    {
        var controller = CreateController();
        var expected = new ClientImportPreviewDto
        {
            Rows = [new ClientImportPreviewRowDto { RowNumber = 2, Status = EClientImportRowStatus.New }]
        };
        _clientCsvImportService.PreviewAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await controller.PreviewClients(MakeFile("Název;IČO\nAcme;123\n"), CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(expected);
    }

    [Fact]
    public async Task PreviewClients_FileOverSizeLimit_ReturnsBadRequest_WithoutCallingService()
    {
        var controller = CreateController();
        // A FormFile whose declared Length exceeds the limit — content itself doesn't need to be
        // that big, this exercises the explicit file.Length check, not the parser's own byte-copy loop.
        var oversized = new FormFile(new MemoryStream([1]), 0, CsvTable.MaxFileSizeBytes + 1, "file", "big.csv");

        var result = await controller.PreviewClients(oversized, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await _clientCsvImportService.DidNotReceive().PreviewAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PreviewClients_CsvParseException_ReturnsBadRequestWithMessage()
    {
        var controller = CreateController();
        _clientCsvImportService.PreviewAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ClientImportPreviewDto>(new CsvParseException("The file is empty.")));

        var result = await controller.PreviewClients(MakeFile(""), CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ConfirmClients_EmptyList_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.ConfirmClients(new ClientImportConfirmDto { Clients = [] }, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ConfirmClients_MoreThanMaxRowCount_ReturnsBadRequest_WithoutCallingService()
    {
        var controller = CreateController();
        var request = new ClientImportConfirmDto
        {
            Clients = Enumerable.Range(0, CsvTable.MaxRowCount + 1)
                .Select(i => new CreateClientDto { CompanyName = $"Co {i}" })
                .ToList()
        };

        var result = await controller.ConfirmClients(request, CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        await _clientCsvImportService.DidNotReceive().ConfirmAsync(Arg.Any<ClientImportConfirmDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConfirmClients_ValidRequest_ReturnsResultFromService()
    {
        var controller = CreateController();
        var request = new ClientImportConfirmDto { Clients = [new CreateClientDto { CompanyName = "Acme" }] };
        var expected = new ClientImportResultDto { CreatedCount = 1, CreatedClientIds = [7] };
        _clientCsvImportService.ConfirmAsync(request, Arg.Any<CancellationToken>()).Returns(expected);

        var result = await controller.ConfirmClients(request, CancellationToken.None);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        ok.Value.ShouldBe(expected);
    }
}
