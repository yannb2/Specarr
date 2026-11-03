using System.Globalization;

namespace Specarr.Media;

/// <summary>English names for the ISO 639-2 codes stored in MKV and MP4 tracks.</summary>
public static class Languages
{
    // MKV uses the bibliographic codes (fre, ger, chi), .NET knows the terminology
    // ones (fra, deu, zho). These map one to the other.
    private static readonly Dictionary<string, string> BibliographicToTerminology = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho",
        ["cze"] = "ces", ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu",
        ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa",
        ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym"
    };

    private static readonly Dictionary<string, string> Special = new(StringComparer.OrdinalIgnoreCase)
    {
        ["und"] = "Undetermined",
        ["mul"] = "Multiple",
        ["zxx"] = "No language",
        ["mis"] = "Uncoded",
        ["qaa"] = "Original"
    };

    private static readonly Lazy<Dictionary<string, string>> Names = new(Build);

    public static string Name(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Special["und"];
        }

        if (Special.TryGetValue(code, out var special))
        {
            return special;
        }

        var key = BibliographicToTerminology.GetValueOrDefault(code, code);
        if (Names.Value.TryGetValue(key, out var name))
        {
            return name;
        }

        // IETF tags such as "en-US" or two-letter codes.
        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            if (!string.IsNullOrEmpty(culture.EnglishName) && !culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal))
            {
                return culture.EnglishName;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return code;
    }

    /// <summary>The code as the tables show it: "eng", "fre". Hover for the name.</summary>
    public static string Short(string? code)
    {
        return string.IsNullOrWhiteSpace(code) ? "und" : code.Trim().ToLowerInvariant();
    }

    private static Dictionary<string, string> Build()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var code = culture.ThreeLetterISOLanguageName;
            if (code.Length == 3 && !names.ContainsKey(code) && !string.IsNullOrEmpty(culture.EnglishName))
            {
                names[code] = culture.EnglishName;
            }
        }

        return names;
    }
}
