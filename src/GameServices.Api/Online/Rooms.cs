namespace GameServices.Api.Online;

public enum RoomStatus
{
    Waiting,    // one player in, or two players choosing decks
    Starting,   // both ready: the duel host is being started
    InMatch,    // the duel is being played
    Finished
}

/// <summary>A deck as the game sends it: one entry per copy.</summary>
public record DeckList(List<string> Main, List<string> Extra);

public class RoomSeat
{
    public Guid UserId { get; init; }
    public string Username { get; init; } = "";
    public Guid? GuildId { get; init; }
    public string GuildTag { get; init; } = "";
    public string GuildName { get; init; } = "";
    public GameServices.Api.Social.ShowcaseDto Showcase { get; init; } = new(1, 0, 0, 0, "");
    public string Cosmetics { get; init; } = "";
    public DeckList? Deck { get; set; }
    public bool Ready => Deck != null;
}

/// <summary>One room in the room list: a table where two players will duel.</summary>
public class Room
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    /// <summary>A Guild Match: the two players are in different guilds and the result changes the guilds' ratings.</summary>
    public bool GuildMatch { get; init; }

    public RoomStatus Status { get; set; } = RoomStatus.Waiting;
    public RoomSeat?[] Seats { get; } = new RoomSeat?[2];
    public string? MatchId { get; set; }

    /// <summary>Set when something stopped the match from starting (shown to the players).</summary>
    public string? Error { get; set; }

    public bool IsFull => Seats[0] != null && Seats[1] != null;
    public bool BothReady => IsFull && Seats[0]!.Ready && Seats[1]!.Ready;

    public int SeatOf(Guid userId) => Seats[0]?.UserId == userId ? 0 : Seats[1]?.UserId == userId ? 1 : -1;
}

public record RoomSeatDto(string Username, string GuildTag, bool Ready, GameServices.Api.Social.ShowcaseDto? Showcase = null, string? Cosmetics = null);

public record RoomDto(Guid Id, string Name, string Status, bool GuildMatch, List<RoomSeatDto> Seats, bool CanJoin, bool Mine, int MySeat, string MatchId, string Error, DateTime CreatedUtc);

/// <summary>
/// The list of rooms. Rooms live in memory only: they are short-lived tables, not records. (Finished duels are
/// written to the database as match history.) One player can be in one room at a time.
/// </summary>
public class RoomRegistry
{
    public const int MaxRooms = 100;

    private readonly object gate = new();
    private readonly Dictionary<Guid, Room> rooms = new();

    public IReadOnlyList<Room> Snapshot()
    {
        lock (gate)
        {
            return rooms.Values.Where(r => r.Status != RoomStatus.Finished).OrderByDescending(r => r.CreatedUtc).ToList();
        }
    }

    public Room? Get(Guid id)
    {
        lock (gate)
        {
            return rooms.GetValueOrDefault(id);
        }
    }

    public Room? ByUser(Guid userId)
    {
        lock (gate)
        {
            return rooms.Values.FirstOrDefault(r => r.Status != RoomStatus.Finished && r.SeatOf(userId) >= 0);
        }
    }

    public Room? ByMatch(string matchId)
    {
        lock (gate)
        {
            return rooms.Values.FirstOrDefault(r => r.MatchId == matchId);
        }
    }

    /// <summary>Makes a room with the player in seat 0. Returns null with a reason if they can't.</summary>
    public Room? Create(RoomSeat owner, string name, bool guildMatch, out string? problem)
    {
        lock (gate)
        {
            problem = null;

            if (rooms.Values.Any(r => r.Status != RoomStatus.Finished && r.SeatOf(owner.UserId) >= 0))
            {
                problem = "You are already in a room. Leave it first.";
                return null;
            }

            if (rooms.Values.Count(r => r.Status != RoomStatus.Finished) >= MaxRooms)
            {
                problem = "There are too many rooms right now. Try again soon.";
                return null;
            }

            Room room = new() { Name = name, GuildMatch = guildMatch };
            room.Seats[0] = owner;
            rooms[room.Id] = room;
            return room;
        }
    }

