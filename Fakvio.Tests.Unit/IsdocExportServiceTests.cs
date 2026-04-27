using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.Isdoc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;
public class IsdocExportServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly IsdocExportService _service;
    private readonly ILogger<IsdocExportService> _logger;

    public IsdocExportServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<IsdocExportService>>();
        _service = new IsdocExportService(_context, _logger);
        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedTestData()
    {
        _context.Currency.Add(new Currency { Id = 1, Code = "CZK", Symbol = "Kc", Name = "Czech Koruna", IsActive = true });
        _context.Currency.Add(new Currency { Id = 2, Code = "EUR", Symbol = "EUR", Name = "Euro", IsActive = true });
        _context.SaveChanges();

        _context.Client.Add(new Client { Id = 1, CompanyName = "Fakvio s.r.o.",
            RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
            IsVatPayer = true, IsIssuer = true, IsActive = true,
            Address = new List<Address> { new Address { Id = 1, Street = "Narodni 1",
                City = "Praha", PostalCode = "11000", Country = "CZ",
                AddressType = EAddressType.Billing, IsPrimary = true } },
            Contact = new List<Contact>() });
        _context.SaveChanges();

        _context.Client.Add(new Client { Id = 2, CompanyName = "Odberatel a.s.",
            RegistrationNumber = "55667788", TaxNumber = "CZ55667788",
            IsVatPayer = true, IsIssuer = false, IsActive = true,
            Address = new List<Address> { new Address { Id = 2, Street = "Masarykova 2",
                City = "Brno", PostalCode = "60200", Country = "CZ",
                AddressType = EAddressType.Billing, IsPrimary = true } },
            Contact = new List<Contact>() });
        _context.SaveChanges();

        // CZK invoice with two items at different VAT rates
        _context.Invoice.Add(new Invoice { Id = 1, DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed, DocumentNumber = "INV2026001",
            IssueDate = new DateTime(2026, 1, 15), DueDate = new DateTime(2026, 1, 29),
            TaxableSupplyDate = new DateTime(2026, 1, 15),
            IssuerId = 1, ClientId = 2, CurrencyId = 1,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "1234567890/0100", VariableSymbol = "2026001",
            TotalBeforeVat = 10000, TotalVat = 2600, TotalWithVat = 12600,
            InvoiceItem = new List<InvoiceItem> {
                new InvoiceItem { Id = 1, OrderIndex = 1, Description = "Poradenstvi",
                    Quantity = 5, Unit = "hod", UnitPrice = 1500,
                    VatRatePercentage = 21, TotalBeforeVat = 7500, VatAmount = 1575, TotalWithVat = 9075 },
                new InvoiceItem { Id = 2, OrderIndex = 2, Description = "Software",
                    Quantity = 1, Unit = "ks", UnitPrice = 2500,
                    VatRatePercentage = 15, TotalBeforeVat = 2500, VatAmount = 375, TotalWithVat = 2875 }
            } });
        _context.SaveChanges();

        // EUR invoice with 0% VAT
        _context.Invoice.Add(new Invoice { Id = 2, DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed, DocumentNumber = "INV2026002",
            IssueDate = new DateTime(2026, 2, 1), DueDate = new DateTime(2026, 2, 15),
            IssuerId = 1, ClientId = 2, CurrencyId = 2,
            PaymentMethod = EPaymentMethod.Cash,
            TotalBeforeVat = 1000, TotalVat = 0, TotalWithVat = 1000,
            InvoiceItem = new List<InvoiceItem> {
                new InvoiceItem { Id = 3, OrderIndex = 1, Description = "Export service",
                    Quantity = 1, Unit = "ks", UnitPrice = 1000,
                    VatRatePercentage = 0, TotalBeforeVat = 1000, VatAmount = 0, TotalWithVat = 1000 }
            } });
        _context.SaveChanges();
    }

    // -------------------------------------------------------------------------
    // Service-level tests (round-trip through DB load)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_CzkInvoice_ReturnsBytesWithXmlDeclaration()
    {
        var bytes = await _service.ExportInvoiceAsync(1);

        bytes.ShouldNotBeNull();
        bytes.ShouldNotBeEmpty();
        // UTF-8 XML starts with <?xml
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.ShouldStartWith("<?xml");
    }

    [Fact]
    public async Task ExportInvoiceAsync_InvoiceNotFound_ThrowsKeyNotFoundException()
    {
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.ExportInvoiceAsync(999));
    }

    [Fact]
    public async Task ExportInvoiceAsync_ForeignCurrencyInvoice_ContainsForeignCurrencyNote()
    {
        var bytes = await _service.ExportInvoiceAsync(2);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.ShouldContain(IsdocMapper.ForeignCurrencyNote);
    }

    // -------------------------------------------------------------------------
    // Mapper-level tests (pure unit tests, no DB)
    // -------------------------------------------------------------------------

    private static Invoice BuildMinimalInvoice()
    {
        return new Invoice
        {
            Id = 42,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV-TEST-001",
            IssueDate = new DateTime(2026, 3, 1),
            DueDate = new DateTime(2026, 3, 15),
            TaxableSupplyDate = new DateTime(2026, 3, 1),
            TotalBeforeVat = 5000, TotalVat = 1050, TotalWithVat = 6050,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "987654321/0300",
            VariableSymbol = "42",
            Currency = new Currency { Code = "CZK" },
            Issuer = new Client
            {
                RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
                CompanyName = "Fakvio s.r.o.", IsVatPayer = true,
                Address = new List<Address> { new Address { Street = "Narodni 1",
                    City = "Praha", PostalCode = "11000", Country = "CZ",
                    IsPrimary = true, AddressType = EAddressType.Billing } },
                Contact = new List<Contact>()
            },
            Client = new Client
            {
                RegistrationNumber = "55667788", TaxNumber = "CZ55667788",
                CompanyName = "Odberatel a.s.", IsVatPayer = true,
                Address = new List<Address> { new Address { Street = "Masarykova 2",
                    City = "Brno", PostalCode = "60200", Country = "CZ",
                    IsPrimary = true, AddressType = EAddressType.Billing } },
                Contact = new List<Contact>()
            },
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem { OrderIndex = 1, Description = "Sluzba",
                    Quantity = 5, Unit = "hod", UnitPrice = 1000,
                    VatRatePercentage = 21, TotalBeforeVat = 5000, VatAmount = 1050, TotalWithVat = 6050 }
            }
        };
    }

    [Fact]
    public void Map_SupplierParty_ContainsIssuerIcoAndDic()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // ICO in AccountingSupplierParty/Party/PartyIdentification/ID
        var supplierParty = doc.Descendants(ns + "AccountingSupplierParty").First();
        var ico = supplierParty.Descendants(ns + "ID").First().Value;
        ico.ShouldBe("11223344");

        // DIC in PartyTaxScheme/CompanyID
        var dic = supplierParty.Descendants(ns + "CompanyID").First().Value;
        dic.ShouldBe("CZ11223344");
    }

    [Fact]
    public void Map_CustomerParty_ContainsClientIcoAndDic()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var customerParty = doc.Descendants(ns + "AccountingCustomerParty").First();
        var ico = customerParty.Descendants(ns + "ID").First().Value;
        ico.ShouldBe("55667788");

        var dic = customerParty.Descendants(ns + "CompanyID").First().Value;
        dic.ShouldBe("CZ55667788");
    }

    [Fact]
    public void Map_PaymentMeans_BankTransfer_ProducesCode42WithVariableSymbol()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var paymentMeans = doc.Descendants(ns + "PaymentMeans").First();
        paymentMeans.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("42");
        paymentMeans.Descendants(ns + "VariableSymbol").First().Value.ShouldBe("42");
    }

    [Fact]
    public void Map_LineTotals_MatchItemTotals()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var line = doc.Descendants(ns + "InvoiceLine").First();
        line.Element(ns + "LineExtensionAmount")!.Value.ShouldBe("5000.00");
        line.Element(ns + "InvoicedQuantity")!.Value.ShouldBe("5.00");
    }

    [Fact]
    public void Map_LegalMonetaryTotal_MatchesInvoiceTotals()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var total = doc.Descendants(ns + "LegalMonetaryTotal").First();
        total.Element(ns + "TaxExclusiveAmount")!.Value.ShouldBe("5000.00");
        total.Element(ns + "TaxInclusiveAmount")!.Value.ShouldBe("6050.00");
        total.Element(ns + "PayableAmount")!.Value.ShouldBe("6050.00");
    }

    [Fact]
    public void Map_DocumentCurrencyCode_CzechInvoice_IsCZK()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");
        doc.Descendants(ns + "DocumentCurrencyCode").First().Value.ShouldBe("CZK");
    }

    [Fact]
    public void Map_DocumentCurrencyCode_EuroInvoice_IsEUR()
    {
        var inv = BuildMinimalInvoice();
        inv.Currency = new Currency { Code = "EUR" };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");
        doc.Descendants(ns + "DocumentCurrencyCode").First().Value.ShouldBe("EUR");
    }

    // -------------------------------------------------------------------------
    // Tax category code mapping unit tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(21, "S")]
    [InlineData(15, "S")]
    [InlineData(12, "S")]
    [InlineData(10, "S")]
    [InlineData(0, "Z")]
    public void MapTaxCategoryCode_ReturnsCorrectCode(decimal rate, string expected)
    {
        IsdocMapper.MapTaxCategoryCode(rate).ShouldBe(expected);
    }

    // -------------------------------------------------------------------------
    // Credit note mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_CreditNote_DocumentTypeIs5()
    {
        var inv = BuildMinimalInvoice();
        inv.DocumentType = EDocumentType.CreditNote;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");
        doc.Descendants(ns + "DocumentType").First().Value.ShouldBe("5");
    }

    [Fact]
    public void Map_Invoice_DocumentTypeIs1()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");
        doc.Descendants(ns + "DocumentType").First().Value.ShouldBe("1");
    }

    // -------------------------------------------------------------------------
    // UUID determinism test
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_SameInvoiceId_ProducesSameUuid()
    {
        var inv1 = BuildMinimalInvoice();
        var inv2 = BuildMinimalInvoice();
        var doc1 = IsdocMapper.Map(inv1);
        var doc2 = IsdocMapper.Map(inv2);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");
        var uuid1 = doc1.Descendants(ns + "UUID").First().Value;
        var uuid2 = doc2.Descendants(ns + "UUID").First().Value;
        uuid1.ShouldBe(uuid2);
        Guid.TryParse(uuid1, out _).ShouldBeTrue();
    }
}
