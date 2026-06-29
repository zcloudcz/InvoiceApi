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
        // Official XSD allows max 1 Note element -- both notes merged with " | " separator.
        var inv = BuildMinimalInvoice();
        inv.Currency = new Currency { Code = "EUR" };
        inv.Notes = "Platba do 15 dni";
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // Single Note element (not under InvoiceLines) at invoice header level
        var headerNotes = doc.Root!.Elements(ns + "Note").ToList();
        headerNotes.Count.ShouldBe(1);
        headerNotes[0].Value.ShouldContain("Platba do 15 dni");
        headerNotes[0].Value.ShouldContain(IsdocMapper.ForeignCurrencyNote);
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

    // =========================================================================
    // Additional coverage tests (issue #13 strengthening pass)
    // =========================================================================

    // -------------------------------------------------------------------------
    // UTF-8 encoding: output must NOT start with BOM (EF BB BF)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_Output_HasNoBom()
    {
        // UTF-8 BOM bytes: 0xEF 0xBB 0xBF.
        // Most XML parsers accept BOM but some Czech accounting software
        // does not -- the service explicitly disables BOM via UTF8Encoding(false).
        var bytes = await _service.ExportInvoiceAsync(1);

        bytes.ShouldNotBeNull();
        bytes.Length.ShouldBeGreaterThan(3);
        // First three bytes must NOT be the UTF-8 BOM sequence
        var hasBom = bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasBom.ShouldBeFalse("Output byte array must not start with UTF-8 BOM (EF BB BF)");
    }

    // -------------------------------------------------------------------------
    // Deleted invoices must not be exported (soft-delete guard in service)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_DeletedInvoice_ThrowsKeyNotFoundException()
    {
        // Seed a deleted invoice -- the service WHERE clause filters Status != Deleted.
        _context.Invoice.Add(new Invoice
        {
            Id = 50,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Deleted,
            DocumentNumber = "DEL001",
            IssueDate = new DateTime(2026, 1, 1),
            IssuerId = 1, ClientId = 2, CurrencyId = 1,
            PaymentMethod = EPaymentMethod.Cash,
            TotalBeforeVat = 100, TotalVat = 0, TotalWithVat = 100,
            InvoiceItem = new List<InvoiceItem>
            {
                new InvoiceItem
                {
                    Id = 100, OrderIndex = 1, Description = "deleted item",
                    Quantity = 1, Unit = "ks", UnitPrice = 100,
                    VatRatePercentage = 0, TotalBeforeVat = 100, VatAmount = 0, TotalWithVat = 100
                }
            }
        });
        _context.SaveChanges();

        // The service must treat a deleted invoice as if it does not exist.
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.ExportInvoiceAsync(50));
    }

    // -------------------------------------------------------------------------
    // PaymentMeans: PayPal and Other both map to ZZZ (UNCL4461 mutually defined)
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_PaymentMeans_PayPal_ProducesCodeZzz()
    {
        var inv = BuildMinimalInvoice();
        inv.PaymentMethod = EPaymentMethod.PayPal;
        inv.BankAccountNumber = null;
        inv.VariableSymbol = null;
        inv.DueDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var payment = doc.Descendants(ns + "Payment").First();
        payment.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("ZZZ");
    }

    [Fact]
    public void Map_PaymentMeans_Other_ProducesCodeZzz()
    {
        var inv = BuildMinimalInvoice();
        inv.PaymentMethod = EPaymentMethod.Other;
        inv.BankAccountNumber = null;
        inv.VariableSymbol = null;
        inv.DueDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var payment = doc.Descendants(ns + "Payment").First();
        payment.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("ZZZ");
    }

    // -------------------------------------------------------------------------
    // PaymentMeans: null PaymentMethod must not emit a <PaymentMeans> element
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_NoPaymentMethod_NoPaymentMeansElement()
    {
        // When PaymentMethod is null the mapper must omit the PaymentMeans element
        // entirely (XSD: PaymentMeans is optional, minOccurs="0").
        var inv = BuildMinimalInvoice();
        inv.PaymentMethod = null;
        inv.BankAccountNumber = null;
        inv.VariableSymbol = null;
        inv.DueDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "PaymentMeans").ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Payment symbols: ConstantSymbol and SpecificSymbol
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_PaymentDetails_EmitsConstantSymbolAndSpecificSymbol()
    {
        // Both symbols must appear inside Details when set.
        var inv = BuildMinimalInvoice();
        inv.ConstantSymbol = "0308";
        inv.SpecificSymbol = "9999";
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var details = doc.Descendants(ns + "Details").First();
        details.Element(ns + "ConstantSymbol")!.Value.ShouldBe("0308");
        details.Element(ns + "SpecificSymbol")!.Value.ShouldBe("9999");
    }

    // -------------------------------------------------------------------------
    // Text rows: IsTextRow items must be skipped from InvoiceLine and TaxTotal
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_TextRowItems_AreIncludedWithZeroAmounts()
    {
        // IsTextRow=true rows are section headers / comments -- they appear in the
        // ISDOC document as lines with zero amounts so the text is preserved.
        var inv = BuildMinimalInvoice();
        inv.InvoiceItem = new List<InvoiceItem>
        {
            new InvoiceItem
            {
                OrderIndex = 1, Description = "Vyvoj",
                Quantity = 10, Unit = "hod", UnitPrice = 500,
                VatRatePercentage = 21, TotalBeforeVat = 5000, VatAmount = 1050, TotalWithVat = 6050,
                IsTextRow = false
            },
            new InvoiceItem
            {
                OrderIndex = 2, Description = "Tato polozka je pouze textova",
                IsTextRow = true
            }
        };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var lines = doc.Descendants(ns + "InvoiceLine").ToList();
        lines.Count.ShouldBe(2);

        // First line: normal billable item
        lines[0].Element(ns + "Item")!.Element(ns + "Description")!.Value.ShouldBe("Vyvoj");
        lines[0].Element(ns + "LineExtensionAmount")!.Value.ShouldBe("5000.00");

        // Second line: text row with zero amounts and preserved description
        lines[1].Element(ns + "Item")!.Element(ns + "Description")!.Value.ShouldBe("Tato polozka je pouze textova");
        lines[1].Element(ns + "InvoicedQuantity")!.Value.ShouldBe("0.00");
        lines[1].Element(ns + "LineExtensionAmount")!.Value.ShouldBe("0.00");
        lines[1].Element(ns + "UnitPrice")!.Value.ShouldBe("0.00");
    }

    [Fact]
    public void Map_TextRowItems_StillExcludedFromTaxTotal()
    {
        // Text rows must not affect TaxSubTotal grouping or amounts.
        var inv = BuildMinimalInvoice();
        inv.InvoiceItem = new List<InvoiceItem>
        {
            new InvoiceItem
            {
                OrderIndex = 1, Description = "Vyvoj",
                Quantity = 10, Unit = "hod", UnitPrice = 500,
                VatRatePercentage = 21, TotalBeforeVat = 5000, VatAmount = 1050, TotalWithVat = 6050,
                IsTextRow = false
            },
            new InvoiceItem
            {
                OrderIndex = 2, Description = "Poznamka",
                IsTextRow = true
            }
        };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // Only one TaxSubTotal (21%) — text row must not create a 0% group
        var taxSubTotals = doc.Descendants(ns + "TaxSubTotal").ToList();
        taxSubTotals.Count.ShouldBe(1);
        taxSubTotals[0].Element(ns + "TaxableAmount")!.Value.ShouldBe("5000.00");
    }

    [Fact]
    public void Map_InvoiceWithTextRows_ValidatesAgainstXsd()
    {
        var inv = BuildMinimalInvoice();
        inv.InvoiceItem!.Add(new InvoiceItem
        {
            OrderIndex = 0, Description = "Sekce: Vyvoj", IsTextRow = true
        });
        var doc = IsdocMapper.Map(inv);

        var errors = GetXsdErrors(doc);
        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }

    // -------------------------------------------------------------------------
    // Issuer without VAT number: PartyTaxScheme must be omitted
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_NonVatPayerIssuer_NoPartyTaxSchemeInSupplierParty()
    {
        // When the issuer is not a VAT payer the PartyTaxScheme element (which
        // holds DIC) must NOT appear in AccountingSupplierParty.
        var inv = BuildMinimalInvoice();
        inv.Issuer!.IsVatPayer = false;
        inv.Issuer.TaxNumber = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var supplierParty = doc.Descendants(ns + "AccountingSupplierParty").First();
        supplierParty.Descendants(ns + "PartyTaxScheme").ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Customer without VAT number: PartyTaxScheme must be omitted
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_NonVatPayerCustomer_NoPartyTaxSchemeInCustomerParty()
    {
        var inv = BuildMinimalInvoice();
        inv.Client!.IsVatPayer = false;
        inv.Client.TaxNumber = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var customerParty = doc.Descendants(ns + "AccountingCustomerParty").First();
        customerParty.Descendants(ns + "PartyTaxScheme").ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Different invoice IDs must produce different UUIDs
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_DifferentInvoiceIds_ProduceDifferentUuids()
    {
        // Determinism: same ID → same UUID (tested elsewhere).
        // Uniqueness:  different ID → different UUID.
        var inv1 = BuildMinimalInvoice(); // Id = 42
        var inv2 = BuildMinimalInvoice();
        inv2.Id = 43;

        var doc1 = IsdocMapper.Map(inv1);
        var doc2 = IsdocMapper.Map(inv2);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var uuid1 = doc1.Descendants(ns + "UUID").First().Value;
        var uuid2 = doc2.Descendants(ns + "UUID").First().Value;
        uuid1.ShouldNotBe(uuid2);
    }

    // -------------------------------------------------------------------------
    // Invoice without DueDate: no PaymentDueDate and no Details element for cash
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_CashInvoiceWithNoDueDate_NoDetailsElement()
    {
        // Cash payment with no due date and no bank details must not produce
        // an empty <Details /> element.
        var inv = BuildMinimalInvoice();
        inv.PaymentMethod = EPaymentMethod.Cash;
        inv.BankAccountNumber = null;
        inv.IBAN = null;
        inv.SWIFT = null;
        inv.VariableSymbol = null;
        inv.ConstantSymbol = null;
        inv.SpecificSymbol = null;
        inv.DueDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // PaymentMeans/Payment must exist (code 10) but no Details inside
        var payment = doc.Descendants(ns + "Payment").First();
        payment.Element(ns + "PaymentMeansCode")!.Value.ShouldBe("10");
        payment.Element(ns + "Details").ShouldBeNull();
    }

    // -------------------------------------------------------------------------
    // InvoiceLine: unitCode fallback to H87 when Unit is null
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_ItemWithNullUnit_UsesH87FallbackUnitCode()
    {
        // UN/ECE Recommendation 20 code H87 means "piece" and is used as
        // fallback when InvoiceItem.Unit is null.
        var inv = BuildMinimalInvoice();
        inv.InvoiceItem.First().Unit = null!;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var qty = doc.Descendants(ns + "InvoicedQuantity").First();
        qty.Attribute("unitCode")!.Value.ShouldBe("H87");
    }

    // -------------------------------------------------------------------------
    // Multiple VAT rates: TaxSubTotal grouping
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_MixedVatRates_ProducesOneTaxSubTotalPerRate()
    {
        // An invoice with two different VAT rates must produce exactly two
        // TaxSubTotal elements inside TaxTotal, one per rate.
        var inv = BuildFullCzkInvoice(); // 21% + 15%
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var taxSubTotals = doc.Descendants(ns + "TaxTotal")
            .First()
            .Elements(ns + "TaxSubTotal")
            .ToList();

        taxSubTotals.Count.ShouldBe(2);

        // Rates should be present as child Percent elements
        var rates = taxSubTotals
            .Select(s => s.Descendants(ns + "Percent").First().Value)
            .ToList();
        rates.ShouldContain("21.00");
        rates.ShouldContain("15.00");
    }

    // -------------------------------------------------------------------------
    // CreditNote XSD validation round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_CreditNote_ValidatesAgainstXsd()
    {
        // DocumentType=5 credit notes must also be schema-valid.
        var inv = BuildMinimalInvoice();
        inv.DocumentType = EDocumentType.CreditNote;
        var doc = IsdocMapper.Map(inv);

        var errors = GetXsdErrors(doc);

        errors.ShouldBeEmpty($"XSD validation errors for CreditNote:\n{string.Join("\n", errors)}");
    }

    // -------------------------------------------------------------------------
    // Element order: ISDOC 6.0.2 XSD requires strict sequence
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_HeaderElements_AreInCorrectXsdSequence()
    {
        // The ISDOC 6.0.2 XSD uses xs:sequence which enforces strict element order.
        // Regression guard: ID must come before UUID, UUID before IssuingSystem, etc.
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var children = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();

        var idxDocType = children.IndexOf("DocumentType");
        var idxId = children.IndexOf("ID");
        var idxUuid = children.IndexOf("UUID");
        var idxIssuingSystem = children.IndexOf("IssuingSystem");
        var idxIssueDate = children.IndexOf("IssueDate");
        var idxVatApplicable = children.IndexOf("VATApplicable");
        var idxLocalCurrency = children.IndexOf("LocalCurrencyCode");
        var idxSupplier = children.IndexOf("AccountingSupplierParty");

        idxDocType.ShouldBeLessThan(idxId, "DocumentType must precede ID");
        idxId.ShouldBeLessThan(idxUuid, "ID must precede UUID");
        idxUuid.ShouldBeLessThan(idxIssuingSystem, "UUID must precede IssuingSystem");
        idxIssuingSystem.ShouldBeLessThan(idxIssueDate, "IssuingSystem must precede IssueDate");
        idxIssueDate.ShouldBeLessThan(idxVatApplicable, "IssueDate must precede VATApplicable");
        idxVatApplicable.ShouldBeLessThan(idxLocalCurrency, "VATApplicable must precede LocalCurrencyCode");
        idxLocalCurrency.ShouldBeLessThan(idxSupplier, "LocalCurrencyCode must precede AccountingSupplierParty");
    }

    // -------------------------------------------------------------------------
    // IssuingSystem element must always be present
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_IssuingSystem_IsAlwaysFakvio()
    {
        // The IssuingSystem element identifies the software that produced the
        // ISDOC document -- must always be "Fakvio".
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "IssuingSystem").First().Value.ShouldBe("Fakvio");
    }

    // -------------------------------------------------------------------------
    // TaxPointDate (DUZP): present when set, absent when null
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_WithTaxableSupplyDate_EmitsTaxPointDate()
    {
        var inv = BuildMinimalInvoice();
        inv.TaxableSupplyDate = new DateTime(2026, 3, 10);
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "TaxPointDate").First().Value.ShouldBe("2026-03-10");
    }

    [Fact]
    public void Map_WithoutTaxableSupplyDate_NoTaxPointDateElement()
    {
        var inv = BuildMinimalInvoice();
        inv.TaxableSupplyDate = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        // TaxPointDate is optional in the XSD (minOccurs="0") -- must be omitted
        doc.Descendants(ns + "TaxPointDate").ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // VATApplicable: reflects issuer's VAT payer status
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_VatPayerIssuer_EmitsVATApplicableTrue()
    {
        var inv = BuildMinimalInvoice();
        inv.Issuer!.IsVatPayer = true;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "VATApplicable").First().Value.ShouldBe("true");
    }

    [Fact]
    public void Map_NonVatPayerIssuer_EmitsVATApplicableFalse()
    {
        var inv = BuildMinimalInvoice();
        inv.Issuer!.IsVatPayer = false;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "VATApplicable").First().Value.ShouldBe("false");
    }

    // -------------------------------------------------------------------------
    // CZK invoice with only Notes (no foreign currency) has a single Note
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_CzkInvoiceWithNotes_EmitsSingleNoteElement()
    {
        // For a CZK invoice with user notes there must be exactly one Note
        // element (no ForeignCurrencyNote appended for domestic invoices).
        var inv = BuildMinimalInvoice();
        inv.Notes = "Dekujeme za objednavku";
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var notes = doc.Descendants(ns + "Note").ToList();
        notes.Count.ShouldBe(1);
        notes[0].Value.ShouldBe("Dekujeme za objednavku");
    }

    // -------------------------------------------------------------------------
    // CZK invoice without Notes has no Note element at all
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_CzkInvoiceWithoutNotes_NoNoteElement()
    {
        var inv = BuildMinimalInvoice();
        inv.Notes = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "Note").ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // IssueDate formatting: must always use ISO 8601 (yyyy-MM-dd)
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_IssueDate_FormattedAsIso8601()
    {
        var inv = BuildMinimalInvoice();
        inv.IssueDate = new DateTime(2026, 12, 31);
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        doc.Descendants(ns + "IssueDate").First().Value.ShouldBe("2026-12-31");
    }

    // -------------------------------------------------------------------------
    // ExportInvoiceAsync: cancellation is respected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExportInvoiceAsync_WithAlreadyCancelledToken_ThrowsOperationCanceledException()
    {
        // A pre-cancelled token should prevent the async DB query from starting.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.ExportInvoiceAsync(1, cts.Token));
    }

    // -------------------------------------------------------------------------
    // XML root element: version attribute must be "6.0.2"
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_RootElement_HasVersion602Attribute()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);

        var root = doc.Root!;
        root.Name.LocalName.ShouldBe("Invoice");
        root.Attribute("version")!.Value.ShouldBe("6.0.2");
    }

    // -------------------------------------------------------------------------
    // IBAN-only bank transfer: BIC emitted as empty (required by XSD BankAccount group)
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_BankTransfer_IbanWithoutSwift_EmitsIbanAndEmptyBic()
    {
        var inv = BuildMinimalInvoice();
        inv.IBAN = "CZ6508000000192000145399";
        inv.SWIFT = null;
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var details = doc.Descendants(ns + "Details").First();
        details.Element(ns + "IBAN")!.Value.ShouldBe("CZ6508000000192000145399");
        details.Element(ns + "BIC")!.Value.ShouldBe(string.Empty);
    }

    // -------------------------------------------------------------------------
    // Contact: phone and email from Client.Contact collection
    // -------------------------------------------------------------------------

    [Fact]
    public void Map_IssuerWithContacts_EmitsContactElement()
    {
        var inv = BuildMinimalInvoice();
        inv.Issuer!.Contact = new List<Contact>
        {
            new Contact { ContactType = EContactType.Phone, ContactValue = "+420123456789", Label = "Jan Novak" },
            new Contact { ContactType = EContactType.Email, ContactValue = "info@fakvio.cz" }
        };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var supplier = doc.Descendants(ns + "AccountingSupplierParty").First();
        var contact = supplier.Descendants(ns + "Contact").First();
        contact.Element(ns + "Name")!.Value.ShouldBe("Jan Novak");
        contact.Element(ns + "Telephone")!.Value.ShouldBe("+420123456789");
        contact.Element(ns + "ElectronicMail")!.Value.ShouldBe("info@fakvio.cz");
    }

    [Fact]
    public void Map_ClientWithContacts_EmitsContactElement()
    {
        var inv = BuildMinimalInvoice();
        inv.Client!.Contact = new List<Contact>
        {
            new Contact { ContactType = EContactType.Email, ContactValue = "objednavky@odberatel.cz" }
        };
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var customer = doc.Descendants(ns + "AccountingCustomerParty").First();
        var contact = customer.Descendants(ns + "Contact").First();
        contact.Element(ns + "ElectronicMail")!.Value.ShouldBe("objednavky@odberatel.cz");
        contact.Element(ns + "Telephone").ShouldBeNull();
    }

    [Fact]
    public void Map_NoContacts_NoContactElement()
    {
        var inv = BuildMinimalInvoice();
        var doc = IsdocMapper.Map(inv);
        var ns = XNamespace.Get("http://isdoc.cz/namespace/2013");

        var supplier = doc.Descendants(ns + "AccountingSupplierParty").First();
        supplier.Descendants(ns + "Contact").ShouldBeEmpty();
    }

    [Fact]
    public void Map_IssuerWithContacts_StillValidatesAgainstXsd()
    {
        var inv = BuildMinimalInvoice();
        inv.Issuer!.Contact = new List<Contact>
        {
            new Contact { ContactType = EContactType.Phone, ContactValue = "+420111222333", Label = "Reception" },
            new Contact { ContactType = EContactType.Email, ContactValue = "fakturace@fakvio.cz" }
        };
        inv.Client!.Contact = new List<Contact>
        {
            new Contact { ContactType = EContactType.Email, ContactValue = "platby@odberatel.cz" }
        };
        var doc = IsdocMapper.Map(inv);

        var errors = GetXsdErrors(doc);
        errors.ShouldBeEmpty($"XSD validation errors:\n{string.Join("\n", errors)}");
    }
}
