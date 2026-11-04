namespace GameServices.Api.Auth;

// DTOs ("data transfer objects") are the exact shapes that go over the wire.
// We never send the database entities directly, so we can't accidentally leak
// something like a password hash.

/// <param name="BirthYear">Steplings asks for these (the card game doesn't): checked, then forgotten. See AgeRules.</param>
/// <param name="Clan">Steplings only, and only if the game asks: the clan the starter should come in (blank = no preference).</param>
public record RegisterRequest(string Username, string Password, string? Email, int? BirthYear = null, int? BirthMonth = null, string? Clan = null);

public record LoginRequest(string Username, string Password);

public record RefreshRequest(string RefreshToken);

public record UserDto(Guid Id, string Username, DateTime CreatedUtc);

public record AuthResponse(
    UserDto User,
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken);
