using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Feedback;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>Exercises the MCP-to-HTTP boundary without a live API or database.</summary>
public class FeedbackMcpTests
{
    [Fact]
    public async Task Submit_ForwardsSharedDtoAndCancellation()
    {
        var api = Substitute.For<IFakvioApiClient>();
        var dto = new CreateFeedbackDto { Type = EFeedbackType.Idea, Subject = "Subject", Description = "Details" };
        using var cancellation = new CancellationTokenSource();
        api.CreateFeedbackAsync(dto, cancellation.Token).Returns(new FeedbackDto { Id = 42, Status = EFeedbackStatus.New });
        var result = await FeedbackTools.SubmitFeedback(api, dto, cancellation.Token);
        await api.Received(1).CreateFeedbackAsync(dto, cancellation.Token);
        using var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("status").GetString().ShouldBe("New");
    }

    [Fact]
    public async Task List_ForwardsPagingAndFilters()
    {
        var api = Substitute.For<IFakvioApiClient>();
        using var cancellation = new CancellationTokenSource();
        api.GetFeedbackAsync(Arg.Any<FeedbackFilterDto>(), cancellation.Token).Returns(new PagedResult<FeedbackDto>());
        await FeedbackTools.ListFeedback(api, 3, 100, EFeedbackType.Bug, EFeedbackStatus.Resolved, cancellation.Token);
        await api.Received(1).GetFeedbackAsync(Arg.Is<FeedbackFilterDto>(f => f.Page == 3 && f.PageSize == 100 && f.Type == EFeedbackType.Bug && f.Status == EFeedbackStatus.Resolved), cancellation.Token);
    }

    [Fact]
    public async Task Get_ForwardsIdAndCancellation()
    {
        var api = Substitute.For<IFakvioApiClient>();
        using var cancellation = new CancellationTokenSource();
        api.GetFeedbackByIdAsync(42, cancellation.Token).Returns(new FeedbackDto { Id = 42, PublicResponse = "Fixed" });
        var result = await FeedbackTools.GetFeedback(api, 42, cancellation.Token);
        await api.Received(1).GetFeedbackByIdAsync(42, cancellation.Token);
        result.ShouldContain("Fixed");
    }

    [Fact]
    public async Task Get_PropagatesCallerCancellation()
    {
        var api = Substitute.For<IFakvioApiClient>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        api.GetFeedbackByIdAsync(42, cancellation.Token).ThrowsAsync(new OperationCanceledException());
        await Should.ThrowAsync<OperationCanceledException>(() => FeedbackTools.GetFeedback(api, 42, cancellation.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "validation_error")]
    [InlineData(HttpStatusCode.Unauthorized, "unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.InternalServerError, "internal_error")]
    public async Task Get_ConvertsHttpErrorsToSafeToolErrors(HttpStatusCode status, string code)
    {
        using var handler = new RecordingHandler(status, "\"private diagnostic\"");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        var result = await FeedbackTools.GetFeedback(new FakvioApiClient(http), 42);
        using var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("error").GetString().ShouldBe(code);
        if (status == HttpStatusCode.InternalServerError) result.ShouldNotContain("private diagnostic");
    }

    [Theory]
    [InlineData("list", "GET", "/api/sysadmin/feedback?page=2&pageSize=25&type=1&status=0")]
    [InlineData("get", "GET", "/api/sysadmin/feedback/42")]
    [InlineData("update", "PATCH", "/api/sysadmin/feedback/42")]
    public async Task AdminTools_UseProtectedEndpointsAndKeepForbidden(string operation, string method, string path)
    {
        using var handler = new RecordingHandler(HttpStatusCode.Forbidden, "{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        var api = new FakvioApiClient(http);
        var result = operation switch
        {
            "list" => await FeedbackTools.ListAdminFeedback(api, 2, 25, EFeedbackType.Idea, EFeedbackStatus.New),
            "get" => await FeedbackTools.GetAdminFeedback(api, 42),
            _ => await FeedbackTools.UpdateFeedbackStatus(api, 42, new UpdateFeedbackStatusDto { Status = EFeedbackStatus.Resolved, PublicResponse = "Fixed" })
        };
        handler.Method.ShouldBe(method);
        handler.Path.ShouldBe(path);
        using var json = JsonDocument.Parse(result);
        json.RootElement.GetProperty("error").GetString().ShouldBe("forbidden");
        result.ShouldContain("SysAdmin");
    }

    [Fact]
    public async Task Submit_HttpBodyHasNoOwnerCompanyOrStatusOverride()
    {
        using var handler = new RecordingHandler(HttpStatusCode.Created, "{\"id\":42}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        await new FakvioApiClient(http).CreateFeedbackAsync(new CreateFeedbackDto { Type = EFeedbackType.Bug, Subject = "Subject", Description = "Details", Page = "/invoices", AppVersion = "2.4.0" });
        handler.Method.ShouldBe("POST");
        handler.Path.ShouldBe("/api/feedback");
        using var json = JsonDocument.Parse(handler.Body!);
        json.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(new[] { "appVersion", "description", "page", "subject", "type" });
    }

    [Fact]
    public async Task List_HttpForwardsInvalidPagingForServerValidation()
    {
        using var handler = new RecordingHandler(HttpStatusCode.OK, "{\"items\":[],\"totalCount\":0}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.invalid/") };
        await new FakvioApiClient(http).GetFeedbackAsync(new FeedbackFilterDto { Page = 0, PageSize = 101 });
        handler.Path.ShouldBe("/api/feedback?page=0&pageSize=101");
    }

    /// <summary>Captures the real HTTP request; responses are deliberately controlled by each test.</summary>
    private sealed class RecordingHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public string? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Method = request.Method.Method;
            Path = request.RequestUri!.PathAndQuery;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
