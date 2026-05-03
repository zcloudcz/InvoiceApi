using Fakvio.Domain.Entities;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Outcome of a successful alias lookup by <see cref="InboundAliasRouter"/>.
///
/// Carries all the data downstream code needs to (a) open the correct tenant scope
/// and (b) write an audit record explaining how the message was routed.
/// </summary>
public sealed record AliasResolution(
    /// <summary>
    /// The alias local-part that was matched against <see cref="MasterMailboxIndex.InboundAlias"/>.
    /// Example: <c>"pay-7f3k9p2aqr"</c>.
    /// </summary>
    string MatchedAlias,

    /// <summary>
    /// The MIME header name (or synthetic name) from which the winning address was extracted.
    /// Examples: <c>"Delivered-To"</c>, <c>"X-Original-To"</c>, <c>"Received-for"</c>, <c>"To"</c>.
    ///
    /// Used for audit / logging so that SysAdmin can see which fallback leg resolved the message.
    /// </summary>
    string MatchedHeader,

    /// <summary>
    /// The matched row from the global master-schema alias index.
    /// Provides <see cref="MasterMailboxIndex.TenantSchema"/> and
    /// <see cref="MasterMailboxIndex.TenantBankAccountMailboxId"/> for tenant scope creation.
    /// </summary>
    MasterMailboxIndex MasterIndexEntry
);
