namespace Fakvio.Infrastructure.Data;

/// <summary>
/// Helpers for working with PostgreSQL tenant schema names.
/// </summary>
public static class SchemaNames
{
    /// <summary>
    /// Sanitizes a schema name to prevent SQL injection.
    /// Only allows lowercase alphanumeric characters and underscores.
    /// PostgreSQL schema names must start with a letter or underscore.
    /// </summary>
    public static string Sanitize(string name)
    {
        var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(sanitized))
            throw new InvalidOperationException($"Invalid schema name: '{name}' — must contain alphanumeric characters.");
        return sanitized;
    }
}
