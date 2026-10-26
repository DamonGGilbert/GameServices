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

    // The player's "showcase": what friends and guildmates see next to their name.
    // The game reports these (they are cosmetic), and the server checks they are in a sane range.
    public int ShowLevel { get; set; } = 1;
    public int ShowPrestige { get; set; }
    public int ShowEmblem { get; set; }
    public int ShowFrame { get; set; }
    public string ShowTitle { get; set; } = "";

    /// <summary>
    /// How the player dresses their side of the table (zone colours, playmat, card sleeve): a small JSON document
    /// the game writes and reads. The server only keeps it tidy (size, valid JSON) so opponents can show it.
    /// </summary>
    public string CosmeticsJson { get; set; } = "";

    /// <summary>The player's PROFILE CARD choices (greeting, favourite card and deck, stats), as JSON. See ProfileCardEndpoints.</summary>
    public string ProfileJson { get; set; } = "";

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

    /// <summary>Lucky Draw tickets. Earned (never bought): see Tickets.</summary>
    public int SpinTickets { get; set; }
}

/// <summary>
/// When this player last got Duel Points for beating a single-player opponent, and at which Prestige rank. An opponent
/// pays once per Prestige rank; from the top ranks on it pays once every 24 hours instead (see RewardRules).
/// </summary>
public class OpponentReward
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string OpponentId { get; set; } = "";
    public int Prestige { get; set; }
    public DateTime LastClaimUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One Lucky Draw ticket that was handed out, and why. (UserId, Reason) is unique: every reason pays once.</summary>
public class TicketGrant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>"final-2" (beat the final opponent at Prestige 2), "prestige-3", "war-&lt;id&gt;" ...</summary>
    public string Reason { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
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

    /// <summary>
    /// Which game this guild belongs to ("yugioh" or "walking", see GameIds). Guilds never mix between games:
    /// a walking-game guild is invisible to the card game and the other way round. Older guilds are "yugioh".
    /// </summary>
    public string GameId { get; set; } = Infrastructure.GameIds.Yugioh;

    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";

    /// <summary>The short 2-4 letter tag shown next to names, e.g. [DAMO].</summary>
    public string Tag { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Open guilds can be joined by anyone. Closed ones need an invite from an officer.</summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>When the leader last changed the name or tag (null = never). Changes are limited to one every 14 days.</summary>
    public DateTime? NameChangedUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // How the guild does in Guild Matches (duels against another guild). The leaderboard ranks by Rating.
    public int Rating { get; set; } = 1000;
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int Draws { get; set; }

    // Guild Wars (the weekend event): wars won and lost. Wins also count towards the guild's tier.
    public int WarWins { get; set; }
    public int WarLosses { get; set; }

    /// <summary>The banner style the leader picked (0 = plain ... 4 = the shiniest). Limited by the guild's tier.</summary>
    public int BannerStyle { get; set; }
}

public enum WarStatus { Active = 0, Finished = 1 }

/// <summary>One weekend war between two guilds.</summary>
public class GuildWar
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GuildAId { get; set; }
    public Guid GuildBId { get; set; }
    public DateTime StartsUtc { get; set; }
    public DateTime EndsUtc { get; set; }
    public WarStatus Status { get; set; } = WarStatus.Active;
    public int ScoreA { get; set; }
    public int ScoreB { get; set; }

    /// <summary>The winning guild, or null for a draw / while the war is still on.</summary>
    public Guid? WinnerGuildId { get; set; }
}

/// <summary>A player's one war duel (their first Guild Match against the other guild during the war).</summary>
public class GuildWarDuel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WarId { get; set; }
    public Guid UserId { get; set; }
    public Guid GuildId { get; set; }
    public bool Won { get; set; }
    public DateTime PlayedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One finished online duel (history).</summary>
public class DuelMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RoomName { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; } = DateTime.UtcNow;

    public Guid Player0Id { get; set; }
    public Guid Player1Id { get; set; }
    public string Player0Name { get; set; } = "";
    public string Player1Name { get; set; } = "";

    /// <summary>0 or 1; -1 = draw / no result.</summary>
    public int WinnerSeat { get; set; } = -1;
    public string Reason { get; set; } = "";
    public int Turns { get; set; }

    public bool GuildMatch { get; set; }
    public Guid? Guild0Id { get; set; }
    public Guid? Guild1Id { get; set; }
    public int Guild0Delta { get; set; }
    public int Guild1Delta { get; set; }
}

/// <summary>
/// Membership. A player can be in at most ONE guild PER GAME (the unique index on UserId + GameId enforces it),
/// so the same person can lead a card-game guild and walk with a walking-game guild.
/// </summary>
public class GuildMember
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GuildId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>A copy of the guild's GameId, kept here so the database can enforce "one guild per player per game".</summary>
    public string GameId { get; set; } = Infrastructure.GameIds.Yugioh;
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

