using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using Fakvio.Contracts.Dto.Import;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for <see cref="ClientCsvImportDialog"/> (N6.4).
/// Drives the whole upload → preview → confirm flow by invoking the underlying
/// MudFileUpload's FilesChanged callback with a fake IBrowserFile — bUnit can't simulate a real
/// browser file picker, but the dialog's own logic (HandleFileSelected onward) is exercised as-is.
/// </summary>
public class ClientCsvImportDialogTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public ClientCsvImportDialogTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);
    }

    /// <summary>Routes preview/confirm requests to canned JSON responses, by request path.</summary>
    private sealed class RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
    };

    private ImportApiService CreateImportService(ClientImportPreviewDto preview, ClientImportResultDto? confirmResult = null)
    {
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/clients/preview"))
            {
                return JsonResponse(HttpStatusCode.OK, preview);
            }

            return JsonResponse(HttpStatusCode.OK, confirmResult ?? new ClientImportResultDto());
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);

        return new ImportApiService(factory, NullLogger<ImportApiService>.Instance, Substitute.For<AuthenticationStateProvider>());
    }

    private sealed class FakeBrowserFile(string name, byte[] content) : IBrowserFile
    {
        public string Name => name;
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => content.Length;
        public string ContentType => "text/csv";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
            => new MemoryStream(content);
    }

    /// <summary>
    /// Renders the dialog through a real <see cref="MudDialogProvider"/> + <see cref="IDialogService"/>
    /// (rather than the component in isolation) — MudDialog only renders its content once it has a
    /// genuine MudDialogInstance ancestor providing the cascading parameters it needs.
    /// </summary>
    private IRenderedComponent<ClientCsvImportDialog> RenderDialog(ImportApiService importService)
    {
        Services.AddSingleton(importService);

        var provider = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        IDialogReference? reference = null;
        provider.InvokeAsync(() => reference = dialogService.Show<ClientCsvImportDialog>("Import"));
        provider.Render();

        return provider.FindComponent<ClientCsvImportDialog>();
    }

    private static async Task SelectFile(IRenderedComponent<ClientCsvImportDialog> dialog, string content)
    {
        var upload = dialog.FindComponent<MudFileUpload<IBrowserFile>>();
        var file = new FakeBrowserFile("clients.csv", System.Text.Encoding.UTF8.GetBytes(content));
        await dialog.InvokeAsync(() => upload.Instance.FilesChanged.InvokeAsync(file));
    }

    [Fact]
    public async Task SelectingFile_ShowsPreviewWithStatusCounts()
    {
        var preview = new ClientImportPreviewDto
        {
            Rows =
            [
                new ClientImportPreviewRowDto { RowNumber = 2, Status = EClientImportRowStatus.New, Client = new() { CompanyName = "Acme" } },
                new ClientImportPreviewRowDto { RowNumber = 3, Status = EClientImportRowStatus.Duplicate, Client = new() { CompanyName = "Beta" }, Reason = "dup" }
            ]
        };
        var dialog = RenderDialog(CreateImportService(preview));

        await SelectFile(dialog, "Název;IČO\r\nAcme;123\r\nBeta;456\r\n");

        dialog.Markup.ShouldContain("Acme");
        dialog.Markup.ShouldContain("Beta");
        // Confirm button should be enabled since NewCount > 0 — find it by its disabled state.
        var confirmButton = dialog.FindComponents<MudButton>().First(b => b.Instance.Color == Color.Primary);
        confirmButton.Instance.Disabled.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectingFile_AllDuplicates_DisablesConfirmButton()
    {
        var preview = new ClientImportPreviewDto
        {
            Rows = [new ClientImportPreviewRowDto { RowNumber = 2, Status = EClientImportRowStatus.Duplicate, Client = new() { CompanyName = "Beta" } }]
        };
        var dialog = RenderDialog(CreateImportService(preview));

        await SelectFile(dialog, "Název;IČO\r\nBeta;456\r\n");

        var confirmButton = dialog.FindComponents<MudButton>().First(b => b.Instance.Color == Color.Primary);
        confirmButton.Instance.Disabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Confirm_CreatesClients_AndShowsResultSummary()
    {
        var preview = new ClientImportPreviewDto
        {
            Rows = [new ClientImportPreviewRowDto { RowNumber = 2, Status = EClientImportRowStatus.New, Client = new() { CompanyName = "Acme" } }]
        };
        var result = new ClientImportResultDto { CreatedCount = 1, SkippedCount = 0 };
        var dialog = RenderDialog(CreateImportService(preview, result));

        await SelectFile(dialog, "Název;IČO\r\nAcme;123\r\n");

        var confirmButton = dialog.FindComponents<MudButton>().First(b => b.Instance.Color == Color.Primary);
        await dialog.InvokeAsync(() => confirmButton.Instance.OnClick.InvokeAsync());

        dialog.Markup.ShouldContain("ClientImport_ResultSummary");
    }
}
