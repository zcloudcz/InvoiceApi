using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Webhook;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Outbound webhooks (DEVGUIDE §4.15): signature, SSRF guard, retry schedule, publisher,
/// dispatcher with a fake HTTP handler, payment-triggered events and tenant isolation.
/// </summary>
public class WebhookTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly ICredentialProtector _protector = Substitute.For<ICredentialProtector>();
    private readonly FakeHandler _handler = new();
    private readonly IHttpClientFactory _httpFactory = Substitute.For<IHttpClientFactory>();
    private long _invoiceId;

    public WebhookTests()
    {
        _context = NewContext();
        // Reversible fake "encryption" so tests can see that the stored value is not the plaintext.
        _protector.Encrypt(Arg.Any<string?>()).Returns(ci => "enc:" + ci.Arg<string?>());
        _protector.Decrypt(Arg.Any<string?>()).Returns(ci => ci.Arg<string?>()!.Substring(4));
        _httpFactory.CreateClient(WebhookDispatchService.HttpClientName).Returns(_ => new HttpClient(_handler, disposeHandler: false));
        _invoiceId = SeedInvoice(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TenantDbContext NewContext() => new(
        new DbContextOptionsBuilder<TenantDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options)
    { Schema = "tenant_42" };

    private static long SeedInvoice(TenantDbContext ctx)
    {
        var czk = new Currency { Code = "CZK", Name = "Koruna", Symbol = "Kč", IsActive = true, SortOrder = 1 };
        var issuer = new Client { RegistrationNumber = "11111111", CompanyName = "Issuer", IsIssuer = true, IsActive = true };
        var client = new Client { RegistrationNumber = "22222222", CompanyName = "Client", IsIssuer = false, IsActive = true };
        ctx.Currency.Add(czk);
        ctx.Client.AddRange(issuer, client);
        ctx.SaveChanges();
        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "2026001",
            IssueDate = DateTime.UtcNow,
            DueDate = DateTime.UtcNow.AddDays(14),
            IssuerId = issuer.Id,
            ClientId = client.Id,
            VariableSymbol = "2026001",
            TotalBeforeVat = 1000m,
            TotalWithVat = 1000m,
            CurrencyId = czk.Id,
            InvoiceItem = new List<InvoiceItem>(),
        };
        ctx.Invoice.Add(invoice);
        ctx.SaveChanges();
        return invoice.Id;
    }

    private WebhookSubscription AddSubscription(TenantDbContext ctx, params string[] events)
    {
        var sub = new WebhookSubscription
        {
            Url = "https://example.com/hook",
            Events = events.ToList(),
            SecretEncrypted = "enc:topsecret",
            IsActive = true,
        };
        ctx.WebhookSubscription.Add(sub);
        ctx.SaveChanges();
        return sub;
    }

    private WebhookPublisher Publisher(TenantDbContext ctx) =>
        new(ctx, Substitute.For<ILogger<WebhookPublisher>>());

    private WebhookDispatchService Dispatcher(TenantDbContext ctx) =>
        new(ctx, _protector, _httpFactory, Substitute.For<ILogger<WebhookDispatchService>>());

    // ─── Signature ────────────────────────────────────────────────────────

    [Fact]
    public void Signer_MatchesIndependentHmacVector()
    {
        // Vector computed with Python: hmac.new(b"secret", b'1700000000.{"a":1}', sha256).hexdigest()
        WebhookSigner.Sign("secret", 1700000000, "{\"a\":1}")
            .ShouldBe("v1=49f24e537407743fa4a0242bb63b94b9a47ee99cbbe071ccd8a22550ae411686");
    }

    [Fact]
    public void Signer_TimestampIsPartOfSignature()
    {
        WebhookSigner.Sign("s", 1, "body").ShouldNotBe(WebhookSigner.Sign("s", 2, "body"));
    }

    [Fact]
    public void GenerateSecret_Is32RandomBytes()
    {
        var a = WebhookSigner.GenerateSecret();
        Convert.FromBase64String(a).Length.ShouldBe(32);
        WebhookSigner.GenerateSecret().ShouldNotBe(a);
    }

    // ─── SSRF ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]   // cloud metadata
    [InlineData("100.64.0.1", true)]        // CGNAT
    [InlineData("100.128.0.1", false)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]         // multicast
    [InlineData("::1", true)]
    [InlineData("fc00::1", true)]           // ULA
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", true)]           // link-local
    [InlineData("ff02::1", true)]           // multicast
    [InlineData("::ffff:127.0.0.1", true)]  // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void IsBlockedAddress_Table(string ip, bool blocked)
    {
        WebhookUrlGuard.IsBlockedAddress(IPAddress.Parse(ip)).ShouldBe(blocked);
    }

    [Theory]
    [InlineData("https://example.com/h", "Production", true)]
    [InlineData("http://example.com/h", "Production", false)]
    [InlineData("http://localhost:5000/h", "Production", false)]
    [InlineData("http://localhost:5000/h", "Development", true)]
    [InlineData("http://example.com/h", "Development", false)]
    [InlineData("ftp://example.com/h", "Development", false)]
    public void ValidateScheme_Table(string url, string env, bool allowed)
    {
        var hostEnv = Substitute.For<IHostEnvironment>();
        hostEnv.EnvironmentName.Returns(env);
        var act = () => WebhookUrlGuard.ValidateScheme(new Uri(url), hostEnv);
        if (allowed) act(); else Should.Throw<InvalidOperationException>(act);
    }

    [Fact]
    public async Task ConnectCallback_RefusesLoopbackTarget()
    {
        // Real socket handler with the guard wired in, pointed at a literal loopback IP: the
        // connect must be refused before any TCP connection is opened.
        WebhookUrlGuard.AllowLoopback = false;
        using var handler = new SocketsHttpHandler { ConnectCallback = WebhookUrlGuard.ConnectCallback, AllowAutoRedirect = false };
        using var client = new HttpClient(handler);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/"));
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    // ─── Subscription service ─────────────────────────────────────────────

    private WebhookSubscriptionService SubscriptionService(string env = "Production")
    {
        var hostEnv = Substitute.For<IHostEnvironment>();
        hostEnv.EnvironmentName.Returns(env);
        return new WebhookSubscriptionService(_context, _protector, _httpFactory, hostEnv,
            Substitute.For<ILogger<WebhookSubscriptionService>>());
    }

    [Fact]
    public async Task Create_StoresEncryptedSecret_AndReturnsPlaintextOnce()
    {
        var svc = SubscriptionService();
        var created = await svc.CreateAsync(new CreateWebhookSubscriptionDto
        {
            Url = "https://example.com/hook",
            Events = [WebhookEventCatalog.InvoicePaid],
        });

        created.Secret.ShouldNotBeNullOrEmpty();
        var stored = await _context.WebhookSubscription.SingleAsync();
        stored.SecretEncrypted.ShouldBe("enc:" + created.Secret);
        stored.SecretEncrypted.ShouldNotBe(created.Secret);

        // Plain read model never exposes the secret.
        typeof(WebhookSubscriptionDto).GetProperties().ShouldNotContain(p => p.Name.Contains("Secret"));
    }

    [Fact]
    public async Task Create_RejectsHttpUrlUnknownEventAndNoEvents()
    {
        var svc = SubscriptionService();
        await Should.ThrowAsync<InvalidOperationException>(() => svc.CreateAsync(
            new CreateWebhookSubscriptionDto { Url = "http://example.com/h", Events = [WebhookEventCatalog.InvoicePaid] }));
        await Should.ThrowAsync<InvalidOperationException>(() => svc.CreateAsync(
            new CreateWebhookSubscriptionDto { Url = "https://example.com/h", Events = ["nope"] }));
        await Should.ThrowAsync<InvalidOperationException>(() => svc.CreateAsync(
            new CreateWebhookSubscriptionDto { Url = "https://example.com/h", Events = [] }));
    }

    [Fact]
    public async Task Rotate_ChangesSecret()
    {
        var svc = SubscriptionService();
        var created = await svc.CreateAsync(new CreateWebhookSubscriptionDto
        {
            Url = "https://example.com/hook", Events = [WebhookEventCatalog.InvoicePaid],
        });

        var rotated = await svc.RotateSecretAsync(created.Subscription.Id);

        rotated.Secret.ShouldNotBe(created.Secret);
        (await _context.WebhookSubscription.SingleAsync()).SecretEncrypted.ShouldBe("enc:" + rotated.Secret);
    }

    [Fact]
    public async Task Redeliver_ResetsDeliveryToPending()
    {
        var sub = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        var delivery = new WebhookDelivery
        {
            SubscriptionId = sub.Id, EventId = Guid.NewGuid(), EventType = "invoice.paid", PayloadJson = "{}",
            Status = EWebhookDeliveryStatus.Failed, Attempts = 8, LastError = "x", NextAttemptAt = DateTimeOffset.UtcNow,
        };
        _context.WebhookDelivery.Add(delivery);
        await _context.SaveChangesAsync();

        await SubscriptionService().RedeliverAsync(delivery.Id);

        var reloaded = await _context.WebhookDelivery.SingleAsync();
        reloaded.Status.ShouldBe(EWebhookDeliveryStatus.Pending);
        reloaded.Attempts.ShouldBe(0);
        reloaded.LastError.ShouldBeNull();
    }

    // ─── Publisher ────────────────────────────────────────────────────────

    [Fact]
    public async Task Publish_NoSubscriptions_IsNoOp()
    {
        await Publisher(_context).PublishInvoiceEventAsync(WebhookEventCatalog.InvoicePaid, _invoiceId);
        (await _context.WebhookDelivery.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Publish_OnlyMatchingActiveSubscriptions_GetDelivery_WithPayload()
    {
        var matching = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        AddSubscription(_context, WebhookEventCatalog.InvoiceCreated);          // other event
        var paused = AddSubscription(_context, WebhookEventCatalog.InvoicePaid); // paused
        paused.IsActive = false;
        await _context.SaveChangesAsync();

        await Publisher(_context).PublishInvoiceEventAsync(WebhookEventCatalog.InvoicePaid, _invoiceId);

        var delivery = await _context.WebhookDelivery.SingleAsync();
        delivery.SubscriptionId.ShouldBe(matching.Id);
        delivery.Status.ShouldBe(EWebhookDeliveryStatus.Pending);

        using var doc = JsonDocument.Parse(delivery.PayloadJson);
        var root = doc.RootElement;
        root.GetProperty("id").GetGuid().ShouldBe(delivery.EventId);
        root.GetProperty("type").GetString().ShouldBe("invoice.paid");
        root.GetProperty("companyId").GetInt64().ShouldBe(42);
        root.GetProperty("data").GetProperty("id").GetInt64().ShouldBe(_invoiceId);
        root.GetProperty("data").GetProperty("number").GetString().ShouldBe("2026001");
        root.GetProperty("data").GetProperty("total").GetDecimal().ShouldBe(1000m);
    }

    [Fact]
    public async Task Publish_NeverThrows_WhenDatabaseFails()
    {
        var ctx = NewContext();
        ctx.Dispose(); // any query now throws ObjectDisposedException
        await Should.NotThrowAsync(() => Publisher(ctx).PublishAsync("invoice.paid", new { }));
    }

    [Fact]
    public async Task MarkPaid_PublishesInvoicePaid()
    {
        AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        var publisher = Publisher(_context);
        var payments = new PaymentMatchingService(_context, Substitute.For<INotificationService>(),
            Substitute.For<ILogger<PaymentMatchingService>>(), publisher);

        var bank = new BankAccount { ClientId = (await _context.Client.FirstAsync(c => c.IsIssuer)).Id, AccountNumber = "1/0100", CurrencyCode = "CZK", IsDefault = true };
        _context.BankAccount.Add(bank);
        await _context.SaveChangesAsync();
        var tx = new BankTransaction
        {
            BankAccountId = bank.Id, DeduplicationHash = "h", TransactionDate = DateTime.UtcNow, Amount = 1000m,
            CurrencyCode = "CZK", Direction = EPaymentDirection.Incoming, VariableSymbol = "2026001",
            ImportSource = EImportSource.Manual, MatchStatus = EMatchStatus.Unmatched,
        };
        _context.BankTransaction.Add(tx);
        await _context.SaveChangesAsync();
        AddSubscription(_context, WebhookEventCatalog.PaymentReceived);

        await payments.MatchAsync(tx.Id);

        var types = await _context.WebhookDelivery.Select(d => d.EventType).ToListAsync();
        types.ShouldContain("invoice.paid");
        types.ShouldContain("payment.received");
    }

    // ─── Dispatcher ───────────────────────────────────────────────────────

    private async Task<WebhookDelivery> EnqueueAsync(TenantDbContext ctx, WebhookSubscription sub)
    {
        var d = new WebhookDelivery
        {
            SubscriptionId = sub.Id, EventId = Guid.NewGuid(), EventType = "invoice.paid", PayloadJson = "{\"x\":1}",
            Status = EWebhookDeliveryStatus.Pending, NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        };
        ctx.WebhookDelivery.Add(d);
        await ctx.SaveChangesAsync();
        return d;
    }

    [Fact]
    public async Task Dispatch_Success_SendsSignedRequest_AndMarksSucceeded()
    {
        var sub = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        var d = await EnqueueAsync(_context, sub);

        await Dispatcher(_context).RunCycleAsync();

        var reloaded = await _context.WebhookDelivery.SingleAsync();
        reloaded.Status.ShouldBe(EWebhookDeliveryStatus.Succeeded);
        reloaded.Attempts.ShouldBe(1);
        reloaded.LastStatusCode.ShouldBe(200);
        reloaded.DeliveredAt.ShouldNotBeNull();

        var req = _handler.Requests.Single();
        req.Url.ShouldBe("https://example.com/hook");
        req.Body.ShouldBe("{\"x\":1}");
        req.Headers["Fakvio-Webhook-Id"].ShouldBe(d.EventId.ToString());
        var ts = long.Parse(req.Headers["Fakvio-Webhook-Timestamp"]);
        req.Headers["Fakvio-Webhook-Signature"].ShouldBe(WebhookSigner.Sign("topsecret", ts, req.Body));
    }

    [Fact]
    public async Task Dispatch_RetryScheduleThenFailed()
    {
        _handler.StatusCode = HttpStatusCode.InternalServerError;
        var sub = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        await EnqueueAsync(_context, sub);
        var dispatcher = Dispatcher(_context);

        // Delay before attempt #2..#8 (DEVGUIDE §4.15): 1m, 5m, 30m, 2h, 6h, 12h, 24h.
        TimeSpan[] expected =
        [
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
            TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24),
        ];

        for (var attempt = 1; attempt <= expected.Length; attempt++)
        {
            var before = DateTimeOffset.UtcNow;
            await dispatcher.RunCycleAsync();
            var d = await _context.WebhookDelivery.SingleAsync();
            d.Attempts.ShouldBe(attempt);
            d.Status.ShouldBe(EWebhookDeliveryStatus.Pending);
            d.LastStatusCode.ShouldBe(500);
            (d.NextAttemptAt - before).ShouldBeInRange(expected[attempt - 1] - TimeSpan.FromSeconds(5), expected[attempt - 1] + TimeSpan.FromSeconds(5));

            // Not due yet -> a cycle right now must not send again.
            await dispatcher.RunCycleAsync();
            (await _context.WebhookDelivery.SingleAsync()).Attempts.ShouldBe(attempt);

            // Fast-forward to make it due.
            d.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await _context.SaveChangesAsync();
        }

        await dispatcher.RunCycleAsync(); // 8th attempt, no more backoff left
        var final = await _context.WebhookDelivery.SingleAsync();
        final.Attempts.ShouldBe(8);
        final.Status.ShouldBe(EWebhookDeliveryStatus.Failed);
        _handler.Requests.Count.ShouldBe(8);
    }

    [Fact]
    public async Task Dispatch_NetworkError_RecordsTruncatedError()
    {
        _handler.Throw = new HttpRequestException(new string('x', 5000));
        var sub = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        await EnqueueAsync(_context, sub);

        await Dispatcher(_context).RunCycleAsync();

        var d = await _context.WebhookDelivery.SingleAsync();
        d.Status.ShouldBe(EWebhookDeliveryStatus.Pending);
        d.LastError!.Length.ShouldBe(2000);
    }

    [Fact]
    public async Task Dispatch_InactiveSubscription_FailsWithoutSending()
    {
        var sub = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        await EnqueueAsync(_context, sub);
        sub.IsActive = false;
        await _context.SaveChangesAsync();

        await Dispatcher(_context).RunCycleAsync();

        (await _context.WebhookDelivery.SingleAsync()).Status.ShouldBe(EWebhookDeliveryStatus.Failed);
        _handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_DeletesTerminalDeliveriesOlderThan30Days_KeepsRecentAndPending()
    {
        var sub = AddSubscription(_context, WebhookEventCatalog.InvoicePaid);
        WebhookDelivery Make(EWebhookDeliveryStatus st, int ageDays) => new()
        {
            SubscriptionId = sub.Id, EventId = Guid.NewGuid(), EventType = "x", PayloadJson = "{}", Status = st,
            NextAttemptAt = DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow.AddDays(-ageDays),
        };
        var oldDone = Make(EWebhookDeliveryStatus.Succeeded, 31);
        var oldFailed = Make(EWebhookDeliveryStatus.Failed, 40);
        var recentDone = Make(EWebhookDeliveryStatus.Succeeded, 5);
        var oldPending = Make(EWebhookDeliveryStatus.Pending, 45);
        _context.WebhookDelivery.AddRange(oldDone, oldFailed, recentDone, oldPending);
        await _context.SaveChangesAsync();
        // Audit hooks may stamp CreatedAt on insert — force the ages we want.
        foreach (var d in new[] { oldDone, oldFailed, recentDone, oldPending })
        {
            d.CreatedAt = d == oldDone ? DateTime.UtcNow.AddDays(-31) : d == oldFailed ? DateTime.UtcNow.AddDays(-40)
                : d == oldPending ? DateTime.UtcNow.AddDays(-45) : DateTime.UtcNow.AddDays(-5);
        }
        await _context.SaveChangesAsync();

        await Dispatcher(_context).RunCycleAsync();

        var ids = await _context.WebhookDelivery.Select(d => d.Id).ToListAsync();
        ids.ShouldBe(new[] { recentDone.Id, oldPending.Id }, ignoreOrder: true);
    }

    // ─── Tenant isolation ─────────────────────────────────────────────────

    [Fact]
    public async Task TenantIsolation_EventsAndDispatchStayInTheirOwnTenant()
    {
        var tenantB = NewContext();
        var invoiceB = SeedInvoice(tenantB);
        AddSubscription(_context, WebhookEventCatalog.InvoicePaid); // subscribed in tenant A only

        await Publisher(tenantB).PublishInvoiceEventAsync(WebhookEventCatalog.InvoicePaid, invoiceB);

        (await tenantB.WebhookDelivery.CountAsync()).ShouldBe(0);
        (await _context.WebhookDelivery.CountAsync()).ShouldBe(0);

        // And tenant B's dispatcher never sees tenant A's subscriptions/deliveries.
        var subA = await _context.WebhookSubscription.SingleAsync();
        await EnqueueAsync(_context, subA);
        await Dispatcher(tenantB).RunCycleAsync();
        _handler.Requests.ShouldBeEmpty();

        // Service lookups are per-context too.
        var svcB = new WebhookSubscriptionService(tenantB, _protector, _httpFactory, Substitute.For<IHostEnvironment>(),
            Substitute.For<ILogger<WebhookSubscriptionService>>());
        (await svcB.GetByIdAsync(subA.Id)).ShouldBeNull();
        (await svcB.GetAllAsync()).ShouldBeEmpty();
        tenantB.Dispose();
    }

    // ─── Fake HTTP ────────────────────────────────────────────────────────

    private sealed record CapturedRequest(string Url, string Body, Dictionary<string, string> Headers);

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public Exception? Throw { get; set; }
        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw != null) throw Throw;
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), body,
                request.Headers.ToDictionary(h => h.Key, h => h.Value.First())));
            return new HttpResponseMessage(StatusCode) { Content = new StringContent("ok") };
        }
    }
}
