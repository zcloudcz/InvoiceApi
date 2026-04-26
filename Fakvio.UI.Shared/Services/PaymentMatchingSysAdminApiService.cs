using Fakvio.Contracts.Dto.PaymentMatching;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Blazor client for SysAdmin's payment matching settings page.
/// </summary>
public class PaymentMatchingSysAdminApiService : ApiClientBase
{
    public PaymentMatchingSysAdminApiService(
        IHttpClientFactory httpClientFactory,
        ILogger<PaymentMatchingSysAdminApiService> logger,
        AuthenticationStateProvider authStateProvider)
        : base(httpClientFactory, logger, authStateProvider)
    {
    }

    public Task<PaymentMatchingSystemSettingsDto?> GetAsync() =>
        GetAsync<PaymentMatchingSystemSettingsDto>("api/sysadmin/payment-matching/settings");

    public Task<PaymentMatchingSystemSettingsDto?> UpdateAsync(PaymentMatchingSystemSettingsDto dto) =>
        PutAsync<PaymentMatchingSystemSettingsDto, PaymentMatchingSystemSettingsDto>(
            "api/sysadmin/payment-matching/settings", dto);

    public Task<TestImapConnectionResult?> TestConnectionAsync(PaymentMatchingSystemSettingsDto dto) =>
        PostAsync<PaymentMatchingSystemSettingsDto, TestImapConnectionResult>(
            "api/sysadmin/payment-matching/test-connection", dto);
}
