using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.Currency;
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
        public CreateReceivedInvoiceDto? LastReceivedInvoiceRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/api/currency/active", StringComparison.Ordinal))
            {
                return Json(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
            }

            if (path.EndsWith("/api/received-invoice", StringComparison.Ordinal) &&
                request.Method == HttpMethod.Post)
            {
                LastReceivedInvoiceRequest =
                    await request.Content!.ReadFromJsonAsync<CreateReceivedInvoiceDto>(cancellationToken);
                return Json(new Fakvio.Contracts.Dto.ReceivedInvoice.ReceivedInvoiceDto { Id = 1 });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T payload) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }
}
