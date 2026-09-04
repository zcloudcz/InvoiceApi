using System.Text.Json;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for MCP <see cref="ReadinessTools"/> — the tool an external AI client (Claude
/// Desktop / Claude Code) uses to ask what the company setup is still missing.
///
/// Junior note: the tool holds no logic; it relays to <c>GET /api/readiness</c> through
/// <see cref="IFakvioApiClient"/>, which is substituted here. So what is tested is the
/// relay contract: the issuer filter reaches the API, the report is serialized in a shape
/// the AI can read, and neither a missing issuer nor a dead API throws at the AI client.
/// </summary>
public class ReadinessToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    [Fact]
    public async Task GetReadiness_SerializesTheReport_IncludingSeverityAndFixRoute()
    {
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto
            {
                Issues =
                [
                    new ReadinessIssueDto
                    {
                        Code = ReadinessCodes.IssuerBankAccountMissing,
                        Severity = EReadinessSeverity.Blocking,
                        MissingFields = ["BankAccount"],
                        FixRoute = "/my-company",
                        IssuerId = 7,
                        IssuerName = "ACME s.r.o."
                    }
                ]
            });

        var json = await ReadinessTools.GetReadiness(_api);

        var root = JsonDocument.Parse(json).RootElement;
        // isReady is computed from the issues — a blocking one must flip it to false.
        root.GetProperty("isReady").GetBoolean().ShouldBeFalse();

        var issue = root.GetProperty("issues")[0];
        issue.GetProperty("code").GetString().ShouldBe("ISSUER_BANK_ACCOUNT_MISSING");
        // Serialized as a name, not as "1" — the AI client has no enum table to look it up in.
        issue.GetProperty("severity").GetString().ShouldBe("Blocking");
        issue.GetProperty("fixRoute").GetString().ShouldBe("/my-company");
        issue.GetProperty("issuerName").GetString().ShouldBe("ACME s.r.o.");
        issue.GetProperty("missingFields")[0].GetString().ShouldBe("BankAccount");
    }

    [Fact]
    public async Task GetReadiness_CompleteSetup_ReturnsReadyWithNoIssues()
    {
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        var json = await ReadinessTools.GetReadiness(_api);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("isReady").GetBoolean().ShouldBeTrue();
        root.GetProperty("issues").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task GetReadiness_WithoutIssuerId_AsksForTheWholeTenant()
    {
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        await ReadinessTools.GetReadiness(_api);

        await _api.Received(1).GetReadinessAsync(null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReadiness_WithIssuerId_PassesItToTheApi()
    {
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());

        await ReadinessTools.GetReadiness(_api, issuerId: 42);

        await _api.Received(1).GetReadinessAsync(42L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetReadiness_PassesTheCallersCancellationTokenToTheApi()
    {
        // The MCP host cancels a tool call when the user aborts the turn. A tool that swallows
        // the token keeps the HTTP call alive on a conversation nobody is listening to any more.
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new ReadinessReportDto());
        using var cts = new CancellationTokenSource();

        await ReadinessTools.GetReadiness(_api, issuerId: 42, ct: cts.Token);

        // Asserted on the exact token, not Arg.Any — that is what makes `default` a failure.
        await _api.Received(1).GetReadinessAsync(42L, cts.Token);
    }

    [Fact]
    public async Task GetReadiness_UnknownIssuerId_ReturnsJsonError()
    {
        // The client turns the API's 404 into null; the tool must name the reason instead
        // of reporting "no issues found", which would read as "everything is fine".
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns((ReadinessReportDto?)null);

        var json = await ReadinessTools.GetReadiness(_api, issuerId: 999);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldContain("999");
        root.TryGetProperty("isReady", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task GetReadiness_NullReportWithoutIssuerId_StillBlamesAMissingIssuer()
    {
        // Characterization, not endorsement. The null branch is written for "that issuerId is
        // not in the tenant", but it does not check that an issuerId was actually asked for,
        // so a tenant-wide null renders an empty ID and a diagnosis about an issuer nobody
        // named. Unreachable today — the client only maps 404 to null when an issuerId was
        // given (see FakvioApiClientTests.GetReadinessAsync_TenantWide404_Throws_*). This test
        // exists so that if that contract ever loosens, the wrong message shows up here first
        // instead of in front of a user.
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns((ReadinessReportDto?)null);

        var json = await ReadinessTools.GetReadiness(_api);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldBe("Issuer with ID  not found.");
    }

    [Fact]
    public async Task GetReadiness_OnApiError_ReturnsJsonError_InsteadOfThrowing()
    {
        // An exception crossing the MCP boundary kills the tool call for the AI client;
        // every tool in this project answers with a sanitized { error, message } object
        // instead — never the raw exception message (issue #279).
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var json = await ReadinessTools.GetReadiness(_api);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        root.GetProperty("message").GetString().ShouldNotContain("API unavailable");
    }

    [Fact]
    public async Task GetReadiness_PropagatesCancellation_WhenTheCallerCancelled()
    {
        // A request the caller cancelled is not a domain error — the tool must let it
        // bubble out instead of turning it into a fake "error" JSON result (issue #279).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReadinessTools.GetReadiness(_api, ct: cts.Token));
    }

    [Fact]
    public async Task GetReadiness_ReturnsSanitizedError_OnHttpClientTimeout()
    {
        // HttpClient throws TaskCanceledException (a subclass of OperationCanceledException)
        // on its OWN timeout, and then the caller's token was never cancelled. That is an
        // API-side failure, not a cancellation, so it has to come back as sanitized JSON:
        // an MCP tool that throws kills the whole call (issue #279).
        using var cts = new CancellationTokenSource();
        _api.GetReadinessAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));

        var json = await ReadinessTools.GetReadiness(_api, ct: cts.Token);

        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        root.GetProperty("message").GetString().ShouldNotContain("HttpClient.Timeout");
    }
}
