using System.Net;
using System.Text;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="TwoFactorApiService.VerifyTwoFactorCodeAsync"/> — RC.4 coverage:
/// a 429 from the "auth-anon" rate limiter must not be reported as "invalid code".
/// </summary>
public class TwoFactorApiServiceTests
{
    private static TwoFactorApiService CreateService(HttpResponseMessage response)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(response))
        {
            BaseAddress = new Uri("https://test.local")
        };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        return new TwoFactorApiService(
            factory,
            NullLogger<TwoFactorApiService>.Instance,
            Substitute.For<AuthenticationStateProvider>());
    }

    [Fact]
    public async Task VerifyTwoFactorCodeAsync_RateLimited_ThrowsRateLimitExceededException()
    {
        var svc = CreateService(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"message":"Too many attempts. Please try again later."}""",
                Encoding.UTF8, "application/json")
        });

        await Should.ThrowAsync<RateLimitExceededException>(
            () => svc.VerifyTwoFactorCodeAsync("session-token", "123456"));
    }

    [Fact]
    public async Task VerifyTwoFactorCodeAsync_InvalidCode_ReturnsNull_WithoutThrowing()
    {
        // A plain 401 (wrong code) must keep behaving like before RC.4.
        var svc = CreateService(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                """{"message":"Invalid or expired verification code."}""",
                Encoding.UTF8, "application/json")
        });

        var result = await svc.VerifyTwoFactorCodeAsync("session-token", "000000");

        result.ShouldBeNull();
    }

    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }
}
