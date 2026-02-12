namespace InvoiceApi.BlazorUI;

/// <summary>
/// Marker class for shared localization resources.
/// IMPORTANT: This class MUST be in the root namespace (InvoiceApi.BlazorUI), NOT in
/// InvoiceApi.BlazorUI.Resources — otherwise the resource manager computes a "double Resources"
/// path (Resources/Resources/SharedResource.resx) and fails to find the .resx files.
/// Resource files: Resources/SharedResource.resx (Czech default), Resources/SharedResource.en.resx (English).
/// Used with IStringLocalizer&lt;SharedResource&gt;.
/// </summary>
public class SharedResource
{
}
