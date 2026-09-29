namespace Accufex.Server.Options;

public class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>
    /// Explicitly allowed origins for CORS.
    /// In production, this must contain exact HTTPS frontend domains and MUST NEVER use wildcards.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}
