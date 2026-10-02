namespace Fakvio.Contracts.Dto.Webhook;

/// <summary>Read DTO for a webhook subscription. Never includes the secret (see <see cref="WebhookSubscriptionCreatedDto"/>).</summary>
public class WebhookSubscriptionDto
{
    public long Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Events { get; set; } = new();
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Returned once, right after Create or Rotate — the only time the plaintext secret is exposed.</summary>
public class WebhookSubscriptionCreatedDto
{
    public WebhookSubscriptionDto Subscription { get; set; } = null!;

    /// <summary>Plaintext HMAC secret. Show it to the user once; it cannot be retrieved again.</summary>
    public string Secret { get; set; } = string.Empty;
}

public class CreateWebhookSubscriptionDto
{
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Events { get; set; } = new();
}

public class UpdateWebhookSubscriptionDto
{
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<string> Events { get; set; } = new();
    public bool IsActive { get; set; }
}

/// <summary>Result of the synchronous "test" action — a ping event sent right away, not queued.</summary>
public class WebhookTestResultDto
{
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
}
