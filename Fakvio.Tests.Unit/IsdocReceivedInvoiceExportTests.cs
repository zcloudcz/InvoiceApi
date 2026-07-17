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

/// <summary>
/// Unit tests for ISDOC export of received (incoming) invoices.
/// Mirrors IsdocExportServiceTests: the generated XML must validate against
/// the embedded ISDOC 6.0.2 XSD, and the parties must be swapped compared to
/// issued invoices — the supplier issued the document, our company receives it.
/// </summary>
public class IsdocReceivedInvoiceExportTests : IDisposable
{
    private static readonly XNamespace Ns = "http://isdoc.cz/namespace/2013";

    private readonly TenantDbContext _context;
    private readonly IsdocExportService _service;

    public IsdocReceivedInvoiceExportTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        _service = new IsdocExportService(_context, Substitute.For<ILogger<IsdocExportService>>());
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

        // Our company — the tenant issuer, i.e. the customer party on received invoices
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Fakvio s.r.o.",
            RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
            IsVatPayer = true, IsIssuer = true, IsActive = true,
            Address = new List<Address>
            {
                new Address
                {
                    Id = 1, Street = "Narodni 1", City = "Praha",
                    PostalCode = "11000", Country = "CZ",
                    AddressType = EAddressType.Billing, IsPrimary = true
                }
            },
            Contact = new List<Contact>()
        });
        _context.SaveChanges();

        // The supplier who issued the invoice to us
        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "Dodavatel s.r.o.",
            RegistrationNumber = "99887766", TaxNumber = "CZ99887766",
            IsVatPayer = true, IsIssuer = false, IsActive = true,
            Address = new List<Address>
            {
                new Address
                {
                    Id = 2, Street = "Videnska 10", City = "Brno",
                    PostalCode = "63900", Country = "CZ",
                    AddressType = EAddressType.Billing, IsPrimary = true
                }
            },
            Contact = new List<Contact>()
        });
        _context.SaveChanges();

        // CZK received invoice with mixed VAT rates (21% + 12%)
        _context.ReceivedInvoice.Add(new ReceivedInvoice
        {
            Id = 1, DocumentNumber = "FV2026001",
            Status = EReceivedInvoiceStatus.Received,
            SupplierId = 2, CurrencyId = 1,
            IssueDate = new DateTime(2026, 5, 1),
            ReceivedDate = new DateTime(2026, 5, 3),
            DueDate = new DateTime(2026, 5, 15),
            TaxableSupplyDate = new DateTime(2026, 5, 1),
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "1234567890/0100",
            IBAN = "CZ6508000000192000145399",
            SWIFT = "GIBACZPX",
            VariableSymbol = "2026001",
            TotalBeforeVat = 10000, TotalVat = 2340, TotalWithVat = 12340,
            Items = new List<ReceivedInvoiceItem>
            {
                new ReceivedInvoiceItem
                {
                    Id = 1, OrderIndex = 1, Description = "Hosting",
                    Quantity = 1, Unit = "ks", UnitPrice = 8000,
                    VatRatePercentage = 21, TotalBeforeVat = 8000, VatAmount = 1680, TotalWithVat = 9680
                },
                new ReceivedInvoiceItem
                {
                    Id = 2, OrderIndex = 2, Description = "Knihy",
                    Quantity = 4, Unit = "ks", UnitPrice = 500,
                    VatRatePercentage = 12, TotalBeforeVat = 2000, VatAmount = 660, TotalWithVat = 2660
                }
            }
        });
        _context.SaveChanges();

        // Soft-deleted received invoice — must not be exportable
        _context.ReceivedInvoice.Add(new ReceivedInvoice
        {
            Id = 2, DocumentNumber = "DEL001",
            Status = EReceivedInvoiceStatus.Deleted,
            SupplierId = 2, CurrencyId = 1,
            IssueDate = new DateTime(2026, 5, 1),
            TotalBeforeVat = 100, TotalVat = 0, TotalWithVat = 100,
            Items = new List<ReceivedInvoiceItem>()
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Validates <paramref name="doc"/> against the embedded XSD and returns all error messages.
    /// </summary>
    private static List<string> GetXsdErrors(XDocument doc)
    {
        var schemas = IsdocExportService.LoadSchemaSet();
        var errors = new List<string>();
        doc.Validate(schemas, (_, e) => errors.Add(e.Message));
        return errors;
    }

    // =========================================================================
    // Service-level tests (round-trip through DB load)
    // =========================================================================

    [Fact]
    public async Task ExportReceivedInvoiceAsync_CzkInvoice_ProducesXsdValidXml()
    {
        var bytes = await _service.ExportReceivedInvoiceAsync(1);
        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));

        var errors = GetXsdErrors(doc);

        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public async Task ExportReceivedInvoiceAsync_SupplierIsSupplierParty_OurCompanyIsCustomerParty()
    {
        var bytes = await _service.ExportReceivedInvoiceAsync(1);
        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));

        // The supplier issued the document → AccountingSupplierParty
        var supplierParty = doc.Descendants(Ns + "AccountingSupplierParty").First();
        supplierParty.Descendants(Ns + "ID").First().Value.ShouldBe("99887766");
        supplierParty.Descendants(Ns + "Name").First().Value.ShouldBe("Dodavatel s.r.o.");

        // Our company (IsIssuer = true) receives it → AccountingCustomerParty
        var customerParty = doc.Descendants(Ns + "AccountingCustomerParty").First();
        customerParty.Descendants(Ns + "ID").First().Value.ShouldBe("11223344");
        customerParty.Descendants(Ns + "Name").First().Value.ShouldBe("Fakvio s.r.o.");
    }

    [Fact]
    public async Task ExportReceivedInvoiceAsync_NotFound_ThrowsKeyNotFoundException()
    {
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.ExportReceivedInvoiceAsync(999));
    }

    [Fact]
    public async Task ExportReceivedInvoiceAsync_DeletedInvoice_ThrowsKeyNotFoundException()
    {
        // The service WHERE clause filters Status != Deleted — a soft-deleted
        // invoice must be treated as if it does not exist.
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.ExportReceivedInvoiceAsync(2));
    }

    [Fact]
    public async Task ExportReceivedInvoiceAsync_NoIssuerClient_StillExports()
    {
        // Misconfigured tenant without an issuer record: the export must still
        // succeed with an empty customer party instead of throwing.
        var issuer = _context.Client.First(c => c.IsIssuer);
        issuer.IsIssuer = false;
        _context.SaveChanges();

        var bytes = await _service.ExportReceivedInvoiceAsync(1);

        var doc = XDocument.Parse(System.Text.Encoding.UTF8.GetString(bytes));
        // Customer party keeps the XSD-required skeleton, but with empty values
        var customerParty = doc.Descendants(Ns + "AccountingCustomerParty").First();
        customerParty.Descendants(Ns + "PartyName").First()
            .Element(Ns + "Name")!.Value.ShouldBe(string.Empty);
    }

    // =========================================================================
    // Mapper-level tests (pure unit tests, no DB)
    // =========================================================================

    [Fact]
    public void Map_ReceivedInvoice_DocumentTypeIs1()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), BuildOurCompany());

        doc.Descendants(Ns + "DocumentType").First().Value.ShouldBe("1");
    }

    [Fact]
    public void Map_ReceivedInvoice_ValidatesAgainstXsd()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), BuildOurCompany());

        var errors = GetXsdErrors(doc);
        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public void Map_EurReceivedInvoice_ValidatesAgainstXsdAndEmitsForeignCurrency()
    {
        var invoice = BuildReceivedInvoice();
        invoice.Currency = new Currency { Code = "EUR" };
        var doc = IsdocMapper.Map(invoice, BuildOurCompany());

        doc.Descendants(Ns + "LocalCurrencyCode").First().Value.ShouldBe("CZK");
        doc.Descendants(Ns + "ForeignCurrencyCode").First().Value.ShouldBe("EUR");

        var errors = GetXsdErrors(doc);
        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public void Map_ReceivedInvoice_UuidDiffersFromIssuedInvoiceWithSameId()
    {
        // Both UUIDs are deterministic, but the name prefixes differ so an
        // issued invoice and a received invoice with the same DB id can never
        // collide when both are imported into the same accounting software.
        var received = BuildReceivedInvoice(); // Id = 42
        var receivedUuid = IsdocMapper.Map(received, BuildOurCompany())
            .Descendants(Ns + "UUID").First().Value;

        var issued = new Invoice
        {
            Id = 42, DocumentType = EDocumentType.Invoice,
            DocumentNumber = "X", IssueDate = new DateTime(2026, 5, 1),
            Currency = new Currency { Code = "CZK" },
            Issuer = BuildOurCompany(), Client = BuildSupplier(),
            InvoiceItem = new List<InvoiceItem>()
        };
        var issuedUuid = IsdocMapper.Map(issued)
            .Descendants(Ns + "UUID").First().Value;

        receivedUuid.ShouldNotBe(issuedUuid);
    }

    [Fact]
    public void Map_ReceivedInvoice_BankTransferEmitsVariableSymbolInDetails()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), BuildOurCompany());

        var details = doc.Descendants(Ns + "Details").First();
        details.Element(Ns + "PaymentDueDate")!.Value.ShouldBe("2026-05-15");
        details.Element(Ns + "IBAN")!.Value.ShouldBe("CZ6508000000192000145399");
        details.Descendants(Ns + "VariableSymbol").First().Value.ShouldBe("2026001");
    }

    [Fact]
    public void Map_ReceivedInvoice_LineTotalsMatchItems()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), BuildOurCompany());

        var line = doc.Descendants(Ns + "InvoiceLine").First();
        line.Element(Ns + "LineExtensionAmount")!.Value.ShouldBe("8000.00");
        line.Element(Ns + "InvoicedQuantity")!.Value.ShouldBe("1.00");
        line.Element(Ns + "UnitPrice")!.Value.ShouldBe("8000.00");
        line.Element(Ns + "Item")!.Element(Ns + "Description")!.Value.ShouldBe("Hosting");
    }

    [Fact]
    public void Map_ReceivedInvoice_MixedVatRates_ProducesOneTaxSubTotalPerRate()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), BuildOurCompany());

        var taxSubTotals = doc.Descendants(Ns + "TaxTotal")
            .First()
            .Elements(Ns + "TaxSubTotal")
            .ToList();

        taxSubTotals.Count.ShouldBe(2);
        var rates = taxSubTotals.Select(s => s.Descendants(Ns + "Percent").First().Value).ToList();
        rates.ShouldContain("21.00");
        rates.ShouldContain("12.00");
    }

    [Fact]
    public void Map_ReceivedInvoice_LegalMonetaryTotalMatchesInvoiceTotals()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), BuildOurCompany());

        var total = doc.Descendants(Ns + "LegalMonetaryTotal").First();
        total.Element(Ns + "TaxExclusiveAmount")!.Value.ShouldBe("10000.00");
        total.Element(Ns + "TaxInclusiveAmount")!.Value.ShouldBe("12340.00");
        total.Element(Ns + "PayableAmount")!.Value.ShouldBe("12340.00");
    }

    [Fact]
    public void Map_ReceivedInvoice_NullCustomer_StillValidatesAgainstXsd()
    {
        var doc = IsdocMapper.Map(BuildReceivedInvoice(), customer: null);

        var errors = GetXsdErrors(doc);
        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    // =========================================================================
    // Test data builders
    // =========================================================================

    private static Client BuildOurCompany() => new()
    {
        RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
        CompanyName = "Fakvio s.r.o.", IsVatPayer = true, IsIssuer = true,
        Address = new List<Address>
        {
            new Address
            {
                Street = "Narodni 1", City = "Praha",
                PostalCode = "11000", Country = "CZ",
                IsPrimary = true, AddressType = EAddressType.Billing
            }
        },
        Contact = new List<Contact>()
    };

    private static Client BuildSupplier() => new()
    {
        RegistrationNumber = "99887766", TaxNumber = "CZ99887766",
        CompanyName = "Dodavatel s.r.o.", IsVatPayer = true,
        Address = new List<Address>
        {
            new Address
            {
                Street = "Videnska 10", City = "Brno",
                PostalCode = "63900", Country = "CZ",
                IsPrimary = true, AddressType = EAddressType.Billing
            }
        },
        Contact = new List<Contact>()
    };

    /// <summary>
    /// Builds a fully-populated CZK received invoice with two items at mixed
    /// VAT rates (21% + 12%), suitable for XSD validation.
    /// </summary>
    private static ReceivedInvoice BuildReceivedInvoice() => new()
    {
        Id = 42,
        DocumentNumber = "FV2026001",
        Status = EReceivedInvoiceStatus.Received,
        Supplier = BuildSupplier(),
        IssueDate = new DateTime(2026, 5, 1),
        ReceivedDate = new DateTime(2026, 5, 3),
        DueDate = new DateTime(2026, 5, 15),
        TaxableSupplyDate = new DateTime(2026, 5, 1),
        PaymentMethod = EPaymentMethod.BankTransfer,
        BankAccountNumber = "1234567890/0100",
        IBAN = "CZ6508000000192000145399",
        SWIFT = "GIBACZPX",
        VariableSymbol = "2026001",
        TotalBeforeVat = 10000, TotalVat = 2340, TotalWithVat = 12340,
        Currency = new Currency { Code = "CZK" },
        Items = new List<ReceivedInvoiceItem>
        {
            new ReceivedInvoiceItem
            {
                OrderIndex = 1, Description = "Hosting",
                Quantity = 1, Unit = "ks", UnitPrice = 8000,
                VatRatePercentage = 21, TotalBeforeVat = 8000, VatAmount = 1680, TotalWithVat = 9680
            },
            new ReceivedInvoiceItem
            {
                OrderIndex = 2, Description = "Knihy",
                Quantity = 4, Unit = "ks", UnitPrice = 500,
                VatRatePercentage = 12, TotalBeforeVat = 2000, VatAmount = 660, TotalWithVat = 2660
            }
        }
    };
}
