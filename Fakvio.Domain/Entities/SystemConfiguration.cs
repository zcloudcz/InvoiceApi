using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Stores global system configuration: app settings, SMTP, and JWT.
/// This is a single-row table in the master database — there is always exactly one record.
///
/// SysAdmin can edit these settings via the /system-settings Blazor page.
/// EmailService reads SMTP settings from this table (with fallback to appsettings.json).
/// SmtpPassword is encrypted at rest via ICredentialProtector (Data Protection API).
/// </summary>
public class SystemConfiguration : BaseEntity
{
    // ─── Application Settings ─────────────────────────────────────────────────
    // General app-wide settings. Editable by SysAdmin, override appsettings.json values.

    /// <summary>
    /// Public display name of the application (used in emails, QR codes, page titles).
    /// Overrides appsettings.json "AppSettings:Name". Default: "Fakvio".
    /// </summary>
    public string AppName { get; set; } = "Fakvio";

    /// <summary>
    /// Base URL of the Blazor WASM frontend (e.g., "https://app.fakvio.cz").
    /// Used for building links in emails (invitation, password reset, etc.).
    /// Overrides appsettings.json "AppSettings:BlazorBaseUrl".
    /// Must NOT have a trailing slash.
    /// </summary>
    public string BlazorBaseUrl { get; set; } = "";

    // ─── SMTP Settings ───────────────────────────────────────────────────────
    // Used by EmailService (MailKit) for sending invoices, invitations, reminders.

    /// <summary>
    /// SMTP server hostname (e.g., "smtp.gmail.com", "smtp.office365.com").
    /// </summary>
    public string SmtpHost { get; set; } = "";

    /// <summary>
    /// SMTP server port. Common values: 587 (STARTTLS), 465 (SSL), 25 (unencrypted).
    /// Default: 587 — the most common secure port for outgoing email.
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// SMTP authentication username (often the same as the sender email).
    /// Null if SMTP server doesn't require authentication.
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP authentication password.
    /// Null if SMTP server doesn't require authentication.
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// Email address used as the "From" field in outgoing emails.
    /// </summary>
    public string SmtpSenderEmail { get; set; } = "";

    /// <summary>
    /// Display name used as the "From" field in outgoing emails (e.g., "Fakvio").
    /// </summary>
    public string SmtpSenderName { get; set; } = "Fakvio";

    /// <summary>
    /// Whether to use SSL/TLS when connecting to the SMTP server.
    /// Default: true — always use encrypted connection in production.
    /// </summary>
    public bool SmtpUseSsl { get; set; } = true;

    // ─── JWT Settings ────────────────────────────────────────────────────────
    // Controls how long JWT tokens remain valid after login.

    /// <summary>
    /// How many hours a JWT token stays valid after being issued.
    /// Default: 24 hours. Shorter = more secure, longer = less re-login friction.
    /// </summary>
    public int JwtExpirationHours { get; set; } = 24;
}
