using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.RecognizedCounterparty;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// CRUD for the recognized-counterparty registry.
///
/// After creating or updating an ACTIVE entry the service asks the payment
/// matcher to re-scan Unmatched transactions — the user immediately sees how
/// many historical payments the new rule recognized (snackbar in the UI).
/// No cycle: PaymentMatchingService does not depend on this service.
/// </summary>
public class RecognizedCounterpartyService : IRecognizedCounterpartyService
{
    private readonly TenantDbContext _context;
    private readonly IPaymentMatchingService _paymentMatcher;
    private readonly ILogger<RecognizedCounterpartyService> _logger;

    public RecognizedCounterpartyService(
        TenantDbContext context,
        IPaymentMatchingService paymentMatcher,
        ILogger<RecognizedCounterpartyService> logger)
    {
        _context = context;
        _paymentMatcher = paymentMatcher;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<RecognizedCounterpartyDto>> GetAllAsync(CancellationToken ct = default)
    {
        var entries = await _context.RecognizedCounterparty
            .AsNoTracking()
            .OrderBy(r => r.Label)
            .ToListAsync(ct);

        return entries.Select(e => e.ToRecognizedCounterpartyDto()).ToList();
    }

    /// <inheritdoc />
    public async Task<RecognizedCounterpartyDto?> GetByIdAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.RecognizedCounterparty
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, ct);

        return entity?.ToRecognizedCounterpartyDto();
    }

    /// <inheritdoc />
    public async Task<SaveRecognizedCounterpartyResponse> CreateAsync(
        SaveRecognizedCounterpartyRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);

        var entity = request.ToRecognizedCounterparty();
        _context.RecognizedCounterparty.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "RecognizedCounterparty created: {Id} '{Label}' account {Account}",
            entity.Id, entity.Label, entity.CounterpartyAccount);

        var recognized = entity.IsActive ? await _paymentMatcher.RescanUnmatchedAsync(ct) : 0;

        return new SaveRecognizedCounterpartyResponse
        {
            Entry = entity.ToRecognizedCounterpartyDto(),
            RecognizedCount = recognized,
        };
    }

    /// <inheritdoc />
    public async Task<SaveRecognizedCounterpartyResponse?> UpdateAsync(
        long id, SaveRecognizedCounterpartyRequest request, CancellationToken ct = default)
    {
        ValidateRequest(request);

        var entity = await _context.RecognizedCounterparty
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity == null)
            return null;

        entity.Label = request.Label;
        entity.CounterpartyAccount = request.CounterpartyAccount;
        entity.VariableSymbol = request.VariableSymbol;
        entity.SpecificSymbol = request.SpecificSymbol;
        entity.ConstantSymbol = request.ConstantSymbol;
        entity.Category = request.Category;
        entity.Note = request.Note;
        entity.IsActive = request.IsActive;

        // Deactivating (or tightening) an entry may leave stale recognitions —
        // reset its transactions so the rescan below (or the user) re-decides them.
        if (!entity.IsActive)
            await ResetRecognizedTransactionsAsync(entity.Id, ct);

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("RecognizedCounterparty updated: {Id} '{Label}'", entity.Id, entity.Label);

        var recognized = entity.IsActive ? await _paymentMatcher.RescanUnmatchedAsync(ct) : 0;

        return new SaveRecognizedCounterpartyResponse
        {
            Entry = entity.ToRecognizedCounterpartyDto(),
            RecognizedCount = recognized,
        };
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        var entity = await _context.RecognizedCounterparty
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (entity == null)
            return false;

        // Reset transactions recognized by this entry BEFORE deleting — the DB
        // FK is SetNull, but MatchStatus must go back to Unmatched too.
        await ResetRecognizedTransactionsAsync(id, ct);

        _context.RecognizedCounterparty.Remove(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("RecognizedCounterparty deleted: {Id} '{Label}'", id, entity.Label);
        return true;
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Resets all transactions assigned to the given registry entry back to
    /// Unmatched (clears the FK). Used on delete and on deactivation.
    /// </summary>
    private async Task ResetRecognizedTransactionsAsync(long recognizedCounterpartyId, CancellationToken ct)
    {
        var affected = await _context.BankTransaction
            .Where(t => t.RecognizedCounterpartyId == recognizedCounterpartyId)
            .ToListAsync(ct);

        foreach (var tx in affected)
        {
            tx.RecognizedCounterpartyId = null;
            if (tx.MatchStatus == EMatchStatus.Recognized)
                tx.MatchStatus = EMatchStatus.Unmatched;
        }

        if (affected.Count > 0)
            _logger.LogInformation(
                "Reset {Count} transactions previously recognized by RecognizedCounterparty {Id}",
                affected.Count, recognizedCounterpartyId);
    }

    /// <summary>Minimal validation — Label and CounterpartyAccount are mandatory.</summary>
    private static void ValidateRequest(SaveRecognizedCounterpartyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
            throw new ArgumentException("Label is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.CounterpartyAccount))
            throw new ArgumentException("CounterpartyAccount is required.", nameof(request));
    }
}
