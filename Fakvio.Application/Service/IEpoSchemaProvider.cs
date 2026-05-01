using System.Xml.Schema;

namespace Fakvio.Application.Service;

/// <summary>
/// Specifies the type of Czech EPO (Electronic Tax Portal) form for VAT submissions.
/// Each form type corresponds to a separate XSD schema published by the Czech Financial Administration.
/// </summary>
public enum EEpoFormType
{
    /// <summary>
    /// VAT return form — "Přiznání k dani z přidané hodnoty" (DPHDP3).
    /// Filed monthly or quarterly to report total output and input VAT.
    /// </summary>
    VatReturn = 1,

    /// <summary>
    /// VAT control statement — "Kontrolní hlášení DPH" (DPHKH1).
    /// Filed monthly (for some quarterly payers also monthly) with
    /// transaction-level detail used to cross-check with trading partners.
    /// </summary>
    ControlStatement = 2
}

/// <summary>
/// Provides compiled <see cref="XmlSchemaSet"/> objects for EPO form validation.
/// Schemas are loaded from the file system (Resources/Epo/{year}/) and cached
/// per (formType, year) pair so each XSD is compiled only once per process.
///
/// Throw <see cref="NotSupportedException"/> when a requested (formType, year)
/// combination has no XSD file available — this signals that the schema assets
/// need to be updated for that tax year.
/// </summary>
public interface IEpoSchemaProvider
{
    /// <summary>
    /// Returns a compiled and cached <see cref="XmlSchemaSet"/> for the given
    /// EPO form type and tax year.
    /// </summary>
    /// <param name="formType">
    /// Which EPO form — <see cref="EEpoFormType.VatReturn"/> (DPHDP3)
    /// or <see cref="EEpoFormType.ControlStatement"/> (DPHKH1).
    /// </param>
    /// <param name="year">
    /// The tax year (e.g. 2026). Must have a matching XSD file under
    /// <c>Resources/Epo/{year}/</c> in the output directory.
    /// </param>
    /// <returns>
    /// A compiled <see cref="XmlSchemaSet"/> ready for XML validation via
    /// <c>XDocument.Validate()</c>.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when no XSD file exists for the requested (formType, year) pair.
    /// The message includes both the year and the form type so the developer
    /// knows exactly which asset is missing.
    /// </exception>
    XmlSchemaSet GetSchemaSet(EEpoFormType formType, int year);
}
