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

/// <summary>
/// One player's card-game wallet and bookkeeping. The SERVER owns these numbers:
/// the game can ask to spend or earn, but it can never just say "I now have 99999".
/// </summary>
public class CardGameProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public int DuelPoints { get; set; }

    /// <summary>Goes up on every change. Two requests racing for the same wallet: only one wins.</summary>
    public int Version { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // Duel rewards are limited (see EconomyEndpoints) so a hacked client can't print money.
    public DateTime? LastRewardUtc { get; set; }
    public int RewardDay { get; set; }
    public int RewardsToday { get; set; }
}

/// <summary>"You own 3 copies of card 246."</summary>
public class OwnedCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string CardId { get; set; } = "";
    public int Quantity { get; set; }
}

/// <summary>
/// A LEDGER: every change to a wallet is written down and never edited, like a bank statement.
/// If a balance ever looks wrong, the ledger shows exactly how it got there.
/// </summary>
public class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>starter, pack, duel-win, duel-loss ...</summary>
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";

    /// <summary>Negative for spending, positive for earning.</summary>
    public int Amount { get; set; }
    public int BalanceAfter { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
