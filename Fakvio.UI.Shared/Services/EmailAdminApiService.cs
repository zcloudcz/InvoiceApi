using Fakvio.Contracts.Dto.Email;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor client service for the SysAdmin email page.
/// Calls the EmailController endpoint to send custom emails.
/// </summary>
public class EmailAdminApiService : ApiClientBase
{
    public EmailAdminApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<EmailAdminApiService> logger,
        CustomAuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    /// <summary>
    /// Sends a custom email via the system SMTP settings.
    /// Returns a success message or throws ApiException on failure.
    /// </summary>
    public async Task<string> SendEmailAsync(SendEmailDto dto)
    {
        var result = await PostAsync<SendEmailDto, MessageResponse>("/api/email/send", dto);
        return result?.Message ?? "Email sent.";
    }

    /// <summary>Simple response wrapper for message-only API responses.</summary>
    private class MessageResponse
    {
        public string Message { get; set; } = string.Empty;
    }
}
