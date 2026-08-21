using System.ComponentModel.DataAnnotations;
using Fakvio.Contracts.Dto.CompanySettings;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Contract tests for the DataAnnotations on UpdateCompanySystemSettingsDto — the payload
/// PUT /api/company/{id}/settings takes.
///
/// CompanyController is an [ApiController], so these attributes decide whether a save from
/// /my-company reaches the endpoint at all or is answered with a 400 the UI can only show
/// as a generic error. The EPO section on that page (issue #158) shapes its payload around
/// exactly these rules — a blank contact e-mail goes as null, and the numeric inputs are
/// capped at the [Range] bounds — so the rules are pinned here rather than left implicit.
///
/// The e-mail tests are also the tripwire for issue #186: they describe today's contract,
/// where an empty string cannot be stored. When #186 makes the e-mail fields clearable,
/// they go red, and the UI workaround in EpoSettingsSection.SaveAsync must go with them.
/// </summary>
public class UpdateCompanySystemSettingsDtoValidationTests
{
    // Bounds copied from the attributes under test — a test that recomputed them from the
    // DTO could not detect the value silently changing.
    private const int TaxOfficeCodeMax = 999;
    private const int TaxOfficeBranchCodeMax = 99999;
    private const int ContactPhoneMaxLength = 50;

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankContactEmail_IsRejected_WhichIsWhyTheEpoSectionSendsNull(string blank)
    {
        var dto = new UpdateCompanySystemSettingsDto { EpoContactEmail = blank };

        Validate(dto).ShouldContain(r => r.MemberNames.Contains(
            nameof(UpdateCompanySystemSettingsDto.EpoContactEmail)));
    }

    [Fact]
    public void BlankSmtpSenderEmail_IsRejectedTheSameWay_ButMyCompanyStillSendsItRaw()
    {
        var dto = new UpdateCompanySystemSettingsDto { SmtpSenderEmail = string.Empty };

        // Same defect, second field: SaveSmtpSettings in MyCompany.razor assigns
        // _smtpSenderEmail unconditionally, so clearing the SMTP sender still ends in a 400.
        // Only the EPO section works around it today. Issue #186 covers both fields, and its
        // acceptance criteria require the workaround to be removed once it lands.
        Validate(dto).ShouldContain(r => r.MemberNames.Contains(
            nameof(UpdateCompanySystemSettingsDto.SmtpSenderEmail)));
    }

    [Fact]
    public void NullEmails_AreAccepted_MeaningKeepTheStoredValue()
    {
        var dto = new UpdateCompanySystemSettingsDto
        {
            EpoContactEmail = null,
            SmtpSenderEmail = null
        };

        Validate(dto).ShouldBeEmpty();
    }

    [Fact]
    public void FilledContactEmail_IsAccepted()
    {
        var dto = new UpdateCompanySystemSettingsDto { EpoContactEmail = "ucetni@example.cz" };

        Validate(dto).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(TaxOfficeCodeMax)]
    public void TaxOfficeCode_InsideRange_IsAccepted(int code)
    {
        var dto = new UpdateCompanySystemSettingsDto { EpoTaxOfficeCode = code };

        Validate(dto).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TaxOfficeCodeMax + 1)]
    public void TaxOfficeCode_OutsideRange_IsRejected(int code)
    {
        var dto = new UpdateCompanySystemSettingsDto { EpoTaxOfficeCode = code };

        // This is what the Min/Max on the numeric input protects against: MudBlazor clamps
        // the typed value, so an out-of-range code never becomes a silent 400.
        Validate(dto).ShouldContain(r => r.MemberNames.Contains(
            nameof(UpdateCompanySystemSettingsDto.EpoTaxOfficeCode)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(TaxOfficeBranchCodeMax)]
    public void TaxOfficeBranchCode_InsideRange_IsAccepted(int code)
    {
        var dto = new UpdateCompanySystemSettingsDto { EpoTaxOfficeBranchCode = code };

        Validate(dto).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TaxOfficeBranchCodeMax + 1)]
    public void TaxOfficeBranchCode_OutsideRange_IsRejected(int code)
    {
        var dto = new UpdateCompanySystemSettingsDto { EpoTaxOfficeBranchCode = code };

        Validate(dto).ShouldContain(r => r.MemberNames.Contains(
            nameof(UpdateCompanySystemSettingsDto.EpoTaxOfficeBranchCode)));
    }

    [Fact]
    public void OverlongContactPhone_IsRejected()
    {
        var dto = new UpdateCompanySystemSettingsDto
        {
            EpoContactPhone = new string('1', ContactPhoneMaxLength + 1)
        };

        // Unlike [Range], this bound is not mirrored by the input (no MaxLength), so an
        // over-long value still reaches the API and comes back as a generic error.
        Validate(dto).ShouldContain(r => r.MemberNames.Contains(
            nameof(UpdateCompanySystemSettingsDto.EpoContactPhone)));
    }

    [Fact]
    public void EmptyOptionalTextFields_AreAccepted_SoTheyCanBeCleared()
    {
        var dto = new UpdateCompanySystemSettingsDto
        {
            EpoContactPhone = string.Empty,
            EpoAuthorizedPersonName = string.Empty
        };

        // Only the e-mail fields carry [EmailAddress]; the rest can be cleared with "".
        Validate(dto).ShouldBeEmpty();
    }

    /// <summary>
    /// Fails the calling test unless the DTO passes the DataAnnotations validation that
    /// [ApiController] runs on an action parameter before the endpoint body executes.
    /// Model binding does more than this; DataAnnotations is the part that rejects the
    /// payloads this section can produce.
    /// </summary>
    public static void AssertPassesApiValidation(UpdateCompanySystemSettingsDto dto)
    {
        var results = Validate(dto);

        results.ShouldBeEmpty(
            "API would answer 400: " + string.Join("; ", results.Select(r => r.ErrorMessage)));
    }

    /// <summary>Runs DataAnnotations validation and returns everything it complained about.</summary>
    private static List<ValidationResult> Validate(UpdateCompanySystemSettingsDto dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(
            dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }
}
