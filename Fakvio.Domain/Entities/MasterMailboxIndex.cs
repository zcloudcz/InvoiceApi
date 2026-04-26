using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Global (master-schema) lookup table mapping an inbound alias to a tenant schema.
///
/// Why a master-schema table? The central IMAP mailbox is one, but aliases live
/// in per-tenant schemas. When an email arrives, the IMAP worker needs to know
/// which tenant schema to open BEFORE it can query BankAccountMailbox. Without
/// this index, we'd have to scan every tenant — O(N tenants) per email.
///
/// Lifecycle:
///   - Mailbox activation   → new row with IsAliasRetired=false
///   - Alias regeneration   → old row IsAliasRetired=true; new row added
///   - Mailbox deactivation → no change (index still maps to same tenant)
///   - Tenant deletion      → rows stay but TenantSchema will point to a dead schema;
///                            worker treats ResolveFailed and moves email to "unrouted"
/// </summary>
public class MasterMailboxIndex : BaseEntity
{
    /// <summary>The alias local-part (e.g., "pay-7f3k9p2aqr"). Globally unique among non-retired rows.</summary>
    public string InboundAlias { get; set; } = string.Empty;

    /// <summary>PostgreSQL schema for the tenant (e.g., "tenant_42").</summary>
    public string TenantSchema { get; set; } = string.Empty;

    /// <summary>Id of the BankAccountMailbox row inside the tenant schema (for direct lookup).</summary>
    public long TenantBankAccountMailboxId { get; set; }

    /// <summary>
    /// True once the alias has been regenerated and the new alias supersedes it.
    /// Retired aliases are never routed; worker moves emails into the IMAP "retired" folder.
    /// </summary>
    public bool IsAliasRetired { get; set; }
}
