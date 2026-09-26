using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.McpServer;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// N2.5 point 4: proves a camelCase, string-enum argument survives the REAL SDK path —
/// <c>McpClient.CallToolAsync</c> → JSON-RPC → <c>McpServerTool.InvokeAsync</c> → the SDK's own
/// argument binding — not just a direct C# method call with an already-typed DTO.
///
/// <para>
/// Why this matters: <see cref="Fakvio.McpServer.Tools.McpToolJsonOptions.Default"/> is what the
/// SDK uses to deserialize <c>tools/call</c> arguments into typed DTO parameters (e.g.
/// <c>CreateReceivedInvoiceDto</c>). Every other test in this folder calls the tool method
/// directly in C#, which never exercises that deserialization step at all — it would stay green
/// even if the options passed to <c>WithToolsFromAssembly()</c> in <c>McpServerRegistration</c>
/// were wrong or missing (see the "must specify a TypeInfoResolver" failure that caught exactly
/// that during development of this test).
/// </para>
///
/// <para>
/// Uses a real in-process MCP session over a pair of pipes (<c>StreamServerTransport</c> /
/// <c>StreamClientTransport</c>) — the same wiring <c>Program.cs</c> uses for stdio, just
/// without a subprocess. Only the outbound HTTP to <c>Fakvio.API</c> is stubbed.
/// </para>
/// </summary>
public class McpSdkInvocationTests
{
    [Fact]
    public async Task CreateReceivedInvoice_PaymentMethodAsCamelCaseString_DeserializesCorrectly()
    {
        await using var session = await McpSdkTestSession.StartAsync();

        var result = await session.Client.CallToolAsync(
            "create_received_invoice",
            new Dictionary<string, object?>
            {
                ["invoice"] = new
                {
                    supplierId = 3,
                    paymentMethod = "BankTransfer", // camelCase property, string enum value
                    items = new[]
                    {
                        new { description = "Hosting", quantity = 1, unit = "pcs", unitPrice = 100 }
                    }
                }
            }!,
            cancellationToken: session.Deadline.Token);

        result.IsError.ShouldNotBe(true,
            $"tool call failed: {string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        session.Api.LastReceivedInvoiceRequest.ShouldNotBeNull();
        session.Api.LastReceivedInvoiceRequest!.PaymentMethod.ShouldBe(Fakvio.Domain.Enums.EPaymentMethod.BankTransfer);
    }

    /// <summary>
    /// A "no more JSON-string parameters" sibling to the enum test above: the SAME real SDK path
    /// (not a direct call) binding a whole nested object array (<c>items</c>) onto
    /// <c>List&lt;CreateReceivedInvoiceItemDto&gt;</c> from camelCase JSON.
    /// </summary>
    [Fact]
    public async Task CreateReceivedInvoice_NestedItemsArray_BindsOntoTypedList()
    {
        await using var session = await McpSdkTestSession.StartAsync();

        await session.Client.CallToolAsync(
            "create_received_invoice",
            new Dictionary<string, object?>
            {
                ["invoice"] = new
                {
                    supplierId = 3,
                    items = new[]
                    {
                        new { description = "Hosting", quantity = 2, unit = "pcs", unitPrice = 500, vatRatePercentage = 21 }
                    }
                }
            }!,
            cancellationToken: session.Deadline.Token);

        var received = session.Api.LastReceivedInvoiceRequest;
        received.ShouldNotBeNull();
        received!.Items.ShouldHaveSingleItem();
        received.Items[0].Description.ShouldBe("Hosting");
        received.Items[0].VatRatePercentage.ShouldBe(21m);
    }

    /// <summary>
    /// N2.5 typed-DTO parameter coverage, Codex review follow-up: <c>create_client</c> takes
    /// <c>CreateClientDto</c> directly (nested address/contact/bank account arrays, a string enum
    /// on the contact type) — the same "no more JSON strings" change as
    /// <c>create_received_invoice</c> above, exercised through the real SDK path too.
    /// </summary>
    [Fact]
    public async Task CreateClient_NestedArraysAndEnum_DeserializeCorrectly()
    {
        await using var session = await McpSdkTestSession.StartAsync();

        var result = await session.Client.CallToolAsync(
            "create_client",
            new Dictionary<string, object?>
            {
                ["client"] = new
                {
                    companyName = "Acme s.r.o.",
                    address = new[]
                    {
                        new { addressType = "Billing", street = "Main 1", city = "Praha", postalCode = "11000", country = "CZ" }
                    },
                    contact = new[]
                    {
                        new { contactType = "Email", contactValue = "info@acme.cz" } // string enum value
                    }
                }
            }!,
            cancellationToken: session.Deadline.Token);

        result.IsError.ShouldNotBe(true,
            $"tool call failed: {string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        session.Api.LastCreateClientRequest.ShouldNotBeNull();
        session.Api.LastCreateClientRequest!.CompanyName.ShouldBe("Acme s.r.o.");
        session.Api.LastCreateClientRequest.Contact.ShouldHaveSingleItem();
        session.Api.LastCreateClientRequest.Contact[0].ContactValue.ShouldBe("info@acme.cz");
    }

    /// <summary>N2.5: <c>update_client</c> takes <c>UpdateClientDto</c> directly — a partial update.</summary>
    [Fact]
    public async Task UpdateClient_PartialTypedDto_DeserializesCorrectly()
    {
        await using var session = await McpSdkTestSession.StartAsync();

        var result = await session.Client.CallToolAsync(
            "update_client",
            new Dictionary<string, object?>
            {
                ["clientId"] = 5,
                ["changes"] = new { isVatPayer = true }
            }!,
            cancellationToken: session.Deadline.Token);

        result.IsError.ShouldNotBe(true,
            $"tool call failed: {string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        session.Api.LastUpdateClientId.ShouldBe(5L);
        session.Api.LastUpdateClientRequest!.IsVatPayer.ShouldBe(true);
    }

    /// <summary>
    /// N2.4/N2.5: <c>create_invoice</c> takes a typed <c>items</c> list (nested objects) plus a
    /// string enum <c>paymentMethod</c> ("BankTransfer") the tool parses itself — proves the whole
    /// parameter set round-trips through the real SDK binding, not just the DTO-typed ones.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_TypedItemsAndPaymentMethodEnum_DeserializeCorrectly()
    {
        await using var session = await McpSdkTestSession.StartAsync();

        var result = await session.Client.CallToolAsync(
            "create_invoice",
            new Dictionary<string, object?>
            {
                ["clientId"] = 1,
                ["paymentMethod"] = "BankTransfer",
                ["items"] = new[]
                {
                    new { description = "Web development", quantity = 10, unit = "hrs", unitPrice = 1500 }
                }
            }!,
            cancellationToken: session.Deadline.Token);

        result.IsError.ShouldNotBe(true,
            $"tool call failed: {string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        session.Api.LastCreateInvoiceRequest.ShouldNotBeNull();
        session.Api.LastCreateInvoiceRequest!.PaymentMethod.ShouldBe(Fakvio.Domain.Enums.EPaymentMethod.BankTransfer);
        session.Api.LastCreateInvoiceRequest.InvoiceItem.ShouldHaveSingleItem();
        session.Api.LastCreateInvoiceRequest.InvoiceItem[0].Description.ShouldBe("Web development");
    }

    /// <summary>N2.5: <c>create_invoice_from_template</c> takes <c>CreateInvoiceFromTemplateDto</c> directly.</summary>
    [Fact]
    public async Task CreateInvoiceFromTemplate_TypedOptionsDto_DeserializesCorrectly()
    {
        await using var session = await McpSdkTestSession.StartAsync();

        var result = await session.Client.CallToolAsync(
            "create_invoice_from_template",
            new Dictionary<string, object?>
            {
                ["templateId"] = 5,
                ["options"] = new { clientId = 10, autoComplete = false }
            }!,
            cancellationToken: session.Deadline.Token);

        result.IsError.ShouldNotBe(true,
            $"tool call failed: {string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        session.Api.LastCreateInvoiceFromTemplateId.ShouldBe(5L);
        session.Api.LastCreateInvoiceFromTemplateRequest!.ClientId.ShouldBe(10L);
    }

    /// <summary>
    /// Minimal in-process MCP session: a real <see cref="McpServerRegistration.AddFakvioMcpServer"/>
    /// host talking to a real <see cref="McpClient"/> over an in-memory pipe pair, with only the
    /// outbound HTTP call to Fakvio.API stubbed.
    /// </summary>
    private sealed class McpSdkTestSession : IAsyncDisposable
    {
        private readonly IHost _host;

        private McpSdkTestSession(IHost host, McpClient client, StubApiHandler api)
        {
            _host = host;
            Client = client;
            Api = api;
        }

        public McpClient Client { get; }
        public StubApiHandler Api { get; }

        /// <summary>Bounds every wait — a hung pipe must fail the test, not the whole suite.</summary>
        public CancellationTokenSource Deadline { get; } = new(TimeSpan.FromSeconds(30));

        public static async Task<McpSdkTestSession> StartAsync()
        {
            // client -> server and server -> client each need their own duplex pipe.
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();

            var api = new StubApiHandler();

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddSingleton(Substitute.For<IApiTokenProvider>());
            builder.Services.ConfigureHttpClientDefaults(http =>
                http.ConfigurePrimaryHttpMessageHandler(() => api));

            builder.Services
                .AddFakvioMcpServer(new McpServerSettings { ApiBaseUrl = "https://api.test.invalid" })
                .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());

            var host = builder.Build();
            await host.StartAsync();

            var clientTransport = new ModelContextProtocol.Protocol.StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(),
                serverOutput: serverToClient.Reader.AsStream(),
                loggerFactory: NullLoggerFactory.Instance);

            using var connectDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var client = await McpClient.CreateAsync(clientTransport, cancellationToken: connectDeadline.Token);

            return new McpSdkTestSession(host, client, api);
        }

        public async ValueTask DisposeAsync()
        {
            Deadline.Dispose();
            await Client.DisposeAsync();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    /// <summary>
    /// Stands in for Fakvio.API: answers the two endpoints this test exercises and records the
    /// last received-invoice request so the test can assert on what actually reached "the API".
    /// </summary>
    private sealed class StubApiHandler : HttpMessageHandler
    {
        private static readonly Regex CreateInvoiceFromTemplatePath =
            new(@"/api/invoicetemplate/(?<id>\d+)/create-invoice$", RegexOptions.Compiled);

        public CreateReceivedInvoiceDto? LastReceivedInvoiceRequest { get; private set; }
        public CreateClientDto? LastCreateClientRequest { get; private set; }
        public long? LastUpdateClientId { get; private set; }
        public UpdateClientDto? LastUpdateClientRequest { get; private set; }
        public CreateInvoiceDto? LastCreateInvoiceRequest { get; private set; }
        public long? LastCreateInvoiceFromTemplateId { get; private set; }
        public CreateInvoiceFromTemplateDto? LastCreateInvoiceFromTemplateRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/api/currency/active", StringComparison.Ordinal))
            {
                return Json(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
            }

            if (path.EndsWith("/api/client/issuer", StringComparison.Ordinal))
            {
                return Json(new ClientDto { Id = 2, CompanyName = "My Company", IsVatPayer = false });
            }

            if (path.EndsWith("/api/received-invoice", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Post)
            {
                LastReceivedInvoiceRequest =
                    await request.Content!.ReadFromJsonAsync<CreateReceivedInvoiceDto>(cancellationToken);
                return Json(new Fakvio.Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto { Id = 1 });
            }

            if (path.EndsWith("/api/client", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                LastCreateClientRequest = await request.Content!.ReadFromJsonAsync<CreateClientDto>(cancellationToken);
                return Json(new ClientDto { Id = 10, CompanyName = LastCreateClientRequest?.CompanyName ?? "" });
            }

            if (request.Method == HttpMethod.Put && path.Contains("/api/client/", StringComparison.Ordinal))
            {
                LastUpdateClientId = long.Parse(path[(path.LastIndexOf('/') + 1)..]);
                LastUpdateClientRequest = await request.Content!.ReadFromJsonAsync<UpdateClientDto>(cancellationToken);
                return Json(new ClientDto { Id = LastUpdateClientId.Value });
            }

            if (path.EndsWith("/api/invoice", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
            {
                LastCreateInvoiceRequest = await request.Content!.ReadFromJsonAsync<CreateInvoiceDto>(cancellationToken);
                return Json(new InvoiceDto { Id = 100 });
            }

            var templateMatch = CreateInvoiceFromTemplatePath.Match(path);
            if (templateMatch.Success && request.Method == HttpMethod.Post)
            {
                LastCreateInvoiceFromTemplateId = long.Parse(templateMatch.Groups["id"].Value);
                LastCreateInvoiceFromTemplateRequest =
                    await request.Content!.ReadFromJsonAsync<CreateInvoiceFromTemplateDto>(cancellationToken);
                return Json(new InvoiceDto { Id = 200 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }
}
