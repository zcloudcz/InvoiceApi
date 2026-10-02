using System.Net;
using System.Text;
using Fakvio.Contracts.Dto.Vies;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Tests ViesService with a fake HttpMessageHandler (no network).</summary>
public class ViesServiceTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (ViesService svc, FakeHandler handler) Create(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new FakeHandler(respond);
        var svc = new ViesService(new HttpClient(handler), new MemoryCache(new MemoryCacheOptions()), NullLogger<ViesService>.Instance);
        return (svc, handler);
    }

    [Theory]
    [InlineData("CZ12345678", "CZ", "12345678")]
    [InlineData("de 123 456 789", "DE", "123456789")]
    [InlineData("GR123456789", "EL", "123456789")]
    [InlineData("EL-123456789", "EL", "123456789")]
    public async Task Verify_NormalizesInput_AndPostsCountryAndNumber(string input, string country, string number)
    {
        var (svc, handler) = Create(_ => Task.FromResult(
            Json("{\"valid\":true,\"name\":\"ACME\",\"address\":\"Street 1\",\"requestDate\":\"2026-10-02T10:00:00Z\"}")));

        var result = await svc.VerifyAsync(input);

        result.Status.ShouldBe(EViesCheckStatus.Valid);
        result.Valid.ShouldBeTrue();
        result.Name.ShouldBe("ACME");
        handler.LastBody.ShouldBe("{\"countryCode\":\"" + country + "\",\"vatNumber\":\"" + number + "\"}");
    }

    [Fact]
    public async Task Verify_Valid_IsCached_ButInvalidIsNot()
    {
        var (svc, handler) = Create(_ => Task.FromResult(Json("{\"valid\":true,\"name\":\"---\",\"address\":\"---\"}")));
        await svc.VerifyAsync("CZ12345678");
        var second = await svc.VerifyAsync("CZ12345678");
        handler.Calls.ShouldBe(1);
        second.Name.ShouldBeNull(); // "---" placeholder normalized

        var (svc2, handler2) = Create(_ => Task.FromResult(Json("{\"valid\":false}")));
        (await svc2.VerifyAsync("CZ99999999")).Status.ShouldBe(EViesCheckStatus.Invalid);
        await svc2.VerifyAsync("CZ99999999");
        handler2.Calls.ShouldBe(2);
    }

    [Theory]
    [InlineData("MS_UNAVAILABLE")]
    [InlineData("TIMEOUT")]
    public async Task Verify_ServiceUserError_IsUnavailable_NotInvalid(string code)
    {
        var (svc, _) = Create(_ => Task.FromResult(Json("{\"valid\":false,\"userError\":\"" + code + "\"}")));
        (await svc.VerifyAsync("CZ12345678")).Status.ShouldBe(EViesCheckStatus.Unavailable);
    }

    [Fact]
    public async Task Verify_Http500_And_NetworkError_AreUnavailable()
    {
        var (svc, _) = Create(_ => Task.FromResult(Json("{}", HttpStatusCode.InternalServerError)));
        (await svc.VerifyAsync("CZ12345678")).Status.ShouldBe(EViesCheckStatus.Unavailable);

        var (svc2, _) = Create(_ => throw new HttpRequestException("boom"));
        (await svc2.VerifyAsync("CZ12345678")).Status.ShouldBe(EViesCheckStatus.Unavailable);
    }

    [Fact]
    public async Task Verify_Timeout_IsUnavailable()
    {
        var (svc, _) = Create(_ => throw new TaskCanceledException("timeout"));
        (await svc.VerifyAsync("CZ12345678")).Status.ShouldBe(EViesCheckStatus.Unavailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345678")]
    [InlineData("CZ")]
    public async Task Verify_MalformedInput_IsInvalid_WithoutCallingVies(string input)
    {
        var (svc, handler) = Create(_ => Task.FromResult(Json("{}")));
        (await svc.VerifyAsync(input)).Status.ShouldBe(EViesCheckStatus.Invalid);
        handler.Calls.ShouldBe(0);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("INVALID_INPUT")]
    public async Task Verify_InvalidUserError_GivesFriendlyMessage(string code)
    {
        var (svc, _) = Create(_ => Task.FromResult(Json("{\"valid\":false,\"userError\":\"" + code + "\"}")));
        var result = await svc.VerifyAsync("CZ12345678");
        result.Status.ShouldBe(EViesCheckStatus.Invalid);
        result.ErrorMessage.ShouldNotContain("INVALID", Case.Sensitive);
    }
}
