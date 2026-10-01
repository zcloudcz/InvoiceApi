using System.ComponentModel.DataAnnotations;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Feedback;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Tests real queries with isolated data: removing either ownership predicate must fail.</summary>
public class FeedbackServiceTests : IDisposable
{
    private readonly MasterDbContext _db;
    private readonly ICurrentUserService _user = Substitute.For<ICurrentUserService>();
    private readonly ITenantResolver _tenant = Substitute.For<ITenantResolver>();
    private readonly FeedbackService _service;

    public FeedbackServiceTests()
    {
        _user.GetCurrentUserId().Returns(10);
        _tenant.GetCurrentCompanyId().Returns(20);
        _db = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, _user);
        _db.User.Add(new User { Id = 10, CompanyId = 20, IsActive = true });
        _db.Client.Add(new Client { Id = 20, IsIssuer = true, RegistrationNumber = "12345678" });
        _db.SaveChanges();
        _service = new FeedbackService(_db, _user, _tenant, NullLogger<FeedbackService>.Instance);
    }

    [Fact]
    public async Task SecondaryCompanyMembership_ControlsFeedbackOwnerScope()
    {
        _db.Client.Add(new Client { Id = 21, IsIssuer = true, RegistrationNumber = "87654321" });
        _db.UserCompanyMembership.Add(new UserCompanyMembership { UserId = 10, CompanyId = 21, Role = EUserRole.User });
        await _db.SaveChangesAsync();
        _tenant.GetCurrentCompanyId().Returns(21);
        (await _service.CreateAsync(Valid())).CompanyId.ShouldBe(21);
        _db.User.Single().CompanyId.ShouldBe(20);
        _db.UserCompanyMembership.Single(m => m.CompanyId == 21).IsActive = false;
        await _db.SaveChangesAsync();
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.CreateAsync(Valid()));
    }
    private static CreateFeedbackDto Valid() => new() { Type = EFeedbackType.Idea, Subject = " A subject ", Description = " <script>inert text</script> ", Page = "/invoices?token=secret#fragment", AppVersion = "2.4" };

    [Fact]
    public async Task Create_DerivesOwnerAndNewStatusAndStripsPageSecrets()
    {
        var result = await _service.CreateAsync(Valid());
        result.UserId.ShouldBe(10);
        result.CompanyId.ShouldBe(20);
        result.Status.ShouldBe(EFeedbackStatus.New);
        result.Subject.ShouldBe("A subject");
        result.Description.ShouldBe("<script>inert text</script>");
        result.Page.ShouldBe("/invoices");
        var saved = await _db.FeedbackReport.SingleAsync();
        saved.CreatedByUserId.ShouldBe(10);
        saved.CreatedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task FeedbackProjection_IncludesReadableOwnerContext()
    {
        _db.Client.Single().CompanyName = "Acme";
        _db.User.Single().Email = "owner@example.test";
        await _db.SaveChangesAsync();
        var result = await _service.CreateAsync(Valid());
        result.CompanyName.ShouldBe("Acme");
        result.ReporterEmail.ShouldBe("owner@example.test");
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("//evil.test")]
    [InlineData("/\\evil")]
    [InlineData("/page\nnext")]
    [InlineData("relative")]
    [InlineData("/%2fhost")]
    [InlineData("/%5chost")]
    public async Task Create_RejectsUnsafePages(string page)
    {
        var input = Valid(); input.Page = page;
        await Should.ThrowAsync<ValidationException>(() => _service.CreateAsync(input));
        _db.FeedbackReport.Count().ShouldBe(0);
    }

    [Theory]
    [InlineData("Subject", 0)] [InlineData("Subject", 201)]
    [InlineData("Description", 0)] [InlineData("Description", 10001)]
    [InlineData("AppVersion", 101)] [InlineData("Page", 2049)]
    public async Task Create_RejectsInvalidLengths(string field, int length)
    {
        var input = Valid();
        typeof(CreateFeedbackDto).GetProperty(field)!.SetValue(input, field == "Page" ? "/" + new string('x', length - 1) : new string('x', length));
        await Should.ThrowAsync<ValidationException>(() => _service.CreateAsync(input));
    }

    [Fact]
    public async Task Create_AcceptsExactLimits()
    {
        var result = await _service.CreateAsync(new() { Subject = new('x', 200), Description = new('x', 10000), Page = "/" + new string('x', 2047), AppVersion = new('x', 100) });
        result.Subject.Length.ShouldBe(200);
        result.Description.Length.ShouldBe(10000);
    }

    [Fact]
    public async Task Create_RejectsUndefinedTypeAndWhitespace()
    {
        var input = Valid(); input.Type = (EFeedbackType)99;
        await Should.ThrowAsync<ValidationException>(() => _service.CreateAsync(input));
        input = Valid(); input.Subject = "  ";
        await Should.ThrowAsync<ValidationException>(() => _service.CreateAsync(input));
    }

    [Theory]
    [InlineData(null, 20)] [InlineData(10, null)] [InlineData(99, 20)] [InlineData(10, 21)]
    public async Task Create_RejectsMissingOrMismatchedIdentity(int? userId, int? companyId)
    {
        _user.GetCurrentUserId().Returns(userId); _tenant.GetCurrentCompanyId().Returns(companyId);
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.CreateAsync(Valid()));
    }

    [Fact]
    public async Task Mine_ExcludesOtherUserAndOtherCompanyBeforePagination()
    {
        var mine = await _service.CreateAsync(Valid());
        _db.FeedbackReport.AddRange(new FeedbackReport { UserId = 11, CompanyId = 20, Subject = "other user" }, new FeedbackReport { UserId = 10, CompanyId = 21, Subject = "other company" });
        await _db.SaveChangesAsync();
        var list = await _service.ListMineAsync(new() { PageSize = 1 });
        list.TotalCount.ShouldBe(1); list.Items.Single().Id.ShouldBe(mine.Id);
        foreach (var foreign in _db.FeedbackReport.Where(x => x.Id != mine.Id))
            (await _service.GetMineAsync(foreign.Id)).ShouldBeNull();
    }

    [Theory]
    [InlineData(0, 25)] [InlineData(1, 0)] [InlineData(1, 101)] [InlineData(int.MaxValue, 100)]
    public async Task List_RejectsInvalidPaging(int page, int size)
        => await Should.ThrowAsync<ValidationException>(() => _service.ListMineAsync(new() { Page = page, PageSize = size }));

    [Fact]
    public async Task List_HasStableOrderFiltersAndEmptyPages()
    {
        var first = await _service.CreateAsync(Valid());
        var second = await _service.CreateAsync(Valid());
        await _db.FeedbackReport.Where(x => x.Id == second.Id).ForEachAsync(x => x.CreatedAt = first.CreatedAt);
        await _db.SaveChangesAsync();
        var list = await _service.ListMineAsync(new() { PageSize = 100, Type = EFeedbackType.Idea, Status = EFeedbackStatus.New });
        list.Items.Select(x => x.Id).ShouldBe(new[] { second.Id, first.Id });
        (await _service.ListMineAsync(new() { Page = 2 })).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Admin_RequiresTrustedRoleAndPublishesResponseWithAudit()
    {
        var report = await _service.CreateAsync(Valid());
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.ListAllAsync(new()));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.GetAnyAsync(report.Id));
        await Should.ThrowAsync<UnauthorizedAccessException>(() => _service.UpdateAsync(report.Id, new() { Status = EFeedbackStatus.Resolved }));
        _tenant.IsSysAdmin().Returns(true);
        _db.User.Single().Role = EUserRole.SysAdmin; await _db.SaveChangesAsync();
        var updated = await _service.UpdateAsync(report.Id, new() { Status = EFeedbackStatus.Resolved, PublicResponse = " Fixed <b>text</b> " });
        updated!.PublicResponse.ShouldBe("Fixed <b>text</b>");
        updated.Status.ShouldBe(EFeedbackStatus.Resolved);
        updated.UpdatedAt.ShouldNotBeNull();
        (await _service.ListAllAsync(new() { Status = EFeedbackStatus.Resolved })).TotalCount.ShouldBe(1);
        (await _service.GetMineAsync(report.Id))!.PublicResponse.ShouldBe(updated.PublicResponse);
        _db.FeedbackReport.Single().UpdatedByUserId.ShouldBe(10);
        await Should.ThrowAsync<ValidationException>(() => _service.UpdateAsync(report.Id, new() { Status = (EFeedbackStatus)99 }));
        await Should.ThrowAsync<ValidationException>(() => _service.UpdateAsync(report.Id, new() { PublicResponse = new('x', 10001) }));
    }

    public void Dispose() => _db.Dispose();
}
