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

    /// <summary>Updated whenever the player's game talks to the social features. Used for the online dot.</summary>
    public DateTime? LastSeenUtc { get; set; }

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

public enum FriendshipStatus { Pending = 0, Accepted = 1 }

/// <summary>
/// One row per PAIR of players. UserAId/UserBId are the two ids in sorted order, so "A and B" and
/// "B and A" are the same pair and the database's unique index can forbid duplicates, even if both
/// players send each other a request at the same instant.
/// </summary>
public class Friendship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserAId { get; set; }
    public Guid UserBId { get; set; }

    /// <summary>Who sent the request (always A or B).</summary>
    public Guid RequesterId { get; set; }

    public FriendshipStatus Status { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedUtc { get; set; }

    public Guid OtherThan(Guid me) => UserAId == me ? UserBId : UserAId;
}

/// <summary>"Blocker does not want to hear from Blocked." Blocking is one-way, but works both ways in effect.</summary>
public class UserBlock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BlockerId { get; set; }
    public Guid BlockedId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One direct message between two friends.</summary>
public class ChatMessage
{
    /// <summary>Counts up (1, 2, 3...). Clients ask for "everything after id N", which is easy and cheap.</summary>
    public long Id { get; set; }
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ReadUtc { get; set; }
}

public enum GuildRole { Member = 0, Officer = 1, Leader = 2 }

/// <summary>A guild (clan): a group of players with a shared chat and shared boss battles.</summary>
public class Guild
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";

    /// <summary>The short 2-4 letter tag shown next to names, e.g. [DAMO].</summary>
    public string Tag { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Open guilds can be joined by anyone. Closed ones need an invite from an officer.</summary>
    public bool IsOpen { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Membership. A player can be in at most ONE guild (the unique index on UserId enforces it).</summary>
public class GuildMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GuildId { get; set; }
    public Guid UserId { get; set; }
    public GuildRole Role { get; set; }
    public DateTime JoinedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Place in the guild line-up: 0 = reserve (not in the line-up), 1-5 = main team, 6-8 = bench.</summary>
    public int Slot { get; set; }
}

public class GuildInvite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GuildId { get; set; }
    public Guid UserId { get; set; }
    public Guid InvitedById { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
}

public class GuildMessage
{
    public long Id { get; set; }
    public Guid GuildId { get; set; }
    public Guid SenderId { get; set; }
    public string Text { get; set; } = "";
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}

public enum BossStatus { Active = 0, Defeated = 1, Expired = 2 }

/// <summary>One boss fight. Everyone in the guild attacks the same health bar.</summary>
public class GuildBoss
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GuildId { get; set; }
    public string BossKey { get; set; } = "";
    public string Name { get; set; } = "";
    public int MaxHp { get; set; }
    public int CurrentHp { get; set; }
    public int RewardPool { get; set; }
    public BossStatus Status { get; set; }
    public Guid StartedById { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime EndsUtc { get; set; }

    /// <summary>Concurrency token: two members hitting the boss at the same instant can't both "win" the same HP.</summary>
    public int Version { get; set; }
}

/// <summary>How much damage one player has dealt to one boss (the leaderboard).</summary>
public class BossContribution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BossId { get; set; }
    public Guid UserId { get; set; }
    public int TotalDamage { get; set; }
    public int Attacks { get; set; }
    public int AttackDay { get; set; }
    public int AttacksToday { get; set; }
}

/// <summary>
/// A "forgot password" code that was emailed to a player. Only a hash is stored. It expires, works
/// once, and is cancelled after too many wrong guesses.
/// </summary>
public class PasswordResetCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public int Attempts { get; set; }
    public DateTime? UsedUtc { get; set; }
}
