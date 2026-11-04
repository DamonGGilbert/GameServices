using GameServices.Api.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GameServices.Api.Moderation;

/// <summary>
/// THE WORD FILTER: keeps swearing, slurs and hate out of names and chat, using the list in the database (BlockedWords).
///
/// The game has its own copy of this code and a built-in list (Assets/_Game/Scripts/Core/ContentFilter.cs), so the phone
/// can say "try another name" straight away - but only the server's word counts.
///
/// How it sees through tricks:
///   * look-alike symbols:  $h1t, @ss, f*ck -> shit, ass (the * is left alone: self-censored is fine)
///   * stretched letters:   fuuuuck, shiiit
///   * spaced out:          f u c k, f.u.c.k, f_u_c_k  (runs of single letters are read as one word)
///   * accents:             fück
///   * names run together:  BigAssDog, xXshitXx, Fu_Ck (names are also split at capitals and underscores)
///
/// Chat is CENSORED (the bad word becomes ****); names are REFUSED.
/// </summary>
public static class ContentFilter
{
    /// <summary>The filter in use: the built-in list until the database's has loaded.</summary>
    public static WordFilter Current { get; private set; } = new WordFilter(
        BlockedWordSeed.Of(BlockedWordKind.Anywhere), BlockedWordSeed.Of(BlockedWordKind.Whole),
        BlockedWordSeed.Of(BlockedWordKind.Allowed), BlockedWordSeed.Of(BlockedWordKind.Combination));

    public static void Use(WordFilter filter) => Current = filter;

    /// <summary>True when the text has nothing the filter would hide.</summary>
    public static bool IsClean(string? text) => Current.IsClean(text);

    /// <summary>Chat: every bad word becomes asterisks (same length, so the message still reads naturally).</summary>
    public static string Censor(string? text) => Current.Censor(text);

    /// <summary>Names (players, monsters, guilds): refused if anything in them is bad - even run together.</summary>
    public static bool IsCleanName(string? name) => Current.IsCleanName(name);
}

/// <summary>A word filter built from one list of words (see BlockedWords for the kinds).</summary>
public sealed class WordFilter
{
    // Endings a WHOLE word may have and still count: ass -> asses, retard -> retarded.
    private const string Endings = "(s|es|ed|er|ers|ing)?";

    // A combination run together in one word: long ones anywhere ("killyourself"), short ones only exactly ("godie").
    private const int LongEnoughForAnywhere = 8;

    // The words of a combination may have one other word between them and still count: "go and die".
    private const int WordsAllowedBetween = 1;

    private readonly Regex anywhereRule;
    private readonly Regex wholeRule;
    private readonly HashSet<string> allowedSet;
    private readonly List<Regex[]> combinations = new List<Regex[]>();

    public WordFilter(IEnumerable<string> anywhere, IEnumerable<string> whole, IEnumerable<string> allowed, IEnumerable<string> combinationPhrases)
    {
        var anywhereWords = new List<string>(anywhere);
        var wholeWords = new List<string>(whole);

        foreach (string phrase in combinationPhrases)
        {
            string[] words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) continue;
            combinations.Add(Array.ConvertAll(words, w => new Regex("^" + Stretchy(w) + "$", RegexOptions.Compiled)));

            string joined = string.Concat(words);
            if (joined.Length >= LongEnoughForAnywhere) anywhereWords.Add(joined);
            else wholeWords.Add(joined);
        }

