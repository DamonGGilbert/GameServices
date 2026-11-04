using GameServices.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GameServices.Api.Moderation;

/// <summary>
/// THE LIST OF WORDS THE GAME BLOCKS lives in the database (BlockedWord), so it can change without a new version of the
/// server. Four kinds:
///     Anywhere      caught even inside other words: "shit" also catches "bullshit"
///     Whole         only on its own, maybe with an ending: "ass" and "asses", but not "class"
///     Combination   words that are only a problem together, in order: "kill yourself", "send nudes"
///     Allowed       innocent words that contain an Anywhere word: "Scunthorpe"
///
/// It's filled from BlockedWordSeed the first time the server starts. After that the list is yours, on the server machine:
///     dotnet GameServices.Api.dll words
///     dotnet GameServices.Api.dll block-word somethingrude         (Anywhere)
///     dotnet GameServices.Api.dll block-whole rude                 (Whole)
///     dotnet GameServices.Api.dll block-phrase go away forever     (Combination)
///     dotnet GameServices.Api.dll allow-word scunthorpe            (Allowed)
///     dotnet GameServices.Api.dll unblock somethingrude
///
/// A running server picks up changes within a minute (WalkingLifecycleService calls RefreshAsync).
/// </summary>
public static class BlockedWords
{
    private static string loaded = "";

    public static async Task SeedIfEmptyAsync(AppDbContext db)
    {
        if (await db.BlockedWords.AnyAsync()) return;
        db.BlockedWords.AddRange(BlockedWordSeed.All());
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();   // another copy of the server started at the same moment and filled it
        }
    }

    /// <summary>Builds a filter from what's in the database.</summary>
    public static async Task<WordFilter> LoadAsync(AppDbContext db)
    {
        List<BlockedWord> rows = await db.BlockedWords.AsNoTracking().ToListAsync();
        return FilterFrom(rows);
    }

    /// <summary>Starts using the database's list, if it has changed since last time.</summary>
    public static async Task RefreshAsync(AppDbContext db)
    {
        List<BlockedWord> rows = await db.BlockedWords.AsNoTracking().ToListAsync();
        string signature = string.Join("|", rows.OrderBy(r => r.Kind).ThenBy(r => r.Text).Select(r => (int)r.Kind + r.Text));
        if (signature == loaded) return;
        ContentFilter.Use(FilterFrom(rows));
        loaded = signature;
    }

    private static WordFilter FilterFrom(List<BlockedWord> rows)
    {
        IEnumerable<string> Of(BlockedWordKind kind) => rows.Where(r => r.Kind == kind).Select(r => r.Text);
        return new WordFilter(Of(BlockedWordKind.Anywhere), Of(BlockedWordKind.Whole), Of(BlockedWordKind.Allowed), Of(BlockedWordKind.Combination));
    }

    // ------------------------------------------------------------------ changing the list

    /// <summary>Adds a word or combination, tidied to plain lower-case letters. False if there's nothing left or it's already there.</summary>
    public static async Task<bool> AddAsync(AppDbContext db, string text, BlockedWordKind kind, string category = "added")
    {
        string tidy = WordFilter.TidyEntry(text, kind == BlockedWordKind.Combination);
        if (tidy.Length == 0) return false;
        if (kind == BlockedWordKind.Combination && !tidy.Contains(' ')) return false;   // one word isn't a combination
        if (await db.BlockedWords.AnyAsync(w => w.Text == tidy && w.Kind == kind)) return false;
        db.BlockedWords.Add(new BlockedWord { Text = tidy, Kind = kind, Category = category });
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Takes a word or combination off the list, whatever kind it is. Returns how many went.</summary>
    public static async Task<int> RemoveAsync(AppDbContext db, string text)
    {
        string tidy = WordFilter.TidyEntry(text, true);
        return await db.BlockedWords.Where(w => w.Text == tidy).ExecuteDeleteAsync();
    }

    public static async Task<bool> TryRunAdmin(IServiceProvider services, string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        BlockedWordKind? adds = command switch
        {
            "block-word" => BlockedWordKind.Anywhere,
            "block-whole" => BlockedWordKind.Whole,
            "block-phrase" => BlockedWordKind.Combination,
            "allow-word" => BlockedWordKind.Allowed,
            _ => null,
        };
        if (adds == null && command is not ("words" or "unblock")) return false;

        using IServiceScope scope = services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await SeedIfEmptyAsync(db);
        string rest = string.Join(' ', args.Skip(1));

        if (command == "words")
        {
            List<BlockedWord> rows = await db.BlockedWords.AsNoTracking().OrderBy(w => w.Kind).ThenBy(w => w.Text).ToListAsync();
            foreach (IGrouping<BlockedWordKind, BlockedWord> kind in rows.GroupBy(w => w.Kind))
            {
                Console.WriteLine(kind.Key + " (" + kind.Count() + "): " + string.Join(", ", kind.Select(w => w.Text)));
            }
        }
        else if (command == "unblock")
        {
            Console.WriteLine(await RemoveAsync(db, rest) > 0 ? "Removed. The server picks it up within a minute." : "That isn't on the list.");
        }
        else
        {
            Console.WriteLine(await AddAsync(db, rest, adds!.Value) ? "Added. The server picks it up within a minute." : "Not added: it's already there, or it needs " + (adds == BlockedWordKind.Combination ? "two or more words." : "some letters."));
        }

        return true;
    }
}

