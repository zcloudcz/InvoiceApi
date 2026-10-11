using System.Security.Claims;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.DownloadLink;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Which origin <c>POST /api/download-links</c> builds the link on. The link carries a bearer credential,
/// so outside Development it must be https and must come from configuration - never from the request,
/// whose scheme is "http" behind a TLS-terminating proxy.
/// </summary>
public class DownloadLinkControllerTests
{
    private readonly ILogger<DownloadLinkController> _logger = Substitute.For<ILogger<DownloadLinkController>>();

    private DownloadLinkController CreateController(string environment, string? publicBaseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DownloadLinks:PublicBaseUrl"] = publicBaseUrl })
            .Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);

        // The request as the app sees it behind the Azure edge: plain http.
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("internal-host");
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("CompanyId", "7"), new Claim(ClaimTypes.NameIdentifier, "3")], "Test"));

        return new DownloadLinkController(
            new DownloadLinkProtector(DataProtectionProvider.Create("test"), TimeSpan.FromMinutes(10)),
            Substitute.For<IServiceScopeFactory>(),
            configuration,
            env,
            new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options),
            _logger)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };
    }

    // InvoicePdf is not one of the kinds that are dry-run when the link is minted, so no services are needed.
    private static CreateDownloadLinkDto Request() => new() { Kind = EDownloadKind.InvoicePdf, Id = 42 };

    [Theory]
    [InlineData(null)]                      // not configured at all
    [InlineData("")]
    [InlineData("http://api.example.com")]  // configured, but not https
    [InlineData("api.example.com")]         // not an absolute URL
    public async Task CreateLink_OutsideDevelopment_WithoutHttpsPublicBaseUrl_Returns500AndLogsError(string? publicBaseUrl)
    {
        var controller = CreateController("Production", publicBaseUrl);

        var result = await controller.CreateLink(Request(), CancellationToken.None);

        var error = result.Result.ShouldBeOfType<ObjectResult>();
        error.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
        _logger.Received(1).Log(LogLevel.Error, Arg.Any<EventId>(), Arg.Any<object>(), null, Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task CreateLink_OutsideDevelopment_UsesTheConfiguredHttpsOrigin_NotTheRequest()
    {
        var controller = CreateController("Production", "https://api.example.com/");

        var result = await controller.CreateLink(Request(), CancellationToken.None);

        var link = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<DownloadLinkDto>();
        link.Url.ShouldStartWith("https://api.example.com/api/download/");
    }

    [Fact]
    public async Task CreateLink_InDevelopment_FallsBackToTheRequestOrigin()
    {
        // Local development runs on http://localhost and has nothing configured.
        var controller = CreateController("Development", publicBaseUrl: "");

        var result = await controller.CreateLink(Request(), CancellationToken.None);

        var link = result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeOfType<DownloadLinkDto>();
        link.Url.ShouldStartWith("http://internal-host/api/download/");
    }
}
