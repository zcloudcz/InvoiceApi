namespace Fakvio.Domain.Enums;

/// <summary>
/// The document types WE issue (everything in <see cref="EDocumentType"/> except
/// <see cref="EDocumentType.ReceivedInvoice"/>).
///
/// Why this exists: issued and received documents share one table and one enum, but every "issued" write
/// path (create invoice, invoice template, number sequence) and every tool that lists the valid values must
/// refuse the received type. A received document created through the issued rules would get a number from our
/// sequence, land in revenue / output VAT, and then be invisible to the issued read side.
/// Use this class instead of <c>Enum.GetNames&lt;EDocumentType&gt;()</c> / <c>Enum.TryParse</c> at those places.
/// </summary>
public static class IssuedDocumentTypes
{
    /// <summary>Names of the issued types, in enum order - for "valid values" lists shown to callers.</summary>
    public static readonly string[] Names = Enum.GetValues<EDocumentType>()
        .Where(t => t != EDocumentType.ReceivedInvoice)
        .Select(t => t.ToString())
        .ToArray();

    /// <summary>
    /// True for a type we issue. False for <see cref="EDocumentType.ReceivedInvoice"/> and for a number that is
    /// not an enum member at all (JSON and <c>Enum.TryParse</c> both accept e.g. <c>99</c>).
    /// </summary>
    public static bool IsIssued(this EDocumentType type) =>
        type != EDocumentType.ReceivedInvoice && Enum.IsDefined(type);

    /// <summary>Parses a name typed by a caller (case-insensitive) and accepts only an issued type.</summary>
    public static bool TryParse(string? text, out EDocumentType type) =>
        Enum.TryParse(text?.Trim(), ignoreCase: true, out type) && type.IsIssued();

    /// <summary>
    /// Guard for the issued write paths. Throws <see cref="InvalidOperationException"/> because that is what the
    /// services already throw for invalid input and what the controllers translate to HTTP 400.
    /// </summary>
    public static void EnsureIssued(EDocumentType type)
    {
        if (!type.IsIssued())
            throw new InvalidOperationException(
                $"Document type '{type}' cannot be used here. Valid values: {string.Join(", ", Names)}.");
    }

    /// <summary>Validation message for <see cref="IsValidVariableSymbol"/> (the text API clients already know).</summary>
    public const string VariableSymbolError = "Variable symbol must contain only digits (max 10)";

    /// <summary>
    /// Variable symbol rule of the documents WE issue: digits only, at most 10 (Czech banking). Null / empty is
    /// fine (the symbol is then derived from the document number). It deliberately does NOT apply to a received
    /// invoice: that symbol is whatever the supplier printed (e.g. <c>INV-2026-0042</c>), we only store it.
    /// </summary>
    public static bool IsValidVariableSymbol(string? variableSymbol) =>
        variableSymbol is null || System.Text.RegularExpressions.Regex.IsMatch(variableSymbol, @"^\d{0,10}\z");
}
