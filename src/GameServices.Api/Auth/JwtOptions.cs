namespace GameServices.Api.Auth;

/// <summary>
/// Settings for the access tokens, read from the "Jwt" section of configuration.
/// The signing Key is a SECRET: in development it lives in appsettings.Development.json;
/// in production it must come from an environment variable (Jwt__Key) or a secret store,
/// never from a file in source control.
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "GameServices";
    public string Audience { get; set; } = "GameServicesClients";

    /// <summary>At least 32 characters. Anyone who knows this can forge logins.</summary>
    public string Key { get; set; } = "";

    /// <summary>Access tokens are short-lived so a stolen one is only useful briefly.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Refresh tokens last longer so players stay signed in for weeks.</summary>
    public int RefreshTokenDays { get; set; } = 30;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Key) || Key.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:Key must be set to a random secret of at least 32 characters. " +
                "In development it is in appsettings.Development.json; in production set the Jwt__Key environment variable.");
        }
    }
}
