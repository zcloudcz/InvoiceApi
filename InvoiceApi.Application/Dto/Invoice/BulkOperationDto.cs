namespace InvoiceApi.Application.Dto.Invoice;

/// <summary>
/// Request DTO for bulk invoice operations.
/// Contains a list of invoice IDs to process in a single batch.
/// Used by endpoints like /bulk/complete, /bulk/mark-paid, /bulk/delete, /bulk/send-email.
/// </summary>
public class BulkOperationRequest
{
    /// <summary>
    /// List of invoice IDs to include in the bulk operation.
    /// The service will validate each invoice individually and collect errors.
    /// </summary>
    public List<long> InvoiceIds { get; set; } = new();
}

/// <summary>
/// Result DTO for bulk invoice operations.
/// Reports how many succeeded, how many failed, and details about each failure.
/// This allows the UI to show partial success messages (e.g., "5 of 7 invoices completed").
/// </summary>
public class BulkOperationResult
{
    /// <summary>
    /// Number of invoices that were processed successfully.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of invoices that failed to process.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Detailed error information for each failed invoice.
    /// Empty when all invoices succeed.
    /// </summary>
    public List<BulkOperationError> Errors { get; set; } = new();
}

/// <summary>
/// Error detail for a single invoice that failed during a bulk operation.
/// Allows the UI to show which specific invoices had problems and why.
/// </summary>
public class BulkOperationError
{
    /// <summary>
    /// The ID of the invoice that failed.
    /// </summary>
    public long InvoiceId { get; set; }

    /// <summary>
    /// Human-readable error message explaining why this invoice failed.
    /// Example: "Invoice is already Completed" or "Only draft invoices can be deleted".
    /// </summary>
    public string Error { get; set; } = string.Empty;
}
