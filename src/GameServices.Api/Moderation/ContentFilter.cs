using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GameServices.Api.Moderation;

/// <summary>
/// THE WORD FILTER: keeps swearing, slurs and hate out of names and chat.
///
/// The SAME code lives in the game (Assets/_Game/Scripts/Core/ContentFilter.cs) so the phone can say "try another
/// name" straight away - but only the server's word counts. Change both together.
///
/// How it sees through tricks:
///   * look-alike symbols:  $h1t, @ss, f*ck -> shit, ass (the * is left alone: self-censored is fine)
///   * stretched letters:   fuuuuck, shiiit
///   * spaced out:          f u c k, f.u.c.k, f_u_c_k  (runs of single letters are read as one word)
///   * accents:             fück
///   * names run together:  BigAssDog, xXshitXx, Fu_Ck (names are also split at capitals and underscores)
///
/// Two kinds of word, so innocent words aren't caught (the "Scunthorpe problem"):
///   * ANYWHERE words are caught even inside other words (fuck, shit, slurs...) - with a short list of innocent
///     words that contain them (Scunthorpe, snigger...).
///   * WHOLE words only count on their own, maybe with an ending like -s or -ing (ass, but not class or
///     assassin; rape, but not grape; spic, but not spicy).
///
/// Chat is CENSORED (the bad word becomes ****); names are REFUSED.
///
/// NOTE: this list is offensive on purpose - it's what we keep out. Keep it lower case, letters only.
/// </summary>
public static class ContentFilter
{
    private static readonly string[] AnywhereWords =
    {
        "fuck", "shit", "cunt", "wank", "bitch", "bastard", "whore", "slut", "piss", "bollock", "twat",
        "asshole", "arsehole", "dickhead", "bellend", "motherfuck", "jizz", "dildo", "porn",
        "nigger", "nigga", "faggot", "wetback", "hitler", "killyourself",
    };

    private static readonly string[] WholeWords =
    {
        "ass", "arse", "dick", "cock", "prick", "knob", "tits", "cum", "shag", "fag", "retard", "rape", "rapist",
        "pedo", "paedo", "nonce", "nazi", "heil", "kkk", "kike", "chink", "spic", "coon", "gook", "paki", "tranny",
        "dyke", "negro", "kys", "sieg",
    };

    // Innocent words that happen to contain an ANYWHERE word.
    private static readonly string[] Allowed =
    {
        "scunthorpe", "snigger", "sniggers", "sniggered", "sniggering", "shitake", "shiitake", "pissarro", "cockburn",
    };

    // Endings a WHOLE word may have and still count: ass -> asses, retard -> retarded.
    private const string Endings = "(s|es|ed|er|ers|ing)?";

    private static readonly Regex AnywhereRule = new Regex(string.Join("|", Array.ConvertAll(AnywhereWords, Stretchy)), RegexOptions.Compiled);
    private static readonly Regex WholeRule = new Regex("^(" + string.Join("|", Array.ConvertAll(WholeWords, Stretchy)) + ")" + Endings + "$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedSet = new HashSet<string>(Allowed);

    // ------------------------------------------------------------------ the three questions

    /// <summary>True when the text has nothing the filter would hide.</summary>
    public static bool IsClean(string? text) => Censor(text) == (text ?? "");

    /// <summary>Chat: every bad word becomes asterisks (same length, so the message still reads naturally).</summary>
    public static string Censor(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        char[] output = text.ToCharArray();
        foreach ((int start, int end, string word) in Words(text))
        {
            if (!IsBad(word)) continue;
            for (int i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(output[i])) output[i] = '*';
            }
        }
        return new string(output);
    }

    /// <summary>Names (players, monsters, guilds): refused if anything in them is bad - even run together.</summary>
    public static bool IsCleanName(string? name)
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

    private static bool IsBad(string word) => !AllowedSet.Contains(word) && (InsideAnything(word) || WholeRule.IsMatch(word));

    private static bool InsideAnything(string word) => !AllowedSet.Contains(word) && AnywhereRule.IsMatch(word);

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
            raw.Add((start, i));
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

    // Letters, digits and the symbols people swap in for letters.
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || "@$!|+".IndexOf(c) >= 0;

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
