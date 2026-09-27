using System.Text.Json;
using Fakvio.Contracts.Dto.Client;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="ClientTools.CreateClient"/> and <see cref="ClientTools.UpdateClient"/> —
/// split into their own file (N2.5) because they no longer share the "JSON string parsing" shape
/// the rest of <c>ClientTools</c>' tests (still in <c>InvoiceToolsTests.cs</c>) were written for.
/// </summary>
public class ClientToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    [Fact]
    public async Task CreateClient_NullClient_ReturnsErrorWithoutCallingApi()
    {
        var json = await ClientTools.CreateClient(_api, null!);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("client is required");
        await _api.DidNotReceive().CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateClient_PartialChange_OnlySendsProvidedFields()
    {
        _api.UpdateClientAsync(5, Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 5, CompanyName = "New Name s.r.o." });

        // Only companyName is set — the rest of UpdateClientDto stays null, meaning
        // "leave unchanged" (partial update contract, unchanged by N2.5).
        var changes = new UpdateClientDto { CompanyName = "New Name s.r.o." };

        var json = await ClientTools.UpdateClient(_api, 5, changes);

        JsonDocument.Parse(json).RootElement.GetProperty("companyName").GetString().ShouldBe("New Name s.r.o.");
        await _api.Received(1).UpdateClientAsync(
            5,
            Arg.Is<UpdateClientDto>(d => d.CompanyName == "New Name s.r.o." && d.IsVatPayer == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateClient_NullChanges_ReturnsErrorWithoutCallingApi()
    {
        var json = await ClientTools.UpdateClient(_api, 5, null!);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("changes is required");
        await _api.DidNotReceive().UpdateClientAsync(Arg.Any<long>(), Arg.Any<UpdateClientDto>(), Arg.Any<CancellationToken>());
    }
}
