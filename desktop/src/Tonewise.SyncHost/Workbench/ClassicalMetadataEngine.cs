using System.Text.Json;

namespace Tonewise.SyncHost.Workbench;

public sealed record ParsedWorkMovement(string Work, string Movement, int? MovementNumber);

public sealed record SeparatedPerformers(IReadOnlyList<string> Soloists, string? Ensemble);

public sealed record ClassicalNowPlayingLines(
    string Line1,
    string Line2,
    IReadOnlyList<string> Line3Tiers);

/// <summary>
/// Parity mirror of Rust <c>tonewise_metadata::classical</c> governed by
/// <c>protocol/dictionaries/composer_metadata.json</c> and <c>protocol/fixtures/classical_golden.json</c>.
/// </summary>
public static class ClassicalMetadataEngine
{
    private sealed record MetadataTables(
        IReadOnlyDictionary<string, string> ComposerEras,
        IReadOnlyDictionary<string, string> EnsembleAbbreviations
    );

    private static readonly Lazy<MetadataTables> LazyTables = new(() =>
    {
        var asm = typeof(ClassicalMetadataEngine).Assembly;
        const string resourceName = "Tonewise.SyncHost.protocol.composer_metadata.json";
        using Stream? stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded classical dictionary '{resourceName}' not found.");
        using var doc = JsonDocument.Parse(stream);

        var eras = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("composer_eras", out var eraObj) && eraObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in eraObj.EnumerateObject())
            {
                string key = prop.Name.Trim().ToLowerInvariant();
                string val = (prop.Value.GetString() ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(key) && val is "baroque" or "classical" or "romantic" or "modern")
                {
                    eras[key] = val;
                }
            }
        }

