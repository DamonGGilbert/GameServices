namespace GameServices.Api.Auth;

/// <summary>
/// WHO CAN HAVE A STEPLINGS ACCOUNT. The game asks for a birth month and year before anything else, and sends them when
/// someone makes an account. The server checks them again and keeps only WHEN the check was passed - never the birthday.
///
/// The minimum age is the setting Accounts:MinimumAge (default 13). The game has the same number in its AgeRules: change
/// both together. Only the month is known, so someone counts as a year older only once their birth month has passed.
/// </summary>
public static class AgeRules
{
    public const int DefaultMinimumAge = 13;

    public static bool IsRealBirthDate(int? year, int? month, DateTime today) =>
        year is int y && month is int m && m is >= 1 and <= 12 && y >= today.Year - 120 && (y < today.Year || (y == today.Year && m <= today.Month));

    public static int AgeIn(int birthYear, int birthMonth, DateTime today) =>
        today.Year - birthYear - (today.Month <= birthMonth ? 1 : 0);
}
