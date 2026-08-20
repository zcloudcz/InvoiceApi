using System.Net;
using System.Text;
using System.Text.Json;
using Fakvio.UI.Shared.Models;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="AuthApiService"/> — focused on the error-forwarding behavior
/// added when the service was migrated onto <see cref="ApiClientBase"/>.
///
/// These tests verify:
///   1. Happy paths still work (login returns token, register returns response).
///   2. Failed login returns null AND forwards a Warning to the server log (audit signal).
///   3. Transport exception during login returns null AND forwards an Error.
///   4. Failed registration throws InvalidOperationException AND forwards a Warning.
///   5. ValidateToken: non-success is routine (expired token) — NOT forwarded;
///      transport exception IS forwarded as Error.
///   6. No IClientLogger wired (test hosts) → no crash, graceful degradation.
/// </summary>
public class AuthApiServiceTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an AuthApiService wired to a stub HttpClient and a substituted IClientLogger,
    /// mirroring what AddApiClient&lt;AuthApiService&gt; does in production DI.
    /// </summary>
    private static (AuthApiService Service, IClientLogger ClientLogger) CreateService(
        HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        var clientLogger = Substitute.For<IClientLogger>();
        clientLogger.LogAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        var service = new AuthApiService(factory, NullLogger<AuthApiService>.Instance);
        service.WithClientLogger(clientLogger);
        return (service, clientLogger);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                Encoding.UTF8, "application/json")
        };

    // ── LoginAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LoginAsync_Success_ReturnsResponse_AndDoesNotForwardToServerLog()
    {
        var (svc, clientLogger) = CreateService(new StubHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, new { token = "jwt-token", email = "a@b.cz" })));

        var result = await svc.LoginAsync(new LoginRequest { Email = "a@b.cz", Password = "pw" });

        result.ShouldNotBeNull();
        result.Token.ShouldBe("jwt-token");
        await clientLogger.DidNotReceiveWithAnyArgs()
            .LogAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task LoginAsync_Unauthorized_ReturnsNull_AndForwardsWarning()
    {
        var (svc, clientLogger) = CreateService(new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var result = await svc.LoginAsync(new LoginRequest { Email = "a@b.cz", Password = "bad" });

        result.ShouldBeNull();
        // Failed login = Warning in AppLog (security/audit signal, not a system error).
        await clientLogger.Received(1).LogAsync(
            "Warning",
            Arg.Is<string>(m => m.Contains("/api/auth/login") && m.Contains("401")),
            "AuthApiService",
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task LoginAsync_TransportException_ReturnsNull_AndForwardsError()
    {
        var (svc, clientLogger) = CreateService(
            new ThrowingHttpMessageHandler(new HttpRequestException("connection refused")));

        var result = await svc.LoginAsync(new LoginRequest { Email = "a@b.cz", Password = "pw" });

        result.ShouldBeNull();
        await clientLogger.Received(1).LogAsync(
            "Error",
            Arg.Is<string>(m => m.Contains("/api/auth/login")),
            Arg.Any<string>(),
            Arg.Is<string?>(e => e != null && e.Contains("connection refused")),
            Arg.Any<string?>());
    }

    [Fact]
    public async Task LoginAsync_WithoutClientLogger_DoesNotThrow()
    {
        // Test hosts don't wire IClientLogger — forwarding must degrade silently.
        var httpClient = new HttpClient(new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)))
        { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        var svc = new AuthApiService(factory, NullLogger<AuthApiService>.Instance);

        var result = await svc.LoginAsync(new LoginRequest { Email = "a@b.cz", Password = "pw" });

        result.ShouldBeNull();
    }

    // ── RegisterAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterAsync_Success_ReturnsResponse()
    {
        var (svc, clientLogger) = CreateService(new StubHttpMessageHandler(
            JsonResponse(HttpStatusCode.OK, new { userId = 1, email = "a@b.cz" })));

        var result = await svc.RegisterAsync(new RegisterRequest { Email = "a@b.cz" });

        result.ShouldNotBeNull();
        result.UserId.ShouldBe(1);
        await clientLogger.DidNotReceiveWithAnyArgs()
            .LogAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task RegisterAsync_BadRequest_Throws_AndForwardsWarning_WithErrorBody()
    {
        var (svc, clientLogger) = CreateService(new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("Email already registered.")
            }));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => svc.RegisterAsync(new RegisterRequest { Email = "a@b.cz" }));

        ex.Message.ShouldBe("Email already registered.");
        // The server-returned error body travels in the exception column for diagnostics.
        await clientLogger.Received(1).LogAsync(
            "Warning",
            Arg.Is<string>(m => m.Contains("/api/auth/register") && m.Contains("400")),
            "AuthApiService",
            "Email already registered.",
            Arg.Any<string?>());
    }

    [Fact]
    public async Task RegisterAsync_TransportException_Throws_AndForwardsError()
    {
        var (svc, clientLogger) = CreateService(
            new ThrowingHttpMessageHandler(new HttpRequestException("timeout")));

        await Should.ThrowAsync<InvalidOperationException>(
            () => svc.RegisterAsync(new RegisterRequest { Email = "a@b.cz" }));

        await clientLogger.Received(1).LogAsync(
            "Error",
            Arg.Is<string>(m => m.Contains("/api/auth/register")),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    // ── ValidateTokenAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateTokenAsync_Unauthorized_ReturnsFalse_WithoutForwarding()
    {
        // Expired token on app start is routine — must NOT spam AppLog.
        var (svc, clientLogger) = CreateService(new StubHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var result = await svc.ValidateTokenAsync("expired");

        result.ShouldBeFalse();
        await clientLogger.DidNotReceiveWithAnyArgs()
            .LogAsync(default!, default!, default, default, default);
    }

    [Fact]
    public async Task ValidateTokenAsync_TransportException_ReturnsFalse_AndForwardsError()
    {
        var (svc, clientLogger) = CreateService(
            new ThrowingHttpMessageHandler(new HttpRequestException("dns failure")));

        var result = await svc.ValidateTokenAsync("token");

        result.ShouldBeFalse();
        await clientLogger.Received(1).LogAsync(
            "Error",
            Arg.Is<string>(m => m.Contains("/api/auth/validate")),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<string?>());
    }

    // ── Stub helpers ──────────────────────────────────────────────────────────

    /// <summary>HttpMessageHandler stub returning a fixed response for any request.</summary>
    private sealed class StubHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    /// <summary>HttpMessageHandler stub throwing the given exception on every request.</summary>
    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
