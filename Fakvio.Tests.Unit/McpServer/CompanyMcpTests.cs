using Fakvio.Contracts.Dto.CompanyMembership;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

public class CompanyMcpTests
{
    [Fact]
    public async Task MembershipAdministrationForwardsExactTargetAndPatch()
    {
        var api = Substitute.For<IFakvioApiClient>();
        var change = new UpdateCompanyMembershipDto { Role = Fakvio.Domain.Enums.EUserRole.User, IsActive = false };
        api.GetUserCompanyMembershipsAsync(7, Arg.Any<CancellationToken>()).Returns([new ManagedCompanyMembershipDto { CompanyId = 8, IsActive = true }]);
        api.UpdateUserCompanyMembershipAsync(7, 8, change, Arg.Any<CancellationToken>()).Returns(new ManagedCompanyMembershipDto { CompanyId = 8, IsActive = false });
        (await CompanyTools.ListUserCompanyMemberships(api, 7)).ShouldContain("companyId");
        (await CompanyTools.UpdateUserCompanyMembership(api, 7, 8, change)).ShouldContain("false");
        await api.Received(1).UpdateUserCompanyMembershipAsync(7, 8, change, Arg.Any<CancellationToken>());
        api.ReceivedCalls().Count().ShouldBe(2);
    }

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
