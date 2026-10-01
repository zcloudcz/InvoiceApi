using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

public class CompanyMcpTests
{
    [Fact]
    public async Task Context_IsIsolatedAcrossConcurrentCalls_AndRestoredOnFailure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = Enumerable.Range(1, 20).Select(id => CompanyRequestContext.RunAsync(id, async () =>
        {
            await gate.Task;
            await Task.Yield();
            return CompanyRequestContext.CompanyId;
        })).ToArray();
        gate.SetResult();
        (await Task.WhenAll(results)).ShouldBe(Enumerable.Range(1, 20).Select(x => (long?)x));
        CompanyRequestContext.CompanyId.ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => CompanyRequestContext.RunAsync<int>(42,
            () => throw new InvalidOperationException("test")));
        CompanyRequestContext.CompanyId.ShouldBeNull();
    }

    [Fact]
    public async Task AddCompany_ForwardsOperationWithoutMintingCredentials()
    {
        var api = Substitute.For<IFakvioApiClient>();
        var request = new CreateMyCompanyDto { OperationId = Guid.NewGuid(), CompanyName = "Second", RegistrationNumber = "12345678" };
        api.CreateMyCompanyAsync(request, Arg.Any<CancellationToken>()).Returns(new CompanyMembershipDto { CompanyId = 8 });
        var result = await CompanyTools.AddCompany(api, request);
        result.ShouldContain("8");
        await api.Received(1).CreateMyCompanyAsync(request, Arg.Any<CancellationToken>());
        api.ReceivedCalls().Count().ShouldBe(1);
    }

    [Fact]
    public async Task Selection_ValidatesExplicitMembership_WithoutPersistingCompany()
    {
        var api = Substitute.For<IFakvioApiClient>();
        api.GetMyCompaniesAsync(Arg.Any<CancellationToken>()).Returns([new CompanyMembershipDto { CompanyId = 8, IsProvisioned = true }]);
        var result = await CompanyTools.SelectCompany(api, 8);
        result.ShouldContain("companyId");
        CompanyRequestContext.CompanyId.ShouldBeNull();
        var denied = await CompanyTools.SelectCompany(api, 9);
        denied.ShouldContain("error");
    }
}
