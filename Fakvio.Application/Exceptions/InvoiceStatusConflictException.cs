namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown when the invoice status no longer matches what the caller expected
/// (<c>UpdateInvoiceDto.ExpectedStatus</c>) — someone changed it in between. The API maps it to 409.
/// Derives from InvalidOperationException so callers that only know "business rule violated" still handle it.
/// </summary>
public sealed class InvoiceStatusConflictException(string message) : InvalidOperationException(message);