        var abbrs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("ensemble_abbreviations", out var abbrObj) && abbrObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in abbrObj.EnumerateObject())
            {
                string key = prop.Name.Trim().ToLowerInvariant();
                string val = (prop.Value.GetString() ?? "").Trim();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                {
                    abbrs[key] = val;
                }
            }
        }

        return new MetadataTables(eras, abbrs);
    });

    private static readonly string[] ClassicalGenreTokens =
    [
        "classical", "古典", "交响", "协奏", "奏鸣", "室内乐", "歌剧", "巴洛克",
        "orchestra", "symphon", "concerto", "sonata", "chamber", "opera", "baroque", "choral",
    ];

    private static readonly string[] NonClassicalGenreTokens =
    [
        "pop", "rock", "metal", "jazz", "hip-hop", "hip hop", "rap", "r&b", "electronic", "edm",
        "dance", "country", "folk", "blues", "reggae", "indie", "punk", "soul", "funk", "k-pop",
        "j-pop", "c-pop", "mandopop", "cantopop", "tai-pop", "new age", "ambient", "traditional",
        "latin", "ballad", "disco", "alternative", "world music", "acoustic", "fado", "reggaeton",
        "acg", "anime", "soundtrack", "ost", "game", "流行", "摇滚", "爵士", "电子", "民谣", "说唱", "嘻哈", "原声",
    ];

    private static readonly string[] ClassicalWorkSignalTokens =
    [
        "symphony", "symphonie", "concerto", "konzert", "sonata", "sonate", "quartet", "quintet",
        "trio", "variation", "prelude", "prélude", "nocturne", "etude", "étude", "fugue", "toccata",
        "suite", "partita", "requiem", "cantata", "oratorio", "overture", "ouvertüre", "rhapsody in ",
        "rhapsody on ", "serenade", "divertimento", "scherzo", "chaconne", "passacaglia", "mass in",
        "op.", "bwv", "kv", "k.", "hob.", "rv ", "woo", "交响", "协奏", "奏鸣", "四重奏", "变奏曲",
        "前奏曲", "夜曲", "练习曲", "组曲", "弥撒", "安魂曲", "歌剧",
    ];

    private static readonly string[] EnsembleTokens =
    [
        "orchestra", "philharmonic", "philharmoniker", "symphoniker", "sinfonia", "quartet",
        "quintet", "sextet", "octet", "trio", "ensemble", "choir", "chorus", "academy", "consort",
        "staatskapelle", "gewandhaus", "orchestre", "orchester", "camerata", "solisti", "virtuosi",
        "乐团", "爱乐", "交响", "合唱团", "四重奏", "五重奏", "三重奏", "室内乐团",
    ];

    private static readonly string[] CatalogMarkers =
    [
        "hob.", "hob", "op.", "op", "bwv", "k.", "kv", "rv", "woo",
    ];

    public static bool IsClassicalGenre(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return false;
        string lower = genre.Trim().ToLowerInvariant();
        return ClassicalGenreTokens.Any(lower.Contains);
    }

    public static bool IsExplicitNonClassicalGenre(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return false;
        string lower = genre.Trim().ToLowerInvariant();
        return NonClassicalGenreTokens.Any(lower.Contains);
    }

    public static bool HasClassicalWorkSignal(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string lower = text.Trim().ToLowerInvariant();
        return ClassicalWorkSignalTokens.Any(lower.Contains);
    }

    public static bool IsClassicalTrack(
        string? genre,
        string? work,
        string? composer,
        string? conductor,
        string? title)
    {
        if (IsClassicalGenre(genre)) return true;
        string workClean = (work ?? "").Trim();
        if (IsExplicitNonClassicalGenre(genre))
        {
            return HasClassicalWorkSignal(workClean) || HasClassicalWorkSignal(title);
        }
        if (!string.IsNullOrEmpty(workClean) || !string.IsNullOrWhiteSpace(conductor))
        {
            return true;
        }
        return !string.IsNullOrWhiteSpace(composer);
    }

    public static int? ParseRomanNumeral(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().ToUpperInvariant();
        if (s.Length == 0 || s.Any(c => c is not ('I' or 'V' or 'X' or 'L' or 'C'))) return null;

        int total = 0;
        int prev = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            int val = s[i] switch
            {
                'I' => 1,
                'V' => 5,
                'X' => 10,
                'L' => 50,
                'C' => 100,
                _ => 0,
            };
            if (val == 0) return null;
            if (val < prev) total -= val;
            else
            {
                total += val;
                prev = val;
            }
        }
        if (total is <= 0 or > 99) return null;
        return string.Equals(FormatRomanNumeral(total), s, StringComparison.Ordinal) ? total : null;
    }

    public static string FormatRomanNumeral(int n)
    {
        if (n is <= 0 or > 99) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ReadOnlySpan<string> tens = ["", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC"];
        ReadOnlySpan<string> ones = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
        return tens[n / 10] + ones[n % 10];
    }

    public static (int? Number, string RemainingTitle) ParseMovementPrefix(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) return (null, "");

        int firstNonRoman = 0;
        while (firstNonRoman < trimmed.Length && char.ToUpperInvariant(trimmed[firstNonRoman]) is 'I' or 'V' or 'X' or 'L' or 'C')
        {
            firstNonRoman++;
        }

        if (firstNonRoman > 0 && firstNonRoman < trimmed.Length)
        {
            string token = trimmed[..firstNonRoman];
            string rest = trimmed[firstNonRoman..];
            bool delimited = rest.StartsWith('.')
                || rest.StartsWith(':')
                || rest.StartsWith('：')
                || rest.StartsWith(" - ", StringComparison.Ordinal)
                || rest.StartsWith(" – ", StringComparison.Ordinal)
                || rest.StartsWith(" — ", StringComparison.Ordinal);
            if (delimited && ParseRomanNumeral(token) is int rnum)
            {
                string after = rest.TrimStart('.', ':', '：', '-', '–', '—', ' ').Trim();
                return (rnum, after);
            }
        }

        int firstNonDigit = 0;
        while (firstNonDigit < trimmed.Length && trimmed[firstNonDigit] is >= '0' and <= '9')
        {
            firstNonDigit++;
        }

        if (firstNonDigit > 0 && firstNonDigit < trimmed.Length)
        {
            string digits = trimmed[..firstNonDigit];
            string rest = trimmed[firstNonDigit..];
            if ((rest.StartsWith('.') || rest.StartsWith(" - ", StringComparison.Ordinal) || rest.StartsWith(" – ", StringComparison.Ordinal))
                && int.TryParse(digits, out int num)
                && num is > 0 and <= 99)
            {
                string after = rest.TrimStart('.', '-', '–', ' ').Trim();
                if (!string.IsNullOrEmpty(after))
                {
                    return (num, after);
                }
            }
        }

        return (null, trimmed);
    }

    private static bool IsProtectedCatalogColon(string s, int colonIdx)
    {
        if (colonIdx + 1 >= s.Length || !char.IsAsciiDigit(s[colonIdx + 1])) return false;
        string before = s[..colonIdx].TrimEnd().ToLowerInvariant();
        int windowStart = Math.Max(0, before.Length - 24);
        string tail = before[windowStart..];
        return CatalogMarkers.Any(tail.Contains);
    }

    private static (string Work, string Movement)? FindWorkMovementSplit(string title)
    {
        for (int i = 0; i < title.Length; i++)
        {
            char ch = title[i];
            if (ch == ':' && !IsProtectedCatalogColon(title, i))
            {
                string left = title[..i].Trim();
                string right = title[(i + 1)..].Trim();
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                {
                    return (left, right);
                }
            }
            else if (ch == '：')
            {
                string left = title[..i].Trim();
                string right = title[(i + 1)..].Trim();
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                {
                    return (left, right);
                }
            }
        }

        foreach (string sep in new[] { " — ", " – ", " - " })
        {
            int pos = title.IndexOf(sep, StringComparison.Ordinal);
            if (pos >= 0)
            {
                string left = title[..pos].Trim();
                string right = title[(pos + sep.Length)..].Trim();
                if (!string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right))
                {
                    return (left, right);
                }
            }
        }

        return null;
    }

    public static ParsedWorkMovement SplitWorkAndMovement(
        string? rawTitle,
        string? explicitWork = null,
        string? explicitMovement = null,
        int? explicitMovementNumber = null)
    {
        string title = (rawTitle ?? "").Trim();
        string? expWork = string.IsNullOrWhiteSpace(explicitWork) ? null : explicitWork.Trim();
        string? expMvt = string.IsNullOrWhiteSpace(explicitMovement) ? null : explicitMovement.Trim();

        if (expWork is not null)
        {
            string rawMvtSource;
            if (expMvt is not null)
            {
                rawMvtSource = expMvt;
            }
            else if (FindWorkMovementSplit(title) is var split
                     && split is not null
                     && string.Equals(split.Value.Work, expWork, StringComparison.OrdinalIgnoreCase))
            {
                rawMvtSource = split.Value.Movement;
            }
            else
            {
                rawMvtSource = title;
            }

            var (parsedNum, cleanMvt) = ParseMovementPrefix(rawMvtSource);
            string finalMvt = string.Equals(cleanMvt, expWork, StringComparison.OrdinalIgnoreCase) ? "" : cleanMvt;
            return new ParsedWorkMovement(expWork, finalMvt, explicitMovementNumber ?? parsedNum);
        }

        if (FindWorkMovementSplit(title) is { } found)
        {
            string mvtSource = expMvt ?? found.Movement;
            var (parsedNum, cleanMvt) = ParseMovementPrefix(mvtSource);
            return new ParsedWorkMovement(found.Work, cleanMvt, explicitMovementNumber ?? parsedNum);
        }

        var (titleNum, cleanTitle) = ParseMovementPrefix(title);
        if (titleNum is not null)
        {
            return new ParsedWorkMovement(cleanTitle, expMvt ?? cleanTitle, explicitMovementNumber ?? titleNum);
        }

        return new ParsedWorkMovement(title, expMvt ?? "", explicitMovementNumber);
    }

    public static string? ClassifyComposerEra(string? composer, string? rawGenre = null)
    {
        var tables = LazyTables.Value;
        string trimmed = (composer ?? "").Trim();
        if (trimmed.Length > 0)
        {
            string norm = ArtistEntitySplitter.NormalizeComposerName(trimmed).ToLowerInvariant();
            if (!string.IsNullOrEmpty(norm) && tables.ComposerEras.TryGetValue(norm, out string? era))
            {
                return era;
            }
            string rawLower = trimmed.ToLowerInvariant();
            if (tables.ComposerEras.TryGetValue(rawLower, out string? rawEra))
            {
                return rawEra;
            }
        }

        string g = (rawGenre ?? "").Trim().ToLowerInvariant();
        if (g.Contains("baroque", StringComparison.Ordinal) || g.Contains("巴洛克", StringComparison.Ordinal))
            return "baroque";
        if (g.Contains("romantic", StringComparison.Ordinal) || g.Contains("浪漫", StringComparison.Ordinal))
            return "romantic";
        if (g.Contains("modern", StringComparison.Ordinal) || g.Contains("contemporary", StringComparison.Ordinal)
            || g.Contains("20th", StringComparison.Ordinal) || g.Contains("现代", StringComparison.Ordinal))
            return "modern";
        if (g.Contains("classical era", StringComparison.Ordinal) || g.Contains("古典主义", StringComparison.Ordinal))
            return "classical";
        return null;
    }

    public static string ClassifyWorkForm(string? workTitle, string? rawGenre = null)
    {
        string combined = $"{(workTitle ?? "").Trim()} {(rawGenre ?? "").Trim()}".ToLowerInvariant();
        if (combined.Contains("symphon", StringComparison.Ordinal) || combined.Contains("sinfonia", StringComparison.Ordinal)
            || combined.Contains("交响", StringComparison.Ordinal))
            return "symphonies";
        if (combined.Contains("concerto", StringComparison.Ordinal) || combined.Contains("konzert", StringComparison.Ordinal)
            || combined.Contains("concertino", StringComparison.Ordinal) || combined.Contains("协奏", StringComparison.Ordinal))
            return "concertos";
        if (combined.Contains("quartet", StringComparison.Ordinal) || combined.Contains("quintet", StringComparison.Ordinal)
            || combined.Contains("sextet", StringComparison.Ordinal) || combined.Contains("septet", StringComparison.Ordinal)
            || combined.Contains("octet", StringComparison.Ordinal) || combined.Contains("string trio", StringComparison.Ordinal)
            || combined.Contains("piano trio", StringComparison.Ordinal) || combined.Contains("chamber", StringComparison.Ordinal)
            || combined.Contains("四重奏", StringComparison.Ordinal) || combined.Contains("五重奏", StringComparison.Ordinal)
            || combined.Contains("三重奏", StringComparison.Ordinal) || combined.Contains("室内乐", StringComparison.Ordinal))
            return "chamber";
        if (combined.Contains("sonata", StringComparison.Ordinal) || combined.Contains("sonate", StringComparison.Ordinal)
            || combined.Contains("partita", StringComparison.Ordinal) || combined.Contains("variation", StringComparison.Ordinal)
            || combined.Contains("nocturne", StringComparison.Ordinal) || combined.Contains("etude", StringComparison.Ordinal)
            || combined.Contains("étude", StringComparison.Ordinal) || combined.Contains("prelude", StringComparison.Ordinal)
            || combined.Contains("prélude", StringComparison.Ordinal) || combined.Contains("fugue", StringComparison.Ordinal)
            || combined.Contains("toccata", StringComparison.Ordinal) || combined.Contains("impromptu", StringComparison.Ordinal)
            || combined.Contains("ballade", StringComparison.Ordinal) || combined.Contains("scherzo", StringComparison.Ordinal)
            || combined.Contains("mazurka", StringComparison.Ordinal) || combined.Contains("polonaise", StringComparison.Ordinal)
            || combined.Contains("waltz", StringComparison.Ordinal) || combined.Contains("valse", StringComparison.Ordinal)
            || combined.Contains("chaconne", StringComparison.Ordinal) || combined.Contains("solo", StringComparison.Ordinal)
            || combined.Contains("奏鸣", StringComparison.Ordinal) || combined.Contains("变奏曲", StringComparison.Ordinal)
            || combined.Contains("夜曲", StringComparison.Ordinal) || combined.Contains("练习曲", StringComparison.Ordinal)
            || combined.Contains("前奏曲", StringComparison.Ordinal)
            || (combined.Contains("suite", StringComparison.Ordinal) && !combined.Contains("orchestral suite", StringComparison.Ordinal))
            || combined.Contains("组曲", StringComparison.Ordinal))
            return "solo";
        return "other";
    }

    public static bool IsEnsembleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        string lower = name.Trim().ToLowerInvariant();
        return EnsembleTokens.Any(lower.Contains);
    }

    public static SeparatedPerformers SeparatePerformers(
        IEnumerable<string> artists,
        string? albumArtist,
        IEnumerable<string> conductors,
        string? explicitOrchestra = null,
        IEnumerable<string>? explicitSoloists = null)
    {
        var conductorKeys = conductors
            .Select(c => c.Trim().ToLowerInvariant())
            .Where(c => c.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        string? ensemble = string.IsNullOrWhiteSpace(explicitOrchestra) ? null : explicitOrchestra.Trim();
        var soloists = explicitSoloists is null
            ? new List<string>()
            : explicitSoloists.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

        foreach (string raw in artists)
        {
            string clean = (raw ?? "").Trim();
            if (clean.Length == 0) continue;
            if (IsEnsembleName(clean))
            {
                ensemble ??= clean;
                continue;
            }
            if (conductorKeys.Contains(clean.ToLowerInvariant())) continue;
            if (!soloists.Any(s => string.Equals(s, clean, StringComparison.OrdinalIgnoreCase)))
            {
                soloists.Add(clean);
            }
        }

        if (ensemble is null && !string.IsNullOrWhiteSpace(albumArtist) && IsEnsembleName(albumArtist))
        {
            ensemble = albumArtist.Trim();
        }

        return new SeparatedPerformers(soloists, ensemble);
    }

    public static string AbbreviateEnsemble(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        if (LazyTables.Value.EnsembleAbbreviations.TryGetValue(trimmed.ToLowerInvariant(), out string? abbr))
        {
            return abbr;
        }

        string outName = trimmed
            .Replace("Philharmonic Orchestra", "PO", StringComparison.Ordinal)
            .Replace("Symphony Orchestra", "SO", StringComparison.Ordinal)
            .Replace("Chamber Orchestra", "CO", StringComparison.Ordinal)
            .Replace("Radio Symphony Orchestra", "RSO", StringComparison.Ordinal)
            .Replace("Philharmoniker", "Phil.", StringComparison.Ordinal)
            .Replace("Symphoniker", "Sym.", StringComparison.Ordinal);
        return outName;
    }

    private static bool IsCjkChar(char c) =>
        c is (>= '\u4E00' and <= '\u9FFF')
            or (>= '\u3400' and <= '\u4DBF')
            or (>= '\u3040' and <= '\u30FF')
            or (>= '\uAC00' and <= '\uD7AF');

    public static string ShortSurname(string? name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return "";
        int commaIdx = trimmed.IndexOf(',');
        if (commaIdx > 0)
        {
            string beforeComma = trimmed[..commaIdx].Trim();
            if (beforeComma.Length > 0) return beforeComma;
        }
        string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return trimmed;
        if (parts.All(p => p.Length > 0 && p.All(IsCjkChar)))
        {
            return string.Concat(parts);
        }
        if (parts.Length >= 2)
        {
            string prev = parts[^2].ToLowerInvariant();
            if (prev is "vaughan" or "de" or "di" or "da" or "del" or "della" or "dos" or "das" or "du" or "la" or "le" or "saint" or "st.")
            {
                return $"{parts[^2]} {parts[^1]}";
            }
        }
        return parts[^1];
    }

    public static IReadOnlyList<string> BuildClassicalCreditTiers(
        string? composer,
        IEnumerable<string> conductors,
        IEnumerable<string> soloists,
        string? ensemble,
        int? year)
    {
        string cleanComposer = (composer ?? "").Trim();
        var cleanConductors = conductors.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var cleanSoloists = soloists.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        string? cleanEnsemble = string.IsNullOrWhiteSpace(ensemble) ? null : ensemble.Trim();
        string? yearStr = year is > 0 ? year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        static string JoinParts(params string?[] parts) =>
            string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

        static string? FormatPerformerCredit(IReadOnlyList<string> cList, IReadOnlyList<string> sList)
        {
            string? c = cList.Count > 0 ? string.Join(", ", cList) : null;
            string? s = sList.Count > 0 ? string.Join(", ", sList) : null;
            return (c, s) switch
            {
                (not null, not null) => $"{c} / {s}",
                (not null, null) => c,
                (null, not null) => s,
                _ => null,
            };
        }

        string? perfAll = FormatPerformerCredit(cleanConductors, cleanSoloists);
        IReadOnlyList<string> firstSolo = cleanSoloists.Count > 0 ? [cleanSoloists[0]] : [];
        string? perfFirstSolo = FormatPerformerCredit(cleanConductors, firstSolo);
        string? conductorOnly = cleanConductors.Count > 0
            ? string.Join(", ", cleanConductors)
            : (firstSolo.Count > 0 ? firstSolo[0] : null);
        string? abbrEnsemble = cleanEnsemble is not null ? AbbreviateEnsemble(cleanEnsemble) : null;
        string? compOpt = cleanComposer.Length > 0 ? cleanComposer : null;

        var rawTiers = new[]
        {
            JoinParts(compOpt, perfAll, cleanEnsemble, yearStr),
            JoinParts(compOpt, perfAll, abbrEnsemble, yearStr),
            JoinParts(compOpt, perfFirstSolo, abbrEnsemble, yearStr),
            JoinParts(compOpt, conductorOnly, yearStr),
            JoinParts(
                cleanComposer.Length > 0 ? ShortSurname(cleanComposer) : null,
                conductorOnly is not null ? ShortSurname(conductorOnly) : null,
                yearStr),
        };

        var deduped = new List<string>(rawTiers.Length);
        foreach (string tier in rawTiers)
        {
            if (tier.Length > 0 && !deduped.Contains(tier, StringComparer.Ordinal))
            {
                deduped.Add(tier);
            }
        }
        return deduped;
    }

    public static double EstimateTextWidthPx(string? text, double fontSizePx)
    {
        if (string.IsNullOrEmpty(text)) return 0.0;
        double sum = 0.0;
        foreach (char ch in text)
        {
            sum += ch > 0x2E7F ? fontSizePx : fontSizePx * 0.54;
        }
        return sum;
    }

    public static string SelectBestClassicalCreditTier(IReadOnlyList<string> tiers, double maxWidthPx = 460.0)
    {
        if (tiers.Count == 0) return "";
        foreach (string tier in tiers)
        {
            if (EstimateTextWidthPx(tier, 13.5) <= maxWidthPx)
            {
                return tier;
            }
        }
        return tiers[^1];
    }

    public static string FormatClassicalComposerWorkLine(string? composer, string? workTitle, string? fallbackTitle = null)
    {
        string cleanComposer = (composer ?? "").Trim();
        string cleanWork = string.IsNullOrWhiteSpace(workTitle) ? (fallbackTitle ?? "").Trim() : workTitle.Trim();
        if (cleanComposer.Length == 0) return cleanWork;
        if (cleanWork.Length == 0) return cleanComposer;

        string surname = ShortSurname(cleanComposer);
        if (StartsWithComposerPrefix(cleanWork, cleanComposer)
            || (!string.IsNullOrWhiteSpace(surname)
                && !string.Equals(surname, cleanComposer, StringComparison.OrdinalIgnoreCase)
                && StartsWithComposerPrefix(cleanWork, surname)))
        {
            return cleanWork;
        }

        return $"{cleanComposer} - {cleanWork}";
    }

    private static bool StartsWithComposerPrefix(string work, string prefix)
    {
        if (prefix.Length == 0) return false;
        return work.StartsWith(prefix + ":", StringComparison.OrdinalIgnoreCase)
            || work.StartsWith(prefix + "：", StringComparison.OrdinalIgnoreCase)
            || work.StartsWith(prefix + " - ", StringComparison.OrdinalIgnoreCase)
            || work.StartsWith(prefix + " — ", StringComparison.OrdinalIgnoreCase)
            || work.StartsWith(prefix + " – ", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<string> BuildClassicalPerformerCreditTiers(
        IEnumerable<string> artists,
        IEnumerable<string> conductors,
        int? year)
    {
        var cleanConductors = new List<string>();
        var conductorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in conductors)
        {
            string clean = (raw ?? "").Trim();
            if (clean.Length > 0 && conductorKeys.Add(clean))
            {
                cleanConductors.Add(clean);
            }
        }

        var cleanArtists = new List<string>();
        var artistKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in artists)
        {
            string clean = (raw ?? "").Trim();
            if (clean.Length > 0 && !conductorKeys.Contains(clean) && artistKeys.Add(clean))
            {
                cleanArtists.Add(clean);
            }
        }

        string? yearStr = year is > 0 ? year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        static string JoinParts(params string?[] parts) =>
            string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

        var abbrArtists = cleanArtists
            .Select(a => IsEnsembleName(a) ? AbbreviateEnsemble(a) : a)
            .ToList();
        string? artistsFull = cleanArtists.Count > 0 ? string.Join(", ", cleanArtists) : null;
        string? artistsAbbr = abbrArtists.Count > 0 ? string.Join(", ", abbrArtists) : null;
        string? firstArtistAbbr = abbrArtists.Count > 0 ? abbrArtists[0] : null;
        string? conductorsFull = cleanConductors.Count > 0 ? string.Join(", ", cleanConductors) : null;
        string? shortArtist = cleanArtists.Count > 0
            ? (IsEnsembleName(cleanArtists[0]) ? abbrArtists[0] : ShortSurname(cleanArtists[0]))
            : null;
        string? shortConductor = cleanConductors.Count > 0 ? ShortSurname(cleanConductors[0]) : null;

        var rawTiers = new[]
        {
            JoinParts(artistsFull, conductorsFull, yearStr),
            JoinParts(artistsAbbr, conductorsFull, yearStr),
            JoinParts(firstArtistAbbr, conductorsFull, yearStr),
            JoinParts(shortArtist, shortConductor, yearStr),
        };

        var deduped = new List<string>(rawTiers.Length);
        foreach (string tier in rawTiers)
        {
            if (tier.Length > 0 && !deduped.Contains(tier, StringComparer.Ordinal))
            {
                deduped.Add(tier);
            }
        }
        return deduped;
    }

    public static ClassicalNowPlayingLines FormatClassicalNowPlaying(
        string? rawTitle,
        string? explicitWork,
        string? explicitMovement,
        int? explicitMovementNumber,
        string? album,
        IEnumerable<string> composers,
        IEnumerable<string> artists,
        IEnumerable<string> conductors,
        int? year)
    {
        var parsed = SplitWorkAndMovement(rawTitle, explicitWork, explicitMovement, explicitMovementNumber);
        string fallbackTitle = (rawTitle ?? "").Trim();
        string workTitle = string.IsNullOrWhiteSpace(parsed.Work) ? fallbackTitle : parsed.Work;
        string movementLine = !string.IsNullOrWhiteSpace(parsed.Movement)
            ? (parsed.MovementNumber is int mn ? $"{FormatRomanNumeral(mn)}. {parsed.Movement}" : parsed.Movement)
            : (album ?? "").Trim();

        var composerList = new List<string>();
        var seenComposers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in composers)
        {
            string clean = (raw ?? "").Trim();
            if (clean.Length > 0 && seenComposers.Add(clean))
            {
                composerList.Add(clean);
            }
        }
        string composerCredit = string.Join(", ", composerList);

        string line1 = FormatClassicalComposerWorkLine(composerCredit, workTitle, fallbackTitle);
        var line3Tiers = BuildClassicalPerformerCreditTiers(artists, conductors, year);
        return new ClassicalNowPlayingLines(line1, movementLine, line3Tiers);
    }
}
