namespace GameServices.Api.Auth;

// DTOs ("data transfer objects") are the exact shapes that go over the wire.
// We never send the database entities directly, so we can't accidentally leak
// something like a password hash.

public record RegisterRequest(string Username, string Password, string? Email);

public record LoginRequest(string Username, string Password);

public record RefreshRequest(string RefreshToken);

public record UserDto(Guid Id, string Username, DateTime CreatedUtc);

public record AuthResponse(
    UserDto User,
    string AccessToken,
    DateTime AccessTokenExpiresUtc,
    string RefreshToken);
