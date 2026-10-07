namespace GameServices.Api.Data;

/// <summary>
/// One account. An account belongs to a PERSON, not to a game, so the same
/// login can be used by the card game, the walking game and any future game.
/// (Per-game data -- cards, steps, guild membership -- hangs off this later.)
/// </summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>What other players see, e.g. "Damon_01".</summary>
    public string Username { get; set; } = "";

    /// <summary>Upper-cased copy used for lookups, so "Damon" and "damon" are the same account.</summary>
    public string NormalizedUsername { get; set; } = "";

    public string? Email { get; set; }
    public string? NormalizedEmail { get; set; }

    /// <summary>
    /// The password is NEVER stored -- only this salted, slow hash of it. If the
    /// database ever leaks, the attacker still can't read anyone's password.
    /// </summary>
    public string PasswordHash { get; set; } = "";

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }

    public List<RefreshToken> RefreshTokens { get; set; } = new();
}

/// <summary>
/// A long-lived "stay signed in" token. The game keeps it on the device; the
/// server keeps only a HASH of it (like a password), plus when it expires and
/// whether it has been used or revoked.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string TokenHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Set when the token is used (rotated) or the player signs out.</summary>
    public DateTime? RevokedUtc { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsActive => RevokedUtc == null && ExpiresUtc > DateTime.UtcNow;
}

/// <summary>
/// One player's cloud save for one game. The save itself is an opaque JSON document
/// (each game decides what is in it); the server only adds ownership and versioning.
/// </summary>
public class PlayerSave
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>Which game this save belongs to, e.g. "yugioh-card-game" or "walking-game".</summary>
    public string GameId { get; set; } = "";

    public string Json { get; set; } = "";

    /// <summary>Goes up by one on every save. Used to detect "someone else saved first".</summary>
    public int Version { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
