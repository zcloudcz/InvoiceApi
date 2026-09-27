using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using Fakvio.UI.Shared.Models;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Regression test: the multipart preview calls on <see cref="ImportApiService"/> use the raw
/// HttpClient directly (multipart isn't supported by ApiClientBase's Get/PostAsync helpers), which
/// means they must call <c>AddAuthorizationHeaderAsync()</c> themselves — easy to forget, and
/// forgetting it means every preview request goes out with no Bearer token and gets a 401.
/// Covers both the CSV client preview (N6.3/N6.4) and the pre-existing PDF invoice preview.
/// </summary>
public class ImportApiServiceAuthTests
{
    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    // "null" deserializes fine as either List&lt;T&gt; or a DTO (both are reference types) — keeps
    // this handler reusable for both the CSV (single-object body) and PDF (array body) preview calls.
    private static HttpResponseMessage NullJsonBody() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json")
    };

    private (ImportApiService Service, CapturingHandler Handler) CreateService()
    {
        var handler = new CapturingHandler(NullJsonBody());
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        // Real CustomAuthenticationStateProvider with a stubbed localStorage that "has" a logged-in
        // session — exercises the actual GetTokenAsync() code path ApiClientBase relies on.
        var localStorage = Substitute.For<ILocalStorageService>();
        localStorage.GetItemAsStringAsync("UserSession", Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(new LoginResponse { Token = "test-jwt-token" }));

        var authApiFactory = Substitute.For<IHttpClientFactory>();
        authApiFactory.CreateClient("InvoiceAPI").Returns(new HttpClient(new CapturingHandler(NullJsonBody())));
        var authApiService = new AuthApiService(authApiFactory, NullLogger<AuthApiService>.Instance);

        var authProvider = new CustomAuthenticationStateProvider(localStorage, authApiService);
        var service = new ImportApiService(factory, NullLogger<ImportApiService>.Instance, authProvider);

        return (service, handler);
    }

    [Fact]
    public async Task PreviewClientsAsync_SendsBearerToken()
    {
        var (service, handler) = CreateService();

        await service.PreviewClientsAsync("clients.csv", "Name\r\nAcme\r\n"u8.ToArray());

        handler.LastRequest!.Headers.Authorization.ShouldNotBeNull();
        handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("test-jwt-token");
    }

    [Fact]
    public async Task PreviewAsync_Pdf_SendsBearerToken()
    {
        var (service, handler) = CreateService();

        await service.PreviewAsync([("invoice.pdf", "%PDF"u8.ToArray())], Fakvio.Contracts.Dto.Import.EImportTarget.IssuedInvoice);

        handler.LastRequest!.Headers.Authorization.ShouldNotBeNull();
        handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("test-jwt-token");
    }
}
