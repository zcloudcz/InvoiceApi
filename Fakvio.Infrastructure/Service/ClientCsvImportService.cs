using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Import;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Implementation of <see cref="IClientCsvImportService"/>.
///
/// Column mapping doesn't distinguish between Fakturoid and iDoklad exports by a format code —
/// it just tries every known alias for each field (see <see cref="FieldAliases"/>). This keeps
/// the mapping simple and works for either source (and for a hand-edited CSV) without a "which
/// tool did this come from" setting the user has to get right.
/// </summary>
public class ClientCsvImportService : IClientCsvImportService
{
    private readonly IClientService _clientService;
    private readonly ILogger<ClientCsvImportService> _logger;

    public ClientCsvImportService(IClientService clientService, ILogger<ClientCsvImportService> logger)
    {
        _clientService = clientService;
        _logger = logger;
    }

    /// <summary>
    /// Maps our <see cref="CreateClientDto"/> fields to the CSV header names (already normalized
    /// by <see cref="CsvTable.NormalizeHeader"/> — trimmed, lower-cased, diacritics stripped) that
    /// Fakturoid, iDoklad, or a hand-made export might use for them.
    /// Add new aliases here as real exports turn up new spellings — see DEVGUIDE.md §4.13.
    /// </summary>
    private static readonly Dictionary<string, string[]> FieldAliases = new()
    {
        ["CompanyName"] = ["nazev", "nazev firmy", "firma", "company", "company name", "jmeno", "name", "subjekt", "klient"],
        ["RegistrationNumber"] = ["ico", "ic", "ico/rc", "registration number", "registrationnumber"],
        ["TaxNumber"] = ["dic", "vat number", "taxnumber", "tax number"],
        ["Street"] = ["ulice", "adresa", "street", "ulice a cislo popisne"],
        ["City"] = ["mesto", "obec", "city"],
        ["PostalCode"] = ["psc", "zip", "postal code", "postalcode"],
        ["Country"] = ["zeme", "country", "stat"],
        ["Email"] = ["email", "e-mail", "e mail", "mail"],
        ["Phone"] = ["telefon", "phone", "tel", "mobil"],
        ["Iban"] = ["iban"],
        ["AccountNumber"] = ["cislo uctu", "account number", "bankaccount", "ucet", "c. uctu", "cislo bankovniho uctu"],
    };

