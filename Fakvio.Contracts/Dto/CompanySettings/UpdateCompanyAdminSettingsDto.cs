using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.CompanySettings;

/// <summary>
/// DTO for updating the SysAdmin-only part of a CompanySystemSettings record — the tenant's
/// user limit and the internal admin notes, edited in the <c>/company-settings</c> dialog.
///
/// <para>
/// <b>Replace semantics, unlike <see cref="UpdateCompanySystemSettingsDto"/>.</b> Both fields
/// are written exactly as sent, nulls included. That is what keeps "no limit" reachable:
/// <see cref="MaxUsers"/> is an <c>int?</c> with no empty-string sentinel, so the patch rule
/// "null = keep the stored value" would make an existing limit impossible to lift.
/// Callers therefore always send the complete pair (the dialog pre-fills both fields).
/// </para>
///
/// <para>
/// Why a separate DTO (issue #184): these two fields used to sit on
/// <see cref="UpdateCompanySystemSettingsDto"/>, which the <c>/my-company</c> page sends for its
/// SMTP/AI/EPO sections. That page never edits them, so every save there wiped the limit and
/// the notes. Keeping the SysAdmin pair on its own contract means a tenant-side save cannot
/// reach these columns at all.
/// </para>
/// </summary>
public class UpdateCompanyAdminSettingsDto
{
    /// <summary>
    /// Maximum number of users allowed for this tenant. Null = unlimited.
    /// </summary>
    [Range(1, 10000)]
    public int? MaxUsers { get; set; }

    /// <summary>
    /// Internal admin notes about this tenant. Null or empty string = no note
    /// (the MudBlazor text field sends "" when the admin clears it).
    /// </summary>
    [StringLength(2000)]
    public string? AdminNotes { get; set; }
}
