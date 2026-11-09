using System.Collections.Generic;

namespace DLSSGManager;

/// <summary>
/// The interface text, keyed by a stable identifier and split per language.
///
/// The two tables are kept as separate files so a missing translation is visible in review.
/// Keys must match exactly across languages.
/// </summary>
public static partial class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        [Languages.ChineseSimplified] = ChineseSimplified(),
        [Languages.English] = English(),
    };

    /// <summary>The table for a language; falls back to the default when the code is unknown.</summary>
    public static Dictionary<string, string> For(string language)
    {
        var normalized = Languages.Normalize(language);
        return Tables.TryGetValue(normalized, out var table)
            ? table
            : Tables[Languages.ChineseSimplified];
    }

}
