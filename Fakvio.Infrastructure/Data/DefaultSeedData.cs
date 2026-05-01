using System.Reflection;

namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Shared seed data used by both MasterDbContext and TenantDbContext.
/// This static class provides default template HTML that is loaded from
/// embedded resource files (.html) shipped inside the assembly.
///
/// Templates are read once (lazy-loaded, thread-safe) and then cached
/// for the lifetime of the application — no repeated I/O on every call.
///
/// The invoice PDF template matches the reference design (doc-templates-data/faktura.png):
/// - DODAVATEL (supplier) section at top with contact details
/// - Blue header bar with document type label + variable symbol + document number
/// - ODBĚRATEL (client) section with dates on the right
/// - Items table: POPIS POLOŽKY | MJ | DPH | POČET | CENA/MJ | CELKEM BEZ DPH
/// - VAT breakdown: SAZBA | ZÁKLAD | DPH
/// - Grand total: CELKEM K ÚHRADĚ
/// - QR code for payment (if available)
/// </summary>
public static class DefaultSeedData
{
    // ── Lazy-loaded cache for embedded resource templates ──
    // Each template is read from the assembly's embedded resources exactly once.
    // Lazy<T> with LazyThreadSafetyMode.ExecutionAndPublication ensures thread safety
    // — even if multiple threads call the getter simultaneously, the resource
    // is read only once and the result is shared.

    private static readonly Lazy<string> InvoicePdfTemplateHtml = new(
        () => ReadEmbeddedResource("Fakvio.Infrastructure.Templates.InvoicePdfTemplate.html"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> CreditNotePdfTemplateHtml = new(
        () => ReadEmbeddedResource("Fakvio.Infrastructure.Templates.CreditNotePdfTemplate.html"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> AdvanceInvoicePdfTemplateHtml = new(
        () => ReadEmbeddedResource("Fakvio.Infrastructure.Templates.AdvanceInvoicePdfTemplate.html"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<string> TaxReceiptForAdvancePdfTemplateHtml = new(
        () => ReadEmbeddedResource("Fakvio.Infrastructure.Templates.TaxReceiptForAdvancePdfTemplate.html"),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Returns the default invoice PDF HTML template for seed data.
    /// Uses Handlebars-style {{placeholders}} that are replaced at runtime
    /// by PdfExportService when generating actual PDF documents.
    ///
    /// The template uses table-based layout (not CSS flex/grid) because
    /// iText7 pdfhtml has limited CSS support — tables render reliably.
    ///
    /// Color scheme: Steel blue (#5B7D9D) for headers and accent elements.
    /// </summary>
    public static string GetDefaultInvoicePdfTemplate()
    {
        return InvoicePdfTemplateHtml.Value;
    }

    /// <summary>
    /// Returns the default credit note PDF HTML template.
    /// Same layout as the invoice template, but uses a warm red (#A05050)
    /// accent color to visually distinguish credit notes from invoices.
    /// </summary>
    public static string GetDefaultCreditNotePdfTemplate()
    {
        return CreditNotePdfTemplateHtml.Value;
    }

    /// <summary>
    /// Returns the default advance invoice (pro-forma) PDF HTML template.
    /// Uses a green (#3D7A4A) accent and includes a banner that explicitly
    /// states this is NOT a tax document (zálohová faktura — není daňový doklad).
    /// </summary>
    public static string GetDefaultAdvanceInvoicePdfTemplate()
    {
        return AdvanceInvoicePdfTemplateHtml.Value;
    }

    /// <summary>
    /// Returns the default tax receipt for advance payment PDF HTML template.
    /// Uses a purple (#6A3D9A) accent to distinguish it from regular invoices.
    /// This IS a VAT tax document — issued after the advance payment is received.
    /// </summary>
    public static string GetDefaultTaxReceiptForAdvancePdfTemplate()
    {
        return TaxReceiptForAdvancePdfTemplateHtml.Value;
    }

    /// <summary>
    /// Reads an embedded resource from this assembly and returns its content as a string.
    /// Embedded resources are compiled into the DLL — they are NOT separate files on disk.
    /// The resource name follows the pattern: {DefaultNamespace}.{FolderPath}.{FileName}
    /// where folder separators become dots (e.g., Templates/Foo.html → Templates.Foo.html).
    /// </summary>
    /// <param name="resourceName">
    /// The fully-qualified resource name, e.g. "Fakvio.Infrastructure.Templates.InvoicePdfTemplate.html"
    /// </param>
    /// <returns>The full text content of the embedded resource file.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the resource is not found — this indicates a build configuration issue
    /// (missing EmbeddedResource entry in .csproj or wrong resource name).
    /// </exception>
    private static string ReadEmbeddedResource(string resourceName)
    {
        // Get the assembly that contains DefaultSeedData (Fakvio.Infrastructure.dll)
        var assembly = Assembly.GetExecutingAssembly();

        // Open a stream to the embedded resource
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{resourceName}' not found in assembly '{assembly.FullName}'. " +
                $"Ensure the file exists and is marked as EmbeddedResource in the .csproj. " +
                $"Available resources: [{string.Join(", ", assembly.GetManifestResourceNames())}]");

        // Read the entire stream as UTF-8 text
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