    public async Task<ClientImportPreviewDto> PreviewAsync(Stream csvStream, CancellationToken ct = default)
    {
        var table = CsvTable.Parse(csvStream); // throws CsvParseException — let the caller turn that into a 400

        var headerToField = BuildHeaderToFieldMap(table.Headers, out var unknownColumns);
        var result = new ClientImportPreviewDto { UnknownColumns = unknownColumns };

        var mappedRows = table.Rows.Select((row, i) => (RowNumber: i + 2, Client: MapRowToClient(row, headerToField))).ToList();

        // Single bulk existence check instead of one DB round trip per row (up to CsvTable.MaxRowCount rows).
        var existingByRegistrationNumber = await _clientService.GetClientIdsByRegistrationNumbersAsync(
            mappedRows.Select(r => r.Client.RegistrationNumber!), ct);

        // IČOs seen so far in this file — the first occurrence is New, later ones are Duplicate.
        var seenRegistrationNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rowNumber, client) in mappedRows)
        {
            if (string.IsNullOrWhiteSpace(client.CompanyName))
            {
                result.Rows.Add(new ClientImportPreviewRowDto
                {
                    RowNumber = rowNumber,
                    Client = client,
                    Status = EClientImportRowStatus.Invalid,
                    Reason = "Missing company name."
                });
                continue;
            }

            // The DB requires RegistrationNumber (unique, non-null) even though CreateClientDto
            // documents it as optional for physical persons — see DEVGUIDE.md §4.13 deviation note.
            if (string.IsNullOrWhiteSpace(client.RegistrationNumber))
            {
                result.Rows.Add(new ClientImportPreviewRowDto
                {
                    RowNumber = rowNumber,
                    Client = client,
                    Status = EClientImportRowStatus.Invalid,
                    Reason = "Missing IČO (registration number) — required by this system even for individuals."
                });
                continue;
            }

            if (!seenRegistrationNumbers.Add(client.RegistrationNumber))
            {
                result.Rows.Add(new ClientImportPreviewRowDto
                {
                    RowNumber = rowNumber,
                    Client = client,
                    Status = EClientImportRowStatus.Duplicate,
                    Reason = $"IČO {client.RegistrationNumber} appears more than once in this file."
                });
                continue;
            }

            if (existingByRegistrationNumber.TryGetValue(client.RegistrationNumber, out var existingId))
            {
                result.Rows.Add(new ClientImportPreviewRowDto
                {
                    RowNumber = rowNumber,
                    Client = client,
                    Status = EClientImportRowStatus.Duplicate,
                    Reason = $"A client with IČO {client.RegistrationNumber} already exists.",
                    ExistingClientId = existingId
                });
                continue;
            }

            result.Rows.Add(new ClientImportPreviewRowDto
            {
                RowNumber = rowNumber,
                Client = client,
                Status = EClientImportRowStatus.New
            });
        }

        return result;
    }

    public async Task<ClientImportResultDto> ConfirmAsync(ClientImportConfirmDto request, CancellationToken ct = default)
    {
        var result = new ClientImportResultDto();
        var seenRegistrationNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Same bulk pre-check as PreviewAsync: one query instead of one per row. This closes the
        // preview→confirm TOCTOU window for anything that changed *before* this batch started
        // running; a genuine concurrent import of the same IČO in the middle of this loop is still
        // possible (ClientService.CreateClientAsync does its own check-then-insert) — handled below
        // by treating that specific failure as Skipped rather than a hard error.
        // ponytail: residual race is rare (two users importing the same contact at the same instant)
        // and CreateClientAsync's own guard already prevents duplicate rows; a fully atomic
        // upsert would need a dedicated repository method, out of scope for this task.
        var existingByRegistrationNumber = await _clientService.GetClientIdsByRegistrationNumbersAsync(
            request.Clients.Select(c => c.RegistrationNumber!), ct);

        foreach (var client in request.Clients)
        {
            if (!string.IsNullOrWhiteSpace(client.RegistrationNumber))
            {
                if (!seenRegistrationNumbers.Add(client.RegistrationNumber) ||
                    existingByRegistrationNumber.ContainsKey(client.RegistrationNumber))
                {
                    result.SkippedCount++;
                    continue;
                }
            }

            try
            {
                client.FetchFromAres = false; // CSV data is authoritative here — don't overwrite it with an ARES lookup
                var created = await _clientService.CreateClientAsync(client, ct);
                result.CreatedCount++;
                result.CreatedClientIds.Add(created.Id);
            }
            catch (InvalidOperationException)
            {
                // ClientService.CreateClientAsync's own "already exists" guard caught a race our
                // bulk pre-check missed (import ran concurrently with another insert of the same IČO).
                // That's a dedup outcome, not a failure — count and report it the same way.
                result.SkippedCount++;
            }
            catch (Exception ex)
            {
                // Log the full exception server-side (may contain EF/PostgreSQL details), but never
                // forward ex.Message to the caller — it can leak schema/infrastructure information.
                _logger.LogWarning(ex, "Failed to import client {CompanyName} from CSV", client.CompanyName);
                result.Errors.Add($"{client.CompanyName}: import failed — see server log for details.");
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves each CSV header to one of our known fields (or null if unrecognized).
    /// Unknown columns are collected for display but otherwise ignored (per DEVGUIDE.md §4.13).
    /// </summary>
    private static Dictionary<string, string> BuildHeaderToFieldMap(IReadOnlyList<string> headers, out List<string> unknownColumns)
    {
        var map = new Dictionary<string, string>();
        unknownColumns = [];

        foreach (var header in headers)
        {
            var field = FieldAliases.FirstOrDefault(kv => kv.Value.Contains(header)).Key;
            if (field != null)
            {
                map[header] = field;
            }
            else
            {
                unknownColumns.Add(header);
            }
        }

        return map;
    }

    private static CreateClientDto MapRowToClient(IReadOnlyDictionary<string, string> row, Dictionary<string, string> headerToField)
    {
        var values = new Dictionary<string, string>();
        foreach (var (header, field) in headerToField)
        {
            if (row.TryGetValue(header, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                values[field] = value.Trim();
            }
        }

        var client = new CreateClientDto
        {
            CompanyName = values.GetValueOrDefault("CompanyName", string.Empty),
            RegistrationNumber = values.GetValueOrDefault("RegistrationNumber"),
            TaxNumber = values.GetValueOrDefault("TaxNumber"),
            IsVatPayer = values.ContainsKey("TaxNumber"),
            FetchFromAres = false
        };

        if (values.ContainsKey("Street") || values.ContainsKey("City") || values.ContainsKey("PostalCode"))
        {
            client.Address.Add(new CreateAddressDto
            {
                AddressType = EAddressType.Primary,
                Street = values.GetValueOrDefault("Street", string.Empty),
                City = values.GetValueOrDefault("City", string.Empty),
                PostalCode = values.GetValueOrDefault("PostalCode", string.Empty),
                Country = values.GetValueOrDefault("Country", "Czech Republic"),
                IsPrimary = true
            });
        }

        if (values.TryGetValue("Email", out var email))
        {
            client.Contact.Add(new CreateContactDto { ContactType = EContactType.Email, ContactValue = email, IsPrimary = true });
        }

        if (values.TryGetValue("Phone", out var phone))
        {
            client.Contact.Add(new CreateContactDto { ContactType = EContactType.Phone, ContactValue = phone, IsPrimary = !values.ContainsKey("Email") });
        }

        if (values.TryGetValue("AccountNumber", out var accountNumber))
        {
            client.BankAccount.Add(new CreateBankAccountDto
            {
                AccountNumber = accountNumber,
                IBAN = values.GetValueOrDefault("Iban")
            });
        }

        return client;
    }
}