/// <summary>
/// A player's profile picture: a small image (the game sends a 128 x 128 JPEG) so friends, guildmates and
/// opponents can see it. One per player; kept apart from User so ordinary user queries stay light.
/// </summary>
public class UserAvatar
{
    public Guid UserId { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/jpeg";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A playmat or sleeve picture a player chose themselves (see CosmeticImageEndpoints for who can see it).</summary>
public class UserCosmeticImage
{
    public Guid UserId { get; set; }

    /// <summary>0 = playmat, 1 = sleeve.</summary>
    public int Kind { get; set; }

    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string ContentType { get; set; } = "image/jpeg";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

// ====================================================================================================
// THE WALKING GAME
// ====================================================================================================

/// <summary>
/// One player's walking-game record. The SERVER keeps these numbers: the game reports steps, the server decides how
/// many are believable (see Walking/StepRules) and adds only those.
/// </summary>
public class WalkingProfile
{
    public Guid UserId { get; set; }

    /// <summary>Every accepted step, ever. Step races measure progress from this.</summary>
    public long LifetimeSteps { get; set; }

    /// <summary>When the last report was accepted (used for the cadence check and the report rate limit).</summary>
    public DateTime? LastReportUtc { get; set; }

    /// <summary>Privacy: may friends see my daily and weekly steps? On by default (friends only, never strangers).</summary>
    public bool ShareSteps { get; set; } = true;

    public int RacesWon { get; set; }
    public int RacesLost { get; set; }

    /// <summary>How many reports were clipped hard enough to look suspicious.</summary>
    public int FlaggedReports { get; set; }

    /// <summary>Accepted steps carried towards the next monster-finding chance (one every 5,000 - see MonsterFinder).</summary>
    public int EncounterSteps { get; set; }

    // ---- The walking game's Glimmer (see Walking/Wallet) ----
    public int Glimmer { get; set; }

    /// <summary>Steps not yet turned into Glimmer (1 Glimmer per 100).</summary>
    public int GlimmerStepRemainder { get; set; }

    /// <summary>False until the first sync brought the phone's balance across.</summary>
    public bool WalletStarted { get; set; }

    /// <summary>Phone-earned Glimmer accepted today (UTC day), up to a daily allowance.</summary>
    public string PhoneGlimmerDay { get; set; } = "";
    public int PhoneGlimmerToday { get; set; }

    public string PacksDay { get; set; } = "";
    public int PacksToday { get; set; }

    /// <summary>The monster this walker walks with (it can't be offered for).</summary>
    public Guid? CompanionMonsterId { get; set; }

    // ---- Walk with friends (see Walking/WalkTogether) ----
    /// <summary>The ~100 m square of the last report that added steps - only kept while the walker has it switched on.</summary>
    public int? TogetherCellX { get; set; }
    public int? TogetherCellY { get; set; }
    /// <summary>When that was (null = not walking with the feature on right now).</summary>
    public DateTime? TogetherSeenUtc { get; set; }
    public int TogetherStepRemainder { get; set; }
    public string TogetherDay { get; set; } = "";
    public int TogetherBonusToday { get; set; }
    /// <summary>Days on which this walker walked with a friend.</summary>
    public int TogetherDaysTotal { get; set; }
}

/// <summary>Accepted steps for one player on one day (the player's own calendar day, "yyyy-MM-dd").</summary>
public class WalkingDay
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Day { get; set; } = "";
    public int Steps { get; set; }
    public int RejectedSteps { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A friendly "Keep going!" from one friend to another. Limited per friend per day.</summary>
public class Cheer
{
    public long Id { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }

    /// <summary>Which phrase (see WalkingEndpoints.CheerPhrases). A fixed list, so cheers can never be abusive.</summary>
    public int Kind { get; set; }
    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SeenUtc { get; set; }
}

public enum RaceStatus { Pending = 0, Active = 1, Finished = 2, Declined = 3, Cancelled = 4, Expired = 5 }

/// <summary>
/// A friendly step race between two friends. It starts when the opponent accepts; each player's progress is how many
/// accepted steps they have added since then (LifetimeSteps now minus LifetimeSteps at the start).
/// </summary>
public class StepRace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChallengerId { get; set; }
    public Guid OpponentId { get; set; }
    public RaceStatus Status { get; set; } = RaceStatus.Pending;

    /// <summary>How long the race lasts once accepted: 24 or 72 hours.</summary>
    public int Hours { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartsUtc { get; set; }
    public DateTime? EndsUtc { get; set; }

    // Lifetime steps at the start, and the progress when the race ended.
    public long ChallengerBaseline { get; set; }
    public long OpponentBaseline { get; set; }
    public int ChallengerSteps { get; set; }
    public int OpponentSteps { get; set; }

    /// <summary>The winner, or null for a draw / while it is still running.</summary>
    public Guid? WinnerId { get; set; }
}

/// <summary>
/// A player collected their reward for a finished weekly Guild Journey (walking game). One per player per week,
/// guaranteed by a unique index, so the reward can never be claimed twice.
/// </summary>
public class GuildJourneyClaim
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid GuildId { get; set; }

    /// <summary>The Monday the Journey week started, "yyyy-MM-dd" (UTC).</summary>
    public string WeekStart { get; set; } = "";

    /// <summary>How much Glimmer the server worked out the player earned.</summary>
    public int Glimmer { get; set; }

    public DateTime ClaimedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The walking game's weekend guild-vs-guild step race (Saturday 00:00 to Monday 00:00 UTC). Each guild's score is
/// the steps of its top walkers over the weekend. Kept apart from the card game's GuildWar, so neither game's job can
/// ever close the other's.
/// </summary>
public class GuildStepRace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GuildAId { get; set; }
    public Guid GuildBId { get; set; }
    public DateTime StartsUtc { get; set; }
    public DateTime EndsUtc { get; set; }
    public RaceStatus Status { get; set; } = RaceStatus.Active;

    // Filled in when the race ends (while it runs, the score is worked out live from the step reports).
    public int ScoreA { get; set; }
    public int ScoreB { get; set; }

    /// <summary>The winning guild, or null for a draw / while it is still running.</summary>
    public Guid? WinnerGuildId { get; set; }
}

/// <summary>
/// One of a walker's monsters, made by the server (see Walking/Monsters). The server keeps what it rolled; the game
/// builds every other detail (looks, personality, stats) from SpeciesId + Seed + RarityTier.
/// </summary>
public class WalkingMonster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string SpeciesId { get; set; } = "";
    public int RarityTier { get; set; }
    public long Seed { get; set; }

    /// <summary>Hidden potential 1-100 (from seed + rarity; stored so the server never has to work it out again).</summary>
    public int Potential { get; set; }

    /// <summary>Which version of the game's generation rules the seed is meant for.</summary>
    public int RulesVersion { get; set; } = 1;

    /// <summary>The Nth of its species ever found, by anyone ("#12 Emberling").</summary>
    public int Serial { get; set; }

    /// <summary>"starter" or "walk".</summary>
    public string Source { get; set; } = "";
    public string? BiomeId { get; set; }

    /// <summary>The walker's own calendar day it was found ("yyyy-MM-dd") - for the one-find-a-day rule.</summary>
    public string FoundDay { get; set; } = "";
    public DateTime FoundUtc { get; set; } = DateTime.UtcNow;

    /// <summary>False until the player has watched its reveal.</summary>
    public bool Seen { get; set; }

    // ---- What it's done with its walker (sent up by the game; travels with the monster to a new home) ----
    public int Level { get; set; } = 1;
    public int Experience { get; set; }
    public int TrainedPower { get; set; }
    public int TrainedGuard { get; set; }
    public int TrainedTricks { get; set; }
    public int TrainedWits { get; set; }
    public int TrainingSteps { get; set; }

    /// <summary>When it joined its current walker (offers need it to have settled in first).</summary>
    public DateTime AcquiredUtc { get; set; } = DateTime.UtcNow;
    public int TradeCount { get; set; }

    /// <summary>Who it came from, if it found a new home through an offer.</summary>
    public Guid? ArrivedFromUserId { get; set; }
}

/// <summary>How many of each species have ever been found (for serial numbers).</summary>
public class MonsterSpeciesCount
{
    public string SpeciesId { get; set; } = "";
    public int Found { get; set; }
}

/// <summary>One change to a walker's Glimmer, so every balance can be explained.</summary>
public class WalkingLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public int Amount { get; set; }
    public int BalanceAfter { get; set; }
    public string Kind { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum OfferStatus { Pending = 0, Accepted = 1, Declined = 2, Cancelled = 3, Expired = 4 }

/// <summary>An offer of Glimmer for a friend's monster (see Walking/Monsters/MonsterOffers). The Glimmer is held until it's answered.</summary>
public class MonsterOffer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MonsterId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public int Price { get; set; }
    public OfferStatus Status { get; set; } = OfferStatus.Pending;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? DecidedUtc { get; set; }
}

public enum RouteStatus { Active = 0, Completed = 1, Abandoned = 2, Expired = 3 }

/// <summary>A safe walking loop planned for one walker (see Walking/Routes).</summary>
public class WalkingRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>"short", "medium", "long" or "challenge".</summary>
    public string Kind { get; set; } = "";
    public int TargetMetres { get; set; }
    public int Metres { get; set; }

    /// <summary>Share of the route on footpaths and quiet streets (0-1), and how many stretches run beside busier roads.</summary>
    public double QuietShare { get; set; }
    public int BusyStretches { get; set; }

    /// <summary>The way to walk and the checkpoints, as JSON lists of [lat, lon].</summary>
    public string PathJson { get; set; } = "[]";
    public string CheckpointsJson { get; set; } = "[]";

    public int NextCheckpoint { get; set; }
    public RouteStatus Status { get; set; } = RouteStatus.Active;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastCheckpointUtc { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    /// <summary>The walker's lifetime steps when the route began (the finish checks the steps add up).</summary>
    public long StepsAtStart { get; set; }
    public string? Biome { get; set; }
    public int RewardGlimmer { get; set; }
    public bool RewardMonster { get; set; }
}
