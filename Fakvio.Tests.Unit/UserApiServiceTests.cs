// ============================================================================
// UserApiServiceTests — coverage for PR #175 (issue #152).
//
// Last hop of the chain the fix depends on: HTTP response -> SetPasswordResultDto.
// The SetPassword page shows a green "Done, log in" purely on what this method
// returns, so the two flags must survive the deserialization unchanged, and an
// unreadable response must never be turned into "workspace ready".
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using Fakvio.Contracts.Dto.User;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="UserApiService.SetPasswordAsync"/> — the anonymous client call
/// behind the invitation / password-reset link.
/// </summary>
public class UserApiServiceTests
{
    private static readonly SetPasswordDto AnyRequest = new()
    {
        Token = "invitation-token",
        NewPassword = "MySecurePassword123"
    };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a UserApiService wired to a stub HttpClient that answers every request
    /// with <paramref name="response"/>, mirroring what DI does in production.
    /// </summary>
    private static UserApiService CreateService(HttpResponseMessage response)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(response))
        {
            BaseAddress = new Uri("https://test.local")
        };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        return new UserApiService(
            factory,
            NullLogger<UserApiService>.Instance,
            Substitute.For<AuthenticationStateProvider>());
    }

    /// <summary>Builds a response with a JSON body, camelCase like the API produces.</summary>
    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
        => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                Encoding.UTF8,
                "application/json")
        };

    /// <summary>Builds a response with a raw body, for shapes JsonSerializer would not produce.</summary>
    private static HttpResponseMessage RawJsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    // ── SetPasswordAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task SetPasswordAsync_ApiReportsReadyWorkspace_ReturnsBothFlagsSet()
    {
        var svc = CreateService(JsonResponse(HttpStatusCode.OK,
            new { passwordSet = true, workspaceReady = true }));

        var result = await svc.SetPasswordAsync(AnyRequest);

        result.PasswordSet.ShouldBeTrue();
        result.WorkspaceReady.ShouldBeTrue();
    }

    /// <summary>
    /// Regression test for issue #152 at the client boundary: a 200 response is not
    /// automatically success. The old client returned response.IsSuccessStatusCode, which
    /// collapsed exactly this case into "Done, log in" for a workspace that does not exist.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ApiReportsProvisioningFailure_KeepsWorkspaceNotReady()
    {
        var svc = CreateService(JsonResponse(HttpStatusCode.OK,
            new { passwordSet = true, workspaceReady = false }));

        var result = await svc.SetPasswordAsync(AnyRequest);

        result.PasswordSet.ShouldBeTrue();
        result.WorkspaceReady.ShouldBeFalse();
    }

    [Fact]
    public async Task SetPasswordAsync_BadRequest_ReportsPasswordNotSet()
    {
        var svc = CreateService(JsonResponse(HttpStatusCode.BadRequest,
            new { message = "Invalid or expired invitation token." }));

        var result = await svc.SetPasswordAsync(AnyRequest);

        result.PasswordSet.ShouldBeFalse();
        result.WorkspaceReady.ShouldBeFalse();
    }

    /// <summary>
    /// Defensive fallback: a 200 whose body deserializes to null still means the password
    /// was accepted, but the workspace state is unknown — and unknown is never reported
    /// as ready.
    /// </summary>
    [Fact]
    public async Task SetPasswordAsync_ResponseBodyDeserializesToNull_DoesNotClaimWorkspaceReady()
    {
        var svc = CreateService(RawJsonResponse(HttpStatusCode.OK, "null"));

        var result = await svc.SetPasswordAsync(AnyRequest);

        result.PasswordSet.ShouldBeTrue();
        result.WorkspaceReady.ShouldBeFalse();
    }

    /// <summary>HttpMessageHandler stub returning a fixed response for any request.</summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
