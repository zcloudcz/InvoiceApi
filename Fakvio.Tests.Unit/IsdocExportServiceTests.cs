using System.Reflection;
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
/// Unit tests for IsdocExportService and IsdocMapper.
/// Key acceptance criteria from issue #13:
///   - Generated XML must validate against the embedded ISDOC 6.0.2 XSD without errors.
///   - Tested for both CZK (standard) and EUR (foreign-currency) invoices.
/// </summary>
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

        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "Odberatel a.s.",
            RegistrationNumber = "55667788", TaxNumber = "CZ55667788",
            IsVatPayer = true, IsIssuer = false, IsActive = true,
            Address = new List<Address>
            {
                new Address
                {
                    Id = 2, Street = "Masarykova 2", City = "Brno",
                    PostalCode = "60200", Country = "CZ",
                    AddressType = EAddressType.Billing, IsPrimary = true
                }
            },
            Contact = new List<Contact>()
        });
        _context.SaveChanges();

        // CZK invoice: 2 items at different VAT rates (21% + 15%) -- mixed VAT scenario
        _context.Invoice.Add(new Invoice
        {
            Id = 1, DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed, DocumentNumber = "INV2026001",
            IssueDate = new DateTime(2026, 1, 15), DueDate = new DateTime(2026, 1, 29),
            TaxableSupplyDate = new DateTime(2026, 1, 15),
            IssuerId = 1, ClientId = 2, CurrencyId = 1,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "1234567890/0100",
            IBAN = "CZ6508000000192000145399",
            SWIFT = "GIBACZPX",
            VariableSymbol = "2026001",
            TotalBeforeVat = 10000, TotalVat = 2600, TotalWithVat = 12600,
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    Id = 1, OrderIndex = 1, Description = "Poradenstvi",
                    Quantity = 5, Unit = "hod", UnitPrice = 1500,
                    VatRatePercentage = 21, TotalBeforeVat = 7500, VatAmount = 1575, TotalWithVat = 9075
                },
                new InvoiceItem
                {
                    Id = 2, OrderIndex = 2, Description = "Software",
                    Quantity = 1, Unit = "ks", UnitPrice = 2500,
                    VatRatePercentage = 15, TotalBeforeVat = 2500, VatAmount = 375, TotalWithVat = 2875
                }
            }
        });
        _context.SaveChanges();

        // EUR invoice: 0% VAT (zero rate), foreign currency scenario
        _context.Invoice.Add(new Invoice
        {
            Id = 2, DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed, DocumentNumber = "INV2026002",
            IssueDate = new DateTime(2026, 2, 1), DueDate = new DateTime(2026, 2, 15),
            IssuerId = 1, ClientId = 2, CurrencyId = 2,
            PaymentMethod = EPaymentMethod.Cash,
            TotalBeforeVat = 1000, TotalVat = 0, TotalWithVat = 1000,
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    Id = 3, OrderIndex = 1, Description = "Export service",
                    Quantity = 1, Unit = "ks", UnitPrice = 1000,
                    VatRatePercentage = 0, TotalBeforeVat = 1000, VatAmount = 0, TotalWithVat = 1000
                }
            }
        });
        _context.SaveChanges();
    }

    // =========================================================================
    // Helpers: load the embedded XSD and validate an XDocument against it
    // =========================================================================

    /// <summary>
    /// Loads the ISDOC 6.0.2 XSD embedded in Fakvio.Infrastructure and returns it as XmlSchemaSet.
    /// Used directly in tests so the test assertion is independent of the service's logging path.
    /// </summary>
    private static XmlSchemaSet LoadEmbeddedSchema()
    {
        return IsdocExportService.LoadSchemaSet();
    }

    /// <summary>
    /// Validates <paramref name="doc"/> against the embedded XSD and returns all error messages.
    /// An empty list means the document is schema-valid.
    /// </summary>
    private static List<string> GetXsdErrors(XDocument doc)
    {
        var schemas = LoadEmbeddedSchema();
        var errors = new List<string>();
        doc.Validate(schemas, (_, e) => errors.Add(e.Message));
        return errors;
    }

    // =========================================================================
    // XSD validation tests (acceptance criteria #5 and #6 from issue #13)
    // =========================================================================

    [Fact]
    public void Map_FullyPopulatedCzkInvoice_ValidatesAgainstXsd()
    {
        // Acceptance criterion: CZK invoice with >= 2 items and mixed VAT rates
        // must produce XML that is valid against the embedded ISDOC 6.0.2 XSD.
        var invoice = BuildFullCzkInvoice();
        var doc = IsdocMapper.Map(invoice);

        var errors = GetXsdErrors(doc);

        // Report all errors in the failure message so the developer sees them immediately
        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public void Map_ForeignCurrencyInvoice_ValidatesAgainstXsd()
    {
        // Acceptance criterion: EUR invoice (foreign currency, zero VAT) must also
        // produce valid XML. LocalCurrencyCode=CZK + ForeignCurrencyCode=EUR.
        var invoice = BuildEurInvoice();
        var doc = IsdocMapper.Map(invoice);

        var errors = GetXsdErrors(doc);

        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    // =========================================================================
    // Service-level tests (round-trip through DB load)
    // =========================================================================

    [Fact]
    public async Task ExportInvoiceAsync_CzkInvoice_ReturnsBytesWithXmlDeclaration()
    {
        var bytes = await _service.ExportInvoiceAsync(1);

        bytes.ShouldNotBeNull();
        bytes.ShouldNotBeEmpty();
        // UTF-8 XML without BOM starts with the text "<?xml"
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.ShouldStartWith("<?xml");
    }

    [Fact]
    public async Task ExportInvoiceAsync_CzkInvoice_ProducesXsdValidXml()
    {
        // End-to-end: database load + map + serialise, then XSD-validate the bytes.
        var bytes = await _service.ExportInvoiceAsync(1);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var doc = XDocument.Parse(text);

        var errors = GetXsdErrors(doc);

        errors.ShouldBeEmpty($"XSD validation errors for CZK invoice:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public async Task ExportInvoiceAsync_EurInvoice_ProducesXsdValidXml()
    {
        // End-to-end for EUR invoice -- validates LocalCurrencyCode + ForeignCurrencyCode.
        var bytes = await _service.ExportInvoiceAsync(2);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var doc = XDocument.Parse(text);

        var errors = GetXsdErrors(doc);

        errors.ShouldBeEmpty($"XSD validation errors for EUR invoice:\n{string.Join("\n", errors)}");
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

    // =========================================================================
    // Mapper-level tests (pure unit tests, no DB)
    // =========================================================================

    // -------------------------------------------------------------------------
    // Header / currency
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_CzkInvoice_EmitsLocalCurrencyCodeCzk()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "LocalCurrencyCode").First().Value.ShouldBe("CZK");
        // No ForeignCurrencyCode element for a CZK invoice
        doc.Descendants(ns + "ForeignCurrencyCode").ShouldBeEmpty();
    }

    [Fact]
    public void Map_EurInvoice_EmitsLocalCurrencyCodeCzkAndForeignCurrencyCodeEur()
    {
        var inv = BuildMinimalInvoice();
        inv.Currency = new Currency { Code = "EUR" };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // LocalCurrencyCode is always CZK (issuer's accounting currency)
        doc.Descendants(ns + "LocalCurrencyCode").First().Value.ShouldBe("CZK");
        // ForeignCurrencyCode reflects the invoice currency
        doc.Descendants(ns + "ForeignCurrencyCode").First().Value.ShouldBe("EUR");
    }

    [Fact]
    public void Map_EurInvoice_ContainsForeignCurrencyNote()
    {
        var inv = BuildMinimalInvoice();
        inv.Currency = new Currency { Code = "EUR" };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // Note element should contain the exchange-rate explanation
        var notes = doc.Descendants(ns + "Note").Select(n => n.Value).ToList();
        notes.ShouldContain(IsdocMapper.ForeignCurrencyNote);
    }

    [Fact]
    public void Map_InvoiceWithNotes_BothUserNoteAndForeignCurrencyNoteEmitted()
    {
        // XSD allows multiple Note elements (maxOccurs="unbounded")
        // Both the user's notes and the ForeignCurrencyNote must appear.
        var inv = BuildMinimalInvoice();
        inv.Currency = new Currency { Code = "EUR" };
        inv.Notes = "Platba do 15 dni";
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var notes = doc.Descendants(ns + "Note").Select(n => n.Value).ToList();
        notes.ShouldContain("Platba do 15 dni");
        notes.ShouldContain(IsdocMapper.ForeignCurrencyNote);
    }

    // -------------------------------------------------------------------------
    // Parties
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_SupplierParty_ContainsIssuerIcoAndDic()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var supplierParty = doc.Descendants(ns + "AccountingSupplierParty").First();
        // ICO is inside PartyIdentification/ID
        var ico = supplierParty.Descendants(ns + "ID").First().Value;
        ico.ShouldBe("11223344");

        // DIC is inside PartyTaxScheme/CompanyID
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

    // -------------------------------------------------------------------------
    // Payment means
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_PaymentMeans_BankTransfer_ProducesCode42WithVariableSymbol()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var payment = doc.Descendants(ns + "Payment").First();
        // PaymentMeansCode is inside Payment wrapper
        payment.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("42");
        // VariableSymbol is in Details inside Payment
        payment.Descendants(ns + "VariableSymbol").First().Value.ShouldBe("42");
    }

    [Fact]
    public void Map_PaymentMeans_BankTransfer_EmitsIbanBicSeparately()
    {
        // IBAN and SWIFT must appear in their dedicated IBAN / BIC elements,
        // not stuffed into a generic <ID> element.
        var inv = BuildMinimalInvoice();
        inv.IBAN = "CZ6508000000192000145399";
        inv.SWIFT = "GIBACZPX";
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var details = doc.Descendants(ns + "Details").First();
        details.Element(ns + "IBAN")!.Value.ShouldBe("CZ6508000000192000145399");
        details.Element(ns + "BIC")!.Value.ShouldBe("GIBACZPX");
    }

    [Fact]
    public void Map_PaymentMeans_BankTransfer_EmitsPaymentDueDateInDetails()
    {
        // DueDate must appear inside PaymentMeans/Payment/Details/PaymentDueDate,
        // NOT at the invoice header level.
        var inv = BuildMinimalInvoice();
        inv.DueDate = new DateTime(2026, 3, 15);
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // Must NOT be at the root/header level
        var root = doc.Root!;
        root.Element(ns + "DueDate").ShouldBeNull();

        // Must be inside Details
        var details = doc.Descendants(ns + "Details").First();
        details.Element(ns + "PaymentDueDate")!.Value.ShouldBe("2026-03-15");
    }

    [Fact]
    public void Map_PaymentMeans_Cash_ProducesCode10()
    {
        var inv = BuildMinimalInvoice();
        inv.PaymentMethod = EPaymentMethod.Cash;
        inv.BankAccountNumber = null;
        inv.VariableSymbol = null;
        inv.DueDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var payment = doc.Descendants(ns + "Payment").First();
        payment.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("10");
    }

    [Fact]
    public void Map_PaymentMeans_CreditCard_ProducesCode48()
    {
        var inv = BuildMinimalInvoice();
        inv.PaymentMethod = EPaymentMethod.CreditCard;
        inv.BankAccountNumber = null;
        inv.VariableSymbol = null;
        inv.DueDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var payment = doc.Descendants(ns + "Payment").First();
        payment.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("48");
    }

    // -------------------------------------------------------------------------
    // Invoice lines
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_LineTotals_MatchItemTotals()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var line = doc.Descendants(ns + "InvoiceLine").First();
        line.Element(ns + "LineExtensionAmount")!.Value.ShouldBe("5000.00");
        line.Element(ns + "InvoicedQuantity")!.Value.ShouldBe("5.00");
        // UnitPrice is a direct child element (not inside Price wrapper)
        line.Element(ns + "UnitPrice")!.Value.ShouldBe("1000.00");
    }

    [Fact]
    public void Map_InvoiceLine_ClassifiedTaxCategoryIsDirectChild_NotInsideItem()
    {
        // XSD: ClassifiedTaxCategory is a direct child of InvoiceLineType.
        // ItemType only allows Description.
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var line = doc.Descendants(ns + "InvoiceLine").First();

        // ClassifiedTaxCategory must be a direct child of InvoiceLine
        line.Element(ns + "ClassifiedTaxCategory").ShouldNotBeNull();

        // Item must contain only Description (no ClassifiedTaxCategory inside Item)
        var item = line.Element(ns + "Item")!;
        item.Element(ns + "ClassifiedTaxCategory").ShouldBeNull();
        item.Element(ns + "Description")!.Value.ShouldBe("Sluzba");
    }

    // -------------------------------------------------------------------------
    // Tax total
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_TaxTotal_UsesCapitalTSubTotal()
    {
        // The XSD element name is "TaxSubTotal" (capital T), not "TaxSubtotal".
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var taxTotal = doc.Descendants(ns + "TaxTotal").First();
        // Capital T
        taxTotal.Elements(ns + "TaxSubTotal").ShouldNotBeEmpty();
        // Lowercase t should not appear
        taxTotal.Elements(ns + "TaxSubtotal").ShouldBeEmpty();
    }

    [Fact]
    public void Map_TaxTotal_NoTaxExemptionReasonCode()
    {
        // TaxCategoryType in XSD only allows Percent + TaxScheme.
        // TaxExemptionReasonCode is not in the schema.
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "TaxExemptionReasonCode").ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Legal monetary total
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_LegalMonetaryTotal_MatchesInvoiceTotals()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var total = doc.Descendants(ns + "LegalMonetaryTotal").First();
        // LegalMonetaryTotalType starts with TaxExclusiveAmount (not LineExtensionAmount)
        total.Element(ns + "TaxExclusiveAmount")!.Value.ShouldBe("5000.00");
        total.Element(ns + "TaxInclusiveAmount")!.Value.ShouldBe("6050.00");
        total.Element(ns + "PayableAmount")!.Value.ShouldBe("6050.00");
        // LineExtensionAmount does NOT exist in LegalMonetaryTotalType
        total.Element(ns + "LineExtensionAmount").ShouldBeNull();
    }

    // -------------------------------------------------------------------------
    // Credit note / document type
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
    // UUID determinism
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

    // -------------------------------------------------------------------------
    // Tax category code mapping
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

    // =========================================================================
    // Test data builders
    // =========================================================================

    /// <summary>
    /// Builds a minimal but fully-populated CZK invoice suitable for XSD validation.
    /// Single VAT rate for simplicity; use BuildFullCzkInvoice() for mixed-rate testing.
    /// </summary>
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
            },
            Client = new Client
            {
                RegistrationNumber = "55667788", TaxNumber = "CZ55667788",
                CompanyName = "Odberatel a.s.", IsVatPayer = true,
                Address = new List<Address>
                {
                    new Address
                    {
                        Street = "Masarykova 2", City = "Brno",
                        PostalCode = "60200", Country = "CZ",
                        IsPrimary = true, AddressType = EAddressType.Billing
                    }
                },
                Contact = new List<Contact>()
            },
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Sluzba",
                    Quantity = 5, Unit = "hod", UnitPrice = 1000,
                    VatRatePercentage = 21, TotalBeforeVat = 5000, VatAmount = 1050, TotalWithVat = 6050
                }
            }
        };
    }

    /// <summary>
    /// Fully-populated CZK invoice with 2 line items at mixed VAT rates (21% + 15%).
    /// Includes IBAN, SWIFT, all symbols -- used for the XSD validation acceptance test.
    /// </summary>
    private static Invoice BuildFullCzkInvoice()
    {
        return new Invoice
        {
            Id = 100,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026100",
            IssueDate = new DateTime(2026, 4, 1),
            DueDate = new DateTime(2026, 4, 15),
            TaxableSupplyDate = new DateTime(2026, 4, 1),
            TotalBeforeVat = 10000, TotalVat = 2600, TotalWithVat = 12600,
            PaymentMethod = EPaymentMethod.BankTransfer,
            BankAccountNumber = "1234567890/0100",
            IBAN = "CZ6508000000192000145399",
            SWIFT = "GIBACZPX",
            VariableSymbol = "2026100",
            Notes = "Splatnost 14 dni",
            Currency = new Currency { Code = "CZK" },
            Issuer = new Client
            {
                RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
                CompanyName = "Fakvio s.r.o.", IsVatPayer = true,
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
            },
            Client = new Client
            {
                RegistrationNumber = "55667788", TaxNumber = "CZ55667788",
                CompanyName = "Odberatel a.s.", IsVatPayer = true,
                Address = new List<Address>
                {
                    new Address
                    {
                        Street = "Masarykova 2", City = "Brno",
                        PostalCode = "60200", Country = "CZ",
                        IsPrimary = true, AddressType = EAddressType.Billing
                    }
                },
                Contact = new List<Contact>()
            },
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Poradenstvi",
                    Quantity = 5, Unit = "hod", UnitPrice = 1500,
                    VatRatePercentage = 21, TotalBeforeVat = 7500, VatAmount = 1575, TotalWithVat = 9075
                },
                new InvoiceItem
                {
                    OrderIndex = 2, Description = "Software licence",
                    Quantity = 1, Unit = "ks", UnitPrice = 2500,
                    VatRatePercentage = 15, TotalBeforeVat = 2500, VatAmount = 375, TotalWithVat = 2875
                }
            }
        };
    }

    /// <summary>
    /// EUR invoice with 0% VAT -- tests the foreign-currency path including
    /// LocalCurrencyCode=CZK + ForeignCurrencyCode=EUR.
    /// </summary>
    private static Invoice BuildEurInvoice()
    {
        return new Invoice
        {
            Id = 200,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = "INV2026200",
            IssueDate = new DateTime(2026, 4, 1),
            DueDate = new DateTime(2026, 4, 15),
            TotalBeforeVat = 1000, TotalVat = 0, TotalWithVat = 1000,
            PaymentMethod = EPaymentMethod.Cash,
            Currency = new Currency { Code = "EUR" },
            Issuer = new Client
            {
                RegistrationNumber = "11223344", TaxNumber = "CZ11223344",
                CompanyName = "Fakvio s.r.o.", IsVatPayer = true,
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
            },
            Client = new Client
            {
                RegistrationNumber = "55667788",
                CompanyName = "Foreign Client Ltd.", IsVatPayer = false,
                Address = new List<Address>
                {
                    new Address
                    {
                        Street = "Main St 1", City = "London",
                        PostalCode = "W1A 1AA", Country = "GB",
                        IsPrimary = true, AddressType = EAddressType.Billing
                    }
                },
                Contact = new List<Contact>()
            },
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    OrderIndex = 1, Description = "Export service",
                    Quantity = 1, Unit = "ks", UnitPrice = 1000,
                    VatRatePercentage = 0, TotalBeforeVat = 1000, VatAmount = 0, TotalWithVat = 1000
                }
            }
        };
    }
}