        // (A regex with nothing in it would match everything: "(?!)" matches nothing.)
        anywhereRule = new Regex(anywhereWords.Count > 0 ? string.Join("|", anywhereWords.ConvertAll(Stretchy)) : "(?!)", RegexOptions.Compiled);
        wholeRule = new Regex(wholeWords.Count > 0 ? "^(" + string.Join("|", wholeWords.ConvertAll(Stretchy)) + ")" + Endings + "$" : "(?!)", RegexOptions.Compiled);
        allowedSet = new HashSet<string>(allowed);
    }

    /// <summary>What goes in the list: plain lower-case letters, with single spaces between the words of a combination.</summary>
    public static string TidyEntry(string text, bool keepSpaces)
    {
        var words = new List<string>();
        foreach (string part in (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = Normalise(part);
            if (word.Length > 0) words.Add(word);
        }
        return string.Join(keepSpaces ? " " : "", words);
    }

    // ------------------------------------------------------------------ the three questions

    /// <summary>True when the text has nothing the filter would hide.</summary>
    public bool IsClean(string? text) => Censor(text) == (text ?? "");

    /// <summary>Chat: every bad word becomes asterisks (same length, so the message still reads naturally).</summary>
    public string Censor(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        char[] output = text.ToCharArray();
        List<(int Start, int End, string Word)> words = Words(text);

        foreach ((int start, int end, string word) in words)
        {
            if (IsBad(word)) Hide(output, start, end);
        }

        // Words that are only a problem together: everything from the first to the last of them goes.
        foreach (Regex[] combination in combinations)
        {
            for (int first = 0; first < words.Count; first++)
            {
                int last = MatchFrom(words, first, combination);
                if (last >= 0) Hide(output, words[first].Start, words[last].End);
            }
        }

        return new string(output);
    }

    private static void Hide(char[] output, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(output[i])) output[i] = '*';
        }
    }

    // Does this combination start at this word? Returns where it ends, or -1.
    private static int MatchFrom(List<(int Start, int End, string Word)> words, int first, Regex[] combination)
    {
        if (!combination[0].IsMatch(words[first].Word)) return -1;
        int at = first;
        for (int next = 1; next < combination.Length; next++)
        {
            int found = -1;
            for (int look = at + 1; look < words.Count && look <= at + 1 + WordsAllowedBetween; look++)
            {
                if (combination[next].IsMatch(words[look].Word))
                {
                    found = look;
                    break;
                }
            }
            if (found < 0) return -1;
            at = found;
        }
        return at;
    }

    /// <summary>Names (players, monsters, guilds): refused if anything in them is bad - even run together.</summary>
    public bool IsCleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (!IsClean(name)) return false;

        // Split at capitals, spaces and underscores: BigAssDog -> big, ass, dog.
        List<string> parts = new List<string>();
        foreach (string part in Regex.Split(name, "(?<=[a-z])(?=[A-Z])|[^A-Za-z0-9@$!|+]+"))
        {
            string word = Normalise(part);
            if (word.Length == 0) continue;
            if (IsBad(word)) return false;
            parts.Add(word);
        }

        // Short pieces next to each other are read together too: Fu_Ck, SH1T_ head. (Only short ones, so
        // "Grass Hit Squad" is fine.)
        for (int i = 0; i + 1 < parts.Count; i++)
        {
            if (parts[i].Length <= 3 && parts[i + 1].Length <= 3 && IsBad(parts[i] + parts[i + 1])) return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ inside

    private bool IsBad(string word) => !allowedSet.Contains(word) && (anywhereRule.IsMatch(word) || wholeRule.IsMatch(word));

    // "ass" -> "a+s+s+": the letters may be stretched (asssss) but not shortened (as).
    private static string Stretchy(string word)
    {
        var sb = new StringBuilder();
        foreach (char c in word) sb.Append(c).Append('+');
        return sb.ToString();
    }

    // The words of a message with where they are, ready to check. Runs of 3+ single letters (f u c k) count as one.
    private static List<(int Start, int End, string Word)> Words(string text)
    {
        var raw = new List<(int Start, int End)>();
        int i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i]))
            {
                i++;
                continue;
            }
            int start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;

            // On the end of a word these are just punctuation: "kill yourself!!" is "yourself", not "yourselfii". (A "$" or
            // an "@" stays: "a$$" really does end in one.)
            int end = i;
            while (end > start + 1 && "!|+'\u2019".IndexOf(text[end - 1]) >= 0) end--;
            raw.Add((start, end));
        }

        var words = new List<(int, int, string)>();
        for (int w = 0; w < raw.Count; w++)
        {
            int run = w;
            while (run < raw.Count && Normalise(text.Substring(raw[run].Start, raw[run].End - raw[run].Start)).Length == 1) run++;
            if (run - w >= 3)
            {
                var joined = new StringBuilder();
                for (int k = w; k < run; k++) joined.Append(Normalise(text.Substring(raw[k].Start, raw[k].End - raw[k].Start)));
                words.Add((raw[w].Start, raw[run - 1].End, joined.ToString()));
                w = run - 1;
                continue;
            }
            words.Add((raw[w].Start, raw[w].End, Normalise(text.Substring(raw[w].Start, raw[w].End - raw[w].Start))));
        }
        return words;
    }

    // Letters, digits, the symbols people swap in for letters, and apostrophes (so "don't" reads as "dont").
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || "@$!|+'\u2019".IndexOf(c) >= 0;

    // Lower case, accents off, look-alikes turned into letters, everything else dropped.
    private static string Normalise(string text)
    {
        string plain = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(plain.Length);
        foreach (char ch in plain)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            char c = char.ToLowerInvariant(ch);
            switch (c)
            {
                case '0': c = 'o'; break;
                case '1': case '!': case '|': c = 'i'; break;
                case '3': c = 'e'; break;
                case '4': case '@': c = 'a'; break;
                case '5': case '$': c = 's'; break;
                case '7': case '+': c = 't'; break;
                case '8': c = 'b'; break;
                case '9': c = 'g'; break;
            }
            if (c >= 'a' && c <= 'z') sb.Append(c);
        }
        return sb.ToString();
    }
}