    public bool Join(Guid roomId, RoomSeat player, out string? problem, out Room? room)
    {
        lock (gate)
        {
            problem = null;
            room = rooms.GetValueOrDefault(roomId);

            if (room == null || room.Status == RoomStatus.Finished)
            {
                problem = "That room doesn't exist any more.";
                return false;
            }

            if (rooms.Values.Any(r => r.Status != RoomStatus.Finished && r.SeatOf(player.UserId) >= 0))
            {
                problem = "You are already in a room. Leave it first.";
                return false;
            }

            if (room.Status != RoomStatus.Waiting || room.IsFull)
            {
                problem = "That room is full.";
                return false;
            }

            if (room.GuildMatch)
            {
                RoomSeat owner = room.Seats[0]!;

                if (player.GuildId == null)
                {
                    problem = "Guild Matches are only for players in a guild.";
                    return false;
                }

                if (player.GuildId == owner.GuildId)
                {
                    problem = "A Guild Match is played against ANOTHER guild.";
                    return false;
                }
            }

            room.Seats[1] = player;
            room.LastActivityUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <summary>Removes the player from their room. Returns the room (or null) and whether a running match is affected.</summary>
    public Room? Leave(Guid userId)
    {
        lock (gate)
        {
            Room? room = rooms.Values.FirstOrDefault(r => r.Status != RoomStatus.Finished && r.SeatOf(userId) >= 0);

            if (room == null)
            {
                return null;
            }

            // Only a waiting room can be left like this; leaving a match = surrender (handled by the match).
            if (room.Status != RoomStatus.Waiting)
            {
                return room;
            }

            int seat = room.SeatOf(userId);
            room.Seats[seat] = null;

            // The other player (if any) moves into seat 0 and the room goes on, unready.
            if (room.Seats[1] != null)
            {
                room.Seats[0] = room.Seats[1];
                room.Seats[1] = null;
            }

            foreach (RoomSeat? other in room.Seats)
            {
                if (other != null)
                {
                    other.Deck = null;
                }
            }

            if (room.Seats[0] == null)
            {
                rooms.Remove(room.Id);
            }

            room.LastActivityUtc = DateTime.UtcNow;
            return room;
        }
    }

    public void Remove(Guid roomId)
    {
        lock (gate)
        {
            rooms.Remove(roomId);
        }
    }

    /// <summary>Rooms nobody is using any more are swept away.</summary>
    public int Sweep(TimeSpan waitingTimeout, TimeSpan finishedTimeout)
    {
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;

            List<Guid> dead = rooms.Values
                .Where(r => (r.Status == RoomStatus.Waiting && now - r.LastActivityUtc > waitingTimeout)
                         || (r.Status == RoomStatus.Finished && now - r.LastActivityUtc > finishedTimeout))
                .Select(r => r.Id)
                .ToList();

            foreach (Guid id in dead)
            {
                rooms.Remove(id);
            }

            return dead.Count;
        }
    }

    public static RoomDto ToDto(Room room, Guid? viewer)
    {
        int mySeat = viewer != null ? room.SeatOf(viewer.Value) : -1;

        List<RoomSeatDto> seats = room.Seats
            .Select(s => s == null ? new RoomSeatDto("", "", false) : new RoomSeatDto(s.Username, s.GuildTag, s.Ready, s.Showcase, s.Cosmetics))
            .ToList();

        bool canJoin = mySeat < 0 && room.Status == RoomStatus.Waiting && !room.IsFull;

        return new RoomDto(room.Id, room.Name, room.Status.ToString(), room.GuildMatch, seats, canJoin, mySeat >= 0, mySeat, room.MatchId ?? "", room.Error ?? "", room.CreatedUtc);
    }
}