/// <summary>What the list starts with, the first time the server runs. This list is offensive on purpose: it's what we keep out.</summary>
public static class BlockedWordSeed
{
    private static readonly (string Category, BlockedWordKind Kind, string Words)[] Lists =
    {
        ("swearing", BlockedWordKind.Anywhere, "fuck shit cunt wank bitch bastard whore slut piss bollock twat asshole arsehole dickhead bellend motherfuck skank"),
        ("swearing", BlockedWordKind.Whole, "ass arse dick cock prick knob nob tosser bugger crap slag"),
        ("sexual", BlockedWordKind.Anywhere, "jizz dildo porn blowjob handjob rimjob hentai masturbat ejaculat paedophile pedophile molest bukkake sexting"),
        ("sexual", BlockedWordKind.Whole, "tits titty titties boob boobs boobies cum shag sex sexy horny nude nudes penis vagina anal anus semen sperm boner milf orgasm pussy clit erection incest fanny minge"),
        ("hate", BlockedWordKind.Anywhere, "nigger nigga faggot wetback hitler killyourself raghead towelhead"),
        ("hate", BlockedWordKind.Whole, "fag retard rape rapist pedo paedo nonce nazi heil kkk kike chink spic coon gook paki tranny dyke negro kys sieg beaner gypo gyppo pikey wog sambo darkie spaz spastic mong poof poofter homo lesbo shemale isis jihad"),
        ("drugs", BlockedWordKind.Whole, "cocaine heroin meth"),
        ("harm", BlockedWordKind.Combination, "kill yourself|kill urself|kill ur self|kill your self|go die|hang yourself|neck yourself|end yourself|cut yourself|drink bleach|hope you die|you should die"),
        ("sexual", BlockedWordKind.Combination, "send nudes|send nude|suck my|lick my|touch yourself|take off your|show your body|show me your body"),
        // Keeping players safe: nobody in a game needs to ask a stranger these.
        ("safety", BlockedWordKind.Combination, "where do you live|where u live|where you live|what school|which school|your address|my address|your phone number|my phone number|your number|my number|home alone|send a pic|send pics|send me a pic|send a photo|send me a photo|dont tell your|meet me|how old are you|how old r u|add me on|my snapchat|your snapchat|my insta|your insta|my discord|your discord|my whatsapp|your whatsapp"),
        ("innocent", BlockedWordKind.Allowed, "scunthorpe snigger sniggers sniggered sniggering shitake shiitake pissarro cockburn"),
    };

    public static List<BlockedWord> All()
    {
        var rows = new List<BlockedWord>();
        foreach ((string category, BlockedWordKind kind, string words) in Lists)
        {
            foreach (string word in words.Split(kind == BlockedWordKind.Combination ? '|' : ' ', StringSplitOptions.RemoveEmptyEntries))
            {
                rows.Add(new BlockedWord { Text = word, Kind = kind, Category = category });
            }
        }
        return rows;
    }

    public static IEnumerable<string> Of(BlockedWordKind kind) => All().Where(w => w.Kind == kind).Select(w => w.Text);
}
