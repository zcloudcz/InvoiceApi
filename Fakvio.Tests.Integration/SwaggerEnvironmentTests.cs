using System.Net;
using Fakvio.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for the Swagger environment guard (issue #233).
///
/// Swagger publishes the entire API surface — every route, every DTO shape and the
/// authentication scheme. That is exactly what an attacker wants and exactly what a
/// production host must never hand out, so <c>UseSwagger</c>/<c>UseSwaggerUI</c> in
/// <c>Program.cs</c> are registered only when the host runs in Development.
///
/// Covered acceptance criteria:
///   - Production: neither the UI (served at the root prefix) nor swagger.json exists → 404.
///   - Development: both stay exactly where developers expect them → 200.
///
/// The environment is switched per test with <see cref="WebApplicationFactory{T}.WithWebHostBuilder"/>,
/// which runs AFTER <c>FakvioFactory.ConfigureWebHost</c> and therefore overrides the
/// "Testing" environment the factory normally sets, while keeping all of its test doubles
/// (InMemory database, no background services, test JWT settings).
/// </summary>
public class SwaggerEnvironmentTests : IDisposable
{
    // Swagger UI runs with RoutePrefix = string.Empty, so the UI lives at the site root.
    private const string SwaggerUiPath   = "/index.html";
    private const string SwaggerJsonPath = "/swagger/v1/swagger.json";

    // Not an IClassFixture: each test needs its own host started under a different
    // ASPNETCORE_ENVIRONMENT, so the factory is owned (and disposed) by the test class.
    private readonly FakvioFactory _factory = new();

    /// <summary>
    /// Builds a client against a host started in the given hosting environment.
    /// </summary>
    private HttpClient CreateClientFor(string environment) =>
        _factory
            .WithWebHostBuilder(builder => builder.UseEnvironment(environment))
            .CreateClient(new WebApplicationFactoryClientOptions
            {
                // Outside Development the pipeline enables UseHttpsRedirection, which answers
                // every http:// request with a 307 and would mask the 404 under test.
                // Requesting https:// directly keeps the redirect middleware out of the way.
                BaseAddress = new Uri("https://localhost/"),
                AllowAutoRedirect = false
            });

    /// <summary>
    /// The regression this issue is about: in Production the Swagger endpoints must not exist.
    /// 404 (not 401/403) proves the middleware was never registered, rather than merely
    /// being protected by authorization that a future change could relax.
    /// </summary>
    [Fact]
    public async Task Production_SwaggerEndpoints_ReturnNotFound()
    {
        using var client = CreateClientFor("Production");

        var uiResponse   = await client.GetAsync(SwaggerUiPath);
        var jsonResponse = await client.GetAsync(SwaggerJsonPath);

        uiResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        jsonResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The developer experience must not change: Swagger stays served from the root prefix
    /// in Development, and the JSON document is the real generated OpenAPI document.
    /// </summary>
    [Fact]
    public async Task Development_SwaggerEndpoints_AreServed()
    {
        using var client = CreateClientFor("Development");

        var uiResponse   = await client.GetAsync(SwaggerUiPath);
        var jsonResponse = await client.GetAsync(SwaggerJsonPath);

        uiResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        jsonResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Sanity check that we got the generated OpenAPI document, not some other 200.
        var json = await jsonResponse.Content.ReadAsStringAsync();
        json.ShouldContain("\"openapi\"");
        json.ShouldContain("Fakvio");
    }

    public void Dispose() => _factory.Dispose();
}
