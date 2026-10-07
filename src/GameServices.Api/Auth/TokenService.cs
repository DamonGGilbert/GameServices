using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GameServices.Api.Data;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GameServices.Api.Auth;

public record AccessToken(string Value, DateTime ExpiresUtc);

/// <summary>
/// Makes the two kinds of token:
///   ACCESS token  - a signed JWT the game sends with every request ("I am Damon").
///                   Short-lived; the server can check it without touching the database.
///   REFRESH token - a random secret used only to get a new access token.
///                   Long-lived; stored (hashed) in the database so it can be revoked.
/// </summary>
public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions jwt = options.Value;

    public AccessToken CreateAccessToken(User user)
    {
        DateTime expires = DateTime.UtcNow.AddMinutes(jwt.AccessTokenMinutes);

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Expires = expires,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, user.Username)
            }),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                SecurityAlgorithms.HmacSha256)
        };

        return new AccessToken(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>
    /// Returns the raw token (sent to the player ONCE) and a ready-to-store
    /// database row that only contains its hash.
    /// </summary>
    public (string Raw, RefreshToken Row) CreateRefreshToken(User user)
    {
        // 32 bytes from a cryptographically secure generator: impossible to guess.
        string raw = Base64Url(RandomNumberGenerator.GetBytes(32));

        RefreshToken row = new()
        {
            UserId = user.Id,
            TokenHash = Hash(raw),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddDays(jwt.RefreshTokenDays)
        };

        return (raw, row);
    }

    /// <summary>SHA-256 is fine here (unlike passwords) because the input is already 256 random bits.</summary>
    public static string Hash(string raw)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
