using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Application.Service;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.Isdoc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Generates ISDOC 6.0.2 XML exports from invoices stored in the tenant database.
/// Loads the invoice with all related data, delegates XML mapping to IsdocMapper,
/// then serialises and validates the output against the embedded XSD.
/// </summary>
public class IsdocExportService : IIsdocExportService
{
    private readonly TenantDbContext _db;
    private readonly ILogger<IsdocExportService> _logger;

    /// <summary>
    /// Static (process-wide) XSD schema cache.
    /// The schema is expensive to parse but never changes at runtime,
    /// so we load it once and reuse it across all requests / service instances.
    /// Lazy&lt;T&gt; is thread-safe by default (LazyThreadSafetyMode.ExecutionAndPublication).
    /// </summary>
    private static readonly Lazy<XmlSchemaSet> SchemaSetCache =
        new(LoadSchemaSet, isThreadSafe: true);

    public IsdocExportService(TenantDbContext db, ILogger<IsdocExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting ISDOC export for invoice {InvoiceId}", invoiceId);

        // Load invoice with all navigation properties needed for the XML document.
        // AsNoTracking: read-only operation, no change tracking needed.
        // AsSplitQuery: avoids cartesian explosion when multiple collections are included.
        // Status filter: soft-deleted invoices (Status=Deleted) must not be exported.
        var invoice = await _db.Invoice
            .AsNoTracking()
            .AsSplitQuery()
            .Where(i => i.Status != EInvoiceStatus.Deleted)
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
                .ThenInclude(item => item.ReverseChargeCode)
            .Include(i => i.Issuer)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Issuer)
                .ThenInclude(c => c!.Contact)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Contact)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
        {
            _logger.LogError("Invoice {InvoiceId} not found for ISDOC export", invoiceId);
            throw new KeyNotFoundException($"Invoice {invoiceId} not found.");
        }

        var document = IsdocMapper.Map(invoice);
        ValidateAgainstSchema(document, invoiceId);
        return SerialiseToBytes(document);
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportReceivedInvoiceAsync(long receivedInvoiceId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting ISDOC export for received invoice {ReceivedInvoiceId}", receivedInvoiceId);

        // Same loading strategy as issued invoices: read-only, split query,
        // soft-deleted documents excluded.
        var invoice = await _db.ReceivedInvoice
            .AsNoTracking()
            .AsSplitQuery()
            .Where(i => i.Status != EReceivedInvoiceStatus.Deleted)
            .Include(i => i.Currency)
            .Include(i => i.Items)
            .Include(i => i.Supplier)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Supplier)
                .ThenInclude(c => c!.Contact)
            .FirstOrDefaultAsync(i => i.Id == receivedInvoiceId, ct);

        if (invoice == null)
        {
            _logger.LogError("Received invoice {ReceivedInvoiceId} not found for ISDOC export", receivedInvoiceId);
            throw new KeyNotFoundException($"Received invoice {receivedInvoiceId} not found.");
        }

        // The customer party on a received invoice is the tenant's own company.
        // There is exactly one issuer record per tenant DB (IsIssuer = true);
        // when missing (misconfigured tenant) we still export with an empty party.
        var ourCompany = await _db.Client
            .AsNoTracking()
            .Include(c => c.Address)
            .Include(c => c.Contact)
            .FirstOrDefaultAsync(c => c.IsIssuer, ct);

        if (ourCompany == null)
            _logger.LogWarning(
                "No issuer client (IsIssuer = true) found — ISDOC customer party for received invoice {ReceivedInvoiceId} will be empty",
                receivedInvoiceId);

        var document = IsdocMapper.Map(invoice, ourCompany);
        ValidateAgainstSchema(document, receivedInvoiceId);
        return SerialiseToBytes(document);
    }

    // --------------------------------------------------------------------------
    // XSD validation (non-blocking: warns but does not throw)
    // --------------------------------------------------------------------------

    private void ValidateAgainstSchema(XDocument document, long invoiceId)
    {
        try
        {
            var schemas = SchemaSetCache.Value;
            var errors = new List<string>();
            document.Validate(schemas, (_, e) => errors.Add(e.Message));

            if (errors.Count > 0)
                _logger.LogWarning(
                    "ISDOC XSD validation for invoice {InvoiceId} produced {Count} issue(s): {Errors}",
                    invoiceId, errors.Count, string.Join("; ", errors));
            else
                _logger.LogDebug(
                    "ISDOC XSD validation passed for invoice {InvoiceId}", invoiceId);
        }
        catch (Exception ex)
        {
            // Schema load failure is non-fatal: log and continue with the export.
            _logger.LogWarning(ex,
                "Could not load ISDOC XSD schema for validation of invoice {InvoiceId}", invoiceId);
        }
    }

    // --------------------------------------------------------------------------
    // Schema loading (called once by Lazy<T>)
    // --------------------------------------------------------------------------

    /// <summary>
    /// Loads and compiles the embedded ISDOC 6.0.2 XSD schema.
    /// Called exactly once per process by <see cref="SchemaSetCache"/>.
    /// </summary>
    internal static XmlSchemaSet LoadSchemaSet()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Fakvio.Infrastructure.Resources.Isdoc.isdoc-6.0.2.xsd";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found in assembly " +
                $"'{assembly.GetName().Name}'. " +
                "Check that the file is marked as EmbeddedResource in the csproj.");

        var schemaSet = new XmlSchemaSet();
        schemaSet.Add("http://isdoc.cz/namespace/2013", XmlReader.Create(stream));
        schemaSet.Compile();
        return schemaSet;
    }

    // --------------------------------------------------------------------------
    // Serialisation
    // --------------------------------------------------------------------------

    private static byte[] SerialiseToBytes(XDocument document)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            // UTF-8 without BOM so the file can be opened by any XML parser
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  "
        };
        using (var writer = XmlWriter.Create(ms, settings))
        {
            document.WriteTo(writer);
        }
        return ms.ToArray();
    }
}
