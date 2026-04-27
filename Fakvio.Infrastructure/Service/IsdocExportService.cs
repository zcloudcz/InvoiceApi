using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service.Isdoc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Generates ISDOC 6.0.2 XML exports from invoices stored in the tenant database.
/// Loads the invoice with related data, delegates mapping to IsdocMapper, then
/// serialises and optionally validates the output against the embedded XSD.
/// </summary>
public class IsdocExportService : IIsdocExportService
{
    private readonly TenantDbContext _db;
    private readonly ILogger<IsdocExportService> _logger;

    // Lazy-loaded XSD schema -- loaded once per instance from the embedded resource.
    private XmlSchemaSet? _schemaSet;

    public IsdocExportService(TenantDbContext db, ILogger<IsdocExportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]> ExportInvoiceAsync(long invoiceId, CancellationToken ct = default)
    {
        _logger.LogInformation("Starting ISDOC export for invoice {InvoiceId}", invoiceId);

        // Load invoice with all related data needed for the ISDOC document.
        // Note: navigation props are InvoiceItem (not InvoiceItems), Address (not Addresses)
        var invoice = await _db.Invoice
            .AsNoTracking()
            .AsSplitQuery()
            .Include(i => i.Currency)
            .Include(i => i.InvoiceItem)
            .Include(i => i.Issuer)
                .ThenInclude(c => c!.Address)
            .Include(i => i.Client)
                .ThenInclude(c => c!.Address)
            .FirstOrDefaultAsync(i => i.Id == invoiceId, ct);

        if (invoice == null)
        {
            _logger.LogError("Invoice {InvoiceId} not found for ISDOC export", invoiceId);
            throw new KeyNotFoundException($"Invoice {invoiceId} not found.");
        }

        var document = IsdocMapper.Map(invoice);
        await ValidateAgainstSchemaAsync(document, invoiceId);
        return SerialiseToBytes(document);
    }

    private async Task ValidateAgainstSchemaAsync(XDocument document, long invoiceId)
    {
        try
        {
            var schemas = await GetSchemaSetAsync();
            var errors = new List<string>();
            document.Validate(schemas, (_, e) => errors.Add(e.Message));

            if (errors.Count > 0)
                _logger.LogWarning(
                    "ISDOC XSD validation for invoice {InvoiceId} produced {Count} issue(s): {Errors}",
                    invoiceId, errors.Count, string.Join("; ", errors));
            else
                _logger.LogDebug("ISDOC XSD validation passed for invoice {InvoiceId}", invoiceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not load ISDOC XSD schema for validation of invoice {InvoiceId}", invoiceId);
        }
    }

    private Task<XmlSchemaSet> GetSchemaSetAsync()
    {
        if (_schemaSet != null) return Task.FromResult(_schemaSet);

        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Fakvio.Infrastructure.Resources.Isdoc.isdoc-6.0.2.xsd";

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource {resourceName} not found in assembly {assembly.GetName().Name}.");

        var schemaSet = new XmlSchemaSet();
        schemaSet.Add("http://isdoc.cz/namespace/2013", XmlReader.Create(stream));
        schemaSet.Compile();

        _schemaSet = schemaSet;
        return Task.FromResult(_schemaSet);
    }

    private static byte[] SerialiseToBytes(XDocument document)
    {
        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
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