using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Tonewise.SyncHost.Workbench;

namespace Tonewise.SyncHost.Playlists;

public static partial class PlaylistTrackMatcher
{
    [Flags]
    internal enum VersionQualifiers
    {
        None = 0,
        Remix = 1 << 0,
        Instrumental = 1 << 1,
        Karaoke = 1 << 2,
        Acoustic = 1 << 3,
        Acapella = 1 << 4,
        Demo = 1 << 5,
        Cover = 1 << 6,
        SpeedMod = 1 << 7,
        Live = 1 << 8,
    }

    internal const VersionQualifiers HardBlockQualifierMask =
        VersionQualifiers.Remix
        | VersionQualifiers.Instrumental
        | VersionQualifiers.Karaoke
        | VersionQualifiers.Acoustic
        | VersionQualifiers.Acapella
        | VersionQualifiers.Demo
        | VersionQualifiers.Cover
        | VersionQualifiers.SpeedMod;

    internal readonly record struct StructuralMarkers(
        HashSet<string> Numbers,
        HashSet<string> Romans,
        HashSet<string> CjkOrdinals
    );

    internal readonly record struct TitleVariantInfo(
        string Normalized,
        string Compact,
        StructuralMarkers Markers,
        int Tier
    );

    internal sealed class IndexedTrack
    {
        public LocalLibraryTrackRef Track { get; }
        public int OriginalIndex { get; }
        public VersionQualifiers Qualifiers { get; }
        public VersionQualifiers HardQualifiers => Qualifiers & HardBlockQualifierMask;
        public bool IsLive => (Qualifiers & VersionQualifiers.Live) != 0;
        public HashSet<string> PrimaryArtistTokens { get; }
        public HashSet<string> AllCreditTokens { get; }
        public string CombinedCreditFlat { get; }
        public string PrimaryArtistKey { get; }
        public string NormalizedAlbum { get; }
        public int LosslessTier { get; }
        public IReadOnlyList<TitleVariantInfo> TitleVariantInfos { get; }
        public StructuralMarkers WorkAndTitleMarkers { get; }

        public IndexedTrack(LocalLibraryTrackRef track, int originalIndex)
        {
            Track = track;
            OriginalIndex = originalIndex;

            string preNormTitle = PreNormalizeUnicode(track.Title);
            Qualifiers = ExtractVersionQualifiersFromPreNorm(preNormTitle);

            PrimaryArtistTokens = ExtractArtistTokens(track.ArtistsDisplay);
            AllCreditTokens = new HashSet<string>(PrimaryArtistTokens, StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(track.ComposersDisplay))
            {
                AllCreditTokens.UnionWith(ExtractArtistTokens(track.ComposersDisplay));
            }
            if (!string.IsNullOrWhiteSpace(track.ConductorsDisplay))
            {
                AllCreditTokens.UnionWith(ExtractArtistTokens(track.ConductorsDisplay));
            }

            CombinedCreditFlat = NormalizeTokenText($"{track.ArtistsDisplay} {track.ComposersDisplay} {track.ConductorsDisplay}");
            PrimaryArtistKey = BuildCanonicalArtistKey(PrimaryArtistTokens, AllCreditTokens);
            NormalizedAlbum = NormalizeAlbum(track.Album);
            LosslessTier = GetLosslessTier(track.RelPath, track.FullPath);

            var variants = new List<TitleVariantInfo>(4);
            var seenNorm = new HashSet<string>(StringComparer.Ordinal);

            void AddVariant(string norm, int tier)
            {
                if (string.IsNullOrEmpty(norm)) return;
                if (!seenNorm.Add(norm))
                {
                    for (int i = 0; i < variants.Count; i++)
                    {
                        if (string.Equals(variants[i].Normalized, norm, StringComparison.Ordinal) && tier > variants[i].Tier)
                        {
                            variants[i] = variants[i] with { Tier = tier };
                        }
                    }
                    return;
                }
                string compact = CompactText(norm);
                variants.Add(new TitleVariantInfo(norm, compact, ExtractStructuralMarkers(norm), tier));
            }

            var titleVariants = NormalizeClassicalTitleVariants(track.Title);
            if (titleVariants.Count > 0)
            {
                AddVariant(titleVariants[0], 1000);
                for (int i = 1; i < titleVariants.Count; i++)
                {
                    AddVariant(titleVariants[i], 970);
                }
            }

            if (!string.IsNullOrWhiteSpace(track.WorkName) && !string.IsNullOrWhiteSpace(track.MovementName))
            {
                AddVariant(NormalizeTitle($"{track.WorkName} {track.MovementName}"), 990);
                AddVariant(NormalizeTitle(track.MovementName), 970);
            }
            else if (!string.IsNullOrWhiteSpace(track.MovementName))
            {
                AddVariant(NormalizeTitle(track.MovementName), 970);
            }

            TitleVariantInfos = variants;

            var workTitleBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(track.Title)) workTitleBuilder.Append(NormalizeTitle(track.Title)).Append(' ');
            if (!string.IsNullOrWhiteSpace(track.WorkName)) workTitleBuilder.Append(NormalizeTitle(track.WorkName)).Append(' ');
            if (!string.IsNullOrWhiteSpace(track.MovementName)) workTitleBuilder.Append(NormalizeTitle(track.MovementName));
            WorkAndTitleMarkers = ExtractStructuralMarkers(workTitleBuilder.ToString().Trim());
        }
    }

    internal readonly record struct CandidateRank(
        int TitleScore,
        int ArtistScore,
        int LiveMatchScore,
        int AlbumScore,
        int DurationScore,
        int LosslessTier,
        int SampleRateHz,
        int OriginalIndex
    ) : IComparable<CandidateRank>
    {
        public int CompareTo(CandidateRank other)
        {
            int c = TitleScore.CompareTo(other.TitleScore);
            if (c != 0) return c;
            c = ArtistScore.CompareTo(other.ArtistScore);
            if (c != 0) return c;
            c = LiveMatchScore.CompareTo(other.LiveMatchScore);
            if (c != 0) return c;
            c = AlbumScore.CompareTo(other.AlbumScore);
            if (c != 0) return c;
            c = DurationScore.CompareTo(other.DurationScore);
            if (c != 0) return c;
            c = LosslessTier.CompareTo(other.LosslessTier);
            if (c != 0) return c;
            c = SampleRateHz.CompareTo(other.SampleRateHz);
            if (c != 0) return c;
            return other.OriginalIndex.CompareTo(OriginalIndex);
        }
    }

    private const uint LcmapSimplifiedChinese = 0x02000000;

    private static readonly HashSet<string> RomanMovementTokens = new(StringComparer.Ordinal)
    {
        "i", "ii", "iii", "iv", "v", "vi", "vii", "viii", "ix", "x", "xi", "xii"
    };

    private static readonly HashSet<string> PlaceholderArtists = new(StringComparer.Ordinal)
    {
        "various artists", "various", "va", "群星", "合辑",
        "unknown artist", "unknown", "未知艺术家", "未知歌手"
    };

    private static readonly HashSet<string> CreditStopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "von", "van", "de", "la", "der", "den", "des", "di", "da", "del",
        "orchestra", "philharmonic", "philharmoniker", "symphony", "sinfonieorchester",
        "ensemble", "quartet", "quintet", "trio", "choir", "chorus", "band", "chamber",
        "royal", "london", "berlin", "vienna", "new", "york", "national", "state",
        "radio", "feat", "with", "featuring", "presents"
    };

    private static readonly Lazy<IReadOnlyList<string>> NormalizedWhitelist = new(() =>
        ArtistEntitySplitter.Whitelist
            .Select(PreNormalizeUnicode)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length)
            .ToList());

    internal static string PreNormalizeUnicode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        string s = raw.Replace('\u00A0', ' ').Trim();
        s = ToSimplifiedChinese(s);
        s = s.Normalize(NormalizationForm.FormKC);
        s = FoldLatinDiacriticsAndLowercase(s);
        return MultiSpaceRegex().Replace(s, " ").Trim();
    }

    private static string ToSimplifiedChinese(string input)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(input)) return input;

        bool hasCjkOrCompat = false;
        foreach (char c in input)
        {
            if (c >= '\u3400')
            {
                hasCjkOrCompat = true;
                break;
            }
        }
        if (!hasCjkOrCompat) return input;

        try
        {
            char[] dest = new char[input.Length + 4];
            int written = LCMapStringEx(
                "zh-CN",
                LcmapSimplifiedChinese,
                input,
                input.Length,
                dest,
                dest.Length,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            return written > 0 ? new string(dest, 0, written) : input;
        }
        catch
        {
            return input;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        char[] lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);

    private static string FoldLatinDiacriticsAndLowercase(string input)
    {
        string lower = input.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);

        foreach (char c in lower)
        {
            switch (c)
            {
                case 'ß': sb.Append("ss"); continue;
                case 'æ': sb.Append("ae"); continue;
                case 'œ': sb.Append("oe"); continue;
                case 'ø': sb.Append('o'); continue;
                case 'ł': sb.Append('l'); continue;
                case 'đ': sb.Append('d'); continue;
                case 'ð': sb.Append('d'); continue;
                case 'þ': sb.Append("th"); continue;
            }

            if (c >= '\u0300' && c <= '\u036F')
            {
                continue;
            }

            // Restrict FormD stripping strictly to Latin-extended blocks so Japanese kana dakuten (が/ぱ) stays intact.
            if ((c >= '\u00C0' && c <= '\u024F') || (c >= '\u1E00' && c <= '\u1EFF'))
            {
                string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
                foreach (char dc in decomposed)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(dc) != UnicodeCategory.NonSpacingMark)
                    {
                        sb.Append(dc);
                    }
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    internal static string NormalizePreNormalizedTokenText(string preNorm)
    {
        if (string.IsNullOrWhiteSpace(preNorm)) return "";
        var sb = new StringBuilder(preNorm.Length);
        bool prevSpace = true;
        foreach (char c in preNorm)
        {
            if (c is '\'' or '\u2018' or '\u2019' or '\u02BC')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().Trim();
    }

    internal static string StripEditionDecorations(string preNorm)
    {
        if (string.IsNullOrWhiteSpace(preNorm)) return "";
        string s = DashEditionSuffixRegex().Replace(preNorm, "");
        s = FeaturingParenRegex().Replace(s, " ");
        s = RemasterSuffixRegex().Replace(s, " ");
        s = SingleSuffixRegex().Replace(s, " ");
        return s.Trim();
    }

    internal static string CompactText(string normalized) =>
        string.IsNullOrEmpty(normalized) ? "" : normalized.Replace(" ", "", StringComparison.Ordinal);

    internal static bool ContainsEastAsianScript(string s)
    {
        foreach (char c in s)
        {
            if ((c >= '\u4E00' && c <= '\u9FFF')
                || (c >= '\u3400' && c <= '\u4DBF')
                || (c >= '\u3040' && c <= '\u30FF')
                || (c >= '\u31F0' && c <= '\u31FF')
                || (c >= '\uAC00' && c <= '\uD7AF')
                || (c >= '\u1100' && c <= '\u11FF')
                || (c >= '\u3130' && c <= '\u318F'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsCjkNumeralChar(char c) =>
        c is '一' or '二' or '三' or '四' or '五' or '六' or '七' or '八' or '九' or '十' or '零' or '百' or '千';

    internal static VersionQualifiers ExtractVersionQualifiers(string? raw) =>
        ExtractVersionQualifiersFromPreNorm(PreNormalizeUnicode(raw));

    internal static VersionQualifiers ExtractVersionQualifiersFromPreNorm(string preNorm)
    {
        if (string.IsNullOrEmpty(preNorm)) return VersionQualifiers.None;
        var q = VersionQualifiers.None;

        if (RemixQualifierRegex().IsMatch(preNorm) || preNorm.Contains("混音", StringComparison.Ordinal))
            q |= VersionQualifiers.Remix;

        if (InstrumentalQualifierRegex().IsMatch(preNorm)
            || preNorm.Contains("伴奏", StringComparison.Ordinal)
            || preNorm.Contains("纯音乐", StringComparison.Ordinal)
            || preNorm.Contains("消音", StringComparison.Ordinal)
            || preNorm.Contains("无人声", StringComparison.Ordinal)
            || preNorm.Contains("乐器版", StringComparison.Ordinal))
            q |= VersionQualifiers.Instrumental;

        if (KaraokeQualifierRegex().IsMatch(preNorm) || preNorm.Contains("卡拉ok", StringComparison.Ordinal))
            q |= VersionQualifiers.Karaoke;

        if (AcousticQualifierRegex().IsMatch(preNorm)
            || preNorm.Contains("不插电", StringComparison.Ordinal)
            || preNorm.Contains("木吉他版", StringComparison.Ordinal))
            q |= VersionQualifiers.Acoustic;

        if (AcapellaQualifierRegex().IsMatch(preNorm)
            || preNorm.Contains("清唱", StringComparison.Ordinal)
            || preNorm.Contains("阿卡贝拉", StringComparison.Ordinal))
            q |= VersionQualifiers.Acapella;

        if (DemoQualifierRegex().IsMatch(preNorm) || preNorm.Contains("小样", StringComparison.Ordinal))
            q |= VersionQualifiers.Demo;

        if (CoverQualifierRegex().IsMatch(preNorm) || preNorm.Contains("翻唱", StringComparison.Ordinal))
            q |= VersionQualifiers.Cover;

        if (SpeedModQualifierRegex().IsMatch(preNorm)
            || preNorm.Contains("变速", StringComparison.Ordinal)
            || preNorm.Contains("加速版", StringComparison.Ordinal)
            || preNorm.Contains("降调", StringComparison.Ordinal))
            q |= VersionQualifiers.SpeedMod;

        if (LiveQualifierRegex().IsMatch(preNorm)
            || preNorm.Contains("现场", StringComparison.Ordinal)
            || preNorm.Contains("演唱会", StringComparison.Ordinal)
            || preNorm.Contains("音乐会", StringComparison.Ordinal))
            q |= VersionQualifiers.Live;

        return q;
    }

    internal static StructuralMarkers ExtractStructuralMarkers(string normalized)
    {
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var romans = new HashSet<string>(StringComparer.Ordinal);
        var cjkOrdinals = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new StructuralMarkers(numbers, romans, cjkOrdinals);
        }

        foreach (string token in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsAsciiDigits(token))
            {
                string trimmed = token.TrimStart('0');
                numbers.Add(trimmed.Length == 0 ? "0" : trimmed);
            }
            else if (RomanMovementTokens.Contains(token))
            {
                romans.Add(token);
            }
        }

        foreach (Match m in CjkOrdinalRegex().Matches(normalized))
        {
            cjkOrdinals.Add(m.Value);
        }

        return new StructuralMarkers(numbers, romans, cjkOrdinals);
    }

    private static bool IsAsciiDigits(string token)
    {
        if (token.Length == 0) return false;
        foreach (char c in token)
        {
            if (c < '0' || c > '9') return false;
        }
        return true;
    }

    internal static bool AreStructurallyCompatible(StructuralMarkers a, StructuralMarkers b)
    {
        if (a.Numbers.Count > 0 && b.Numbers.Count > 0
            && !a.Numbers.IsSubsetOf(b.Numbers) && !b.Numbers.IsSubsetOf(a.Numbers))
        {
            return false;
        }

        if (a.Romans.Count > 0 && b.Romans.Count > 0
            && !a.Romans.IsSubsetOf(b.Romans) && !b.Romans.IsSubsetOf(a.Romans))
        {
            return false;
        }

        if (a.CjkOrdinals.Count > 0 && b.CjkOrdinals.Count > 0
            && !a.CjkOrdinals.IsSubsetOf(b.CjkOrdinals) && !b.CjkOrdinals.IsSubsetOf(a.CjkOrdinals))
        {
            return false;
        }

        return true;
    }

    internal static bool IsPlaceholderOnlyArtist(HashSet<string> tokens)
    {
        if (tokens.Count == 0) return true;
        foreach (string t in tokens)
        {
            if (!string.IsNullOrEmpty(t) && !PlaceholderArtists.Contains(t))
            {
                return false;
            }
        }
        return true;
    }

    internal static IReadOnlyList<string> ExtractSignificantCreditWords(HashSet<string> tokens)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            if (string.IsNullOrEmpty(token) || PlaceholderArtists.Contains(token)) continue;
            foreach (string w in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CreditStopWords.Contains(w) || PlaceholderArtists.Contains(w)) continue;
                bool keep = ContainsEastAsianScript(w) ? w.Length >= 2 : w.Length >= 4;
                if (keep) words.Add(w);
            }
        }
        return words.ToList();
    }

    private static string BuildCanonicalArtistKey(HashSet<string> primaryTokens, HashSet<string> allCreditTokens)
    {
        var source = !IsPlaceholderOnlyArtist(primaryTokens)
            ? primaryTokens
            : (!IsPlaceholderOnlyArtist(allCreditTokens) ? allCreditTokens : null);
        if (source is null) return "";

        return string.Join("|", source.Where(t => !PlaceholderArtists.Contains(t)).OrderBy(t => t, StringComparer.Ordinal));
    }

    internal static int ScoreArtistMatch(HashSet<string> seedTokens, string seedFlat, IndexedTrack cand)
    {
        var effectiveSeedTokens = seedTokens.Where(t => !string.IsNullOrEmpty(t) && !PlaceholderArtists.Contains(t)).ToList();
        if (effectiveSeedTokens.Count == 0) return 0;

        var nonPlaceholderCandTokens = cand.AllCreditTokens
            .Where(t => !string.IsNullOrEmpty(t) && !PlaceholderArtists.Contains(t))
            .ToList();
        if (nonPlaceholderCandTokens.Count == 0) return 0;

        int score = 0;
        var seedMatchedSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (string st in effectiveSeedTokens)
        {
            if (cand.AllCreditTokens.Contains(st))
            {
                score += 4;
                seedMatchedSet.Add(st);
                continue;
            }

            int bestSingleTokenScore = 0;
            foreach (string ct in nonPlaceholderCandTokens)
            {
                if (IsWholeWordContainment(st, ct) || IsInitialSurnameMatch(st, ct))
                {
                    bestSingleTokenScore = Math.Max(bestSingleTokenScore, 3);
                }
                else if (bestSingleTokenScore < 2 && IsFuzzyArtistTokenMatch(st, ct))
                {
                    bestSingleTokenScore = 2;
                }
            }

            if (bestSingleTokenScore > 0)
            {
                score += bestSingleTokenScore;
                seedMatchedSet.Add(st);
            }
        }

        if (score > 0
            && cand.PrimaryArtistTokens.Count > 0
            && seedMatchedSet.Count == effectiveSeedTokens.Count
            && effectiveSeedTokens.Count == cand.PrimaryArtistTokens.Count
            && effectiveSeedTokens.All(st => cand.PrimaryArtistTokens.Contains(st)))
        {
            score += 1;
        }

        if (score == 0
            && !string.IsNullOrEmpty(seedFlat)
            && !PlaceholderArtists.Contains(seedFlat)
            && !string.IsNullOrEmpty(cand.CombinedCreditFlat)
            && IsWholeWordContainment(seedFlat, cand.CombinedCreditFlat))
        {
            score = 1;
        }

        return score;
    }

    private static bool IsWholeWordContainment(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        string shorter = a.Length <= b.Length ? a : b;
        string longer = a.Length <= b.Length ? b : a;

        bool minLenOk = ContainsEastAsianScript(shorter) ? shorter.Length >= 2 : shorter.Length >= 3;
        if (!minLenOk) return false;

        if (ContainsEastAsianScript(shorter))
        {
            return longer.Contains(shorter, StringComparison.Ordinal);
        }

        int idx = 0;
        while ((idx = longer.IndexOf(shorter, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || longer[idx - 1] == ' ';
            int end = idx + shorter.Length;
            bool rightOk = end == longer.Length || longer[end] == ' ';
            if (leftOk && rightOk) return true;
            idx++;
        }
        return false;
    }

    private static bool IsInitialSurnameMatch(string a, string b)
    {
        if (ContainsEastAsianScript(a) || ContainsEastAsianScript(b)) return false;
        if (!a.Contains(' ', StringComparison.Ordinal) || !b.Contains(' ', StringComparison.Ordinal)) return false;

        string[] wa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] wb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wa.Length < 2 || wb.Length < 2) return false;

        string surnameA = wa[^1];
        string surnameB = wb[^1];
        if (surnameA.Length < 3 || !string.Equals(surnameA, surnameB, StringComparison.Ordinal)) return false;

        string[] shorterFirst = wa.Length <= wb.Length ? wa[..^1] : wb[..^1];
        string[] longerFirst = wa.Length <= wb.Length ? wb[..^1] : wa[..^1];
        if (shorterFirst.Length != longerFirst.Length) return false;

        for (int i = 0; i < shorterFirst.Length; i++)
        {
            string f1 = shorterFirst[i];
            string f2 = longerFirst[i];
            if (string.Equals(f1, f2, StringComparison.Ordinal)) continue;
            if (f1.Length == 1 && f2.StartsWith(f1[0])) continue;
            if (f2.Length == 1 && f1.StartsWith(f2[0])) continue;
            return false;
        }
        return true;
    }

    internal readonly record struct FuzzyArtistKeyEntry(
        string Key,
        bool HasSpace,
        uint CharMask,
        string[] LongWords,
        uint[] LongWordMasks,
        string Surname,
        uint SurnameMask,
        List<IndexedTrack> Tracks
    );

    internal static uint ComputeAsciiLetterMask(ReadOnlySpan<char> text)
    {
        uint mask = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 'a' && c <= 'z')
            {
                mask |= 1u << (c - 'a');
            }
            else if (c >= '0' && c <= '9')
            {
                mask |= 1u << 26;
            }
            else if (c == ' ')
            {
                mask |= 1u << 27;
            }
            else
            {
                mask |= 1u << 28;
            }
        }
        return mask;
    }

    internal static bool IsLevenshteinAtLeast80(ReadOnlySpan<char> a, uint maskA, ReadOnlySpan<char> b, uint maskB)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return false;
        int maxAllowedDist = maxLen / 5;
        if (Math.Abs(a.Length - b.Length) > maxAllowedDist) return false;
        int minDiffChars = Math.Max(BitOperations.PopCount(maskA & ~maskB), BitOperations.PopCount(maskB & ~maskA));
        if (minDiffChars > maxAllowedDist) return false;
        return BoundedLevenshtein(a, b, maxAllowedDist) <= maxAllowedDist;
    }

    internal static FuzzyArtistKeyEntry BuildFuzzyArtistKeyEntry(string key, List<IndexedTrack> tracks)
    {
        bool hasSpace = key.Contains(' ', StringComparison.Ordinal);
        uint charMask = ComputeAsciiLetterMask(key);
        if (!hasSpace)
        {
            return new FuzzyArtistKeyEntry(key, false, charMask, [], [], "", 0, tracks);
        }

        string[] rawWords = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var longWordList = new List<string>(rawWords.Length);
        for (int i = 0; i < rawWords.Length; i++)
        {
            if (rawWords[i].Length >= 5)
            {
                longWordList.Add(rawWords[i]);
            }
        }
        string[] longWords = longWordList.Count > 0 ? [.. longWordList] : [];
        uint[] longWordMasks = longWords.Length > 0
            ? Array.ConvertAll(longWords, static w => ComputeAsciiLetterMask(w))
            : [];

        int lastSpace = key.LastIndexOf(' ');
        string sur = key[(lastSpace + 1)..];
        string surname = sur.Length >= 7 ? sur : "";
        uint surnameMask = surname.Length >= 7 ? ComputeAsciiLetterMask(surname) : 0;

        return new FuzzyArtistKeyEntry(key, true, charMask, longWords, longWordMasks, surname, surnameMask, tracks);
    }

    internal static bool IsFuzzyArtistQueryMatchEntry(
        string q,
        bool qHasSpace,
        uint qMask,
        string[] qLongWords,
        uint[] qLongWordMasks,
        string qSurname,
        uint qSurnameMask,
        in FuzzyArtistKeyEntry entry)
    {
        if (q.Length < 5 || entry.Key.Length < 5 || ContainsEastAsianScript(q) || ContainsEastAsianScript(entry.Key))
        {
            return false;
        }

        if (IsLevenshteinAtLeast80(q, qMask, entry.Key, entry.CharMask))
        {
            return true;
        }

        if (!qHasSpace && entry.HasSpace)
        {
            for (int i = 0; i < entry.LongWords.Length; i++)
            {
                if (IsLevenshteinAtLeast80(q, qMask, entry.LongWords[i], entry.LongWordMasks[i]))
                {
                    return true;
                }
            }
        }
        else if (qHasSpace && !entry.HasSpace)
        {
            for (int i = 0; i < qLongWords.Length; i++)
            {
                if (IsLevenshteinAtLeast80(entry.Key, entry.CharMask, qLongWords[i], qLongWordMasks[i]))
                {
                    return true;
                }
            }
        }
        else if (qHasSpace && entry.HasSpace)
        {
            if (qSurname.Length >= 7 && entry.Surname.Length >= 7
                && IsLevenshteinAtLeast80(qSurname, qSurnameMask, entry.Surname, entry.SurnameMask))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsFuzzyArtistTokenMatch(string a, string b)
    {
        if (a.Length < 5 || b.Length < 5 || ContainsEastAsianScript(a) || ContainsEastAsianScript(b)) return false;

        double wholeSim = NormalizedLevenshteinSimilarity(a, b);
        if (wholeSim >= 0.80) return true;

        bool aHasSpace = a.Contains(' ', StringComparison.Ordinal);
        bool bHasSpace = b.Contains(' ', StringComparison.Ordinal);

        if (!aHasSpace && bHasSpace)
        {
            foreach (string w in b.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.Length >= 5 && NormalizedLevenshteinSimilarity(a, w) >= 0.80) return true;
            }
        }
        else if (aHasSpace && !bHasSpace)
        {
            foreach (string w in a.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.Length >= 5 && NormalizedLevenshteinSimilarity(b, w) >= 0.80) return true;
            }
        }
        else if (aHasSpace && bHasSpace)
        {
            int idxA = a.LastIndexOf(' ');
            int idxB = b.LastIndexOf(' ');
            string surA = a[(idxA + 1)..];
            string surB = b[(idxB + 1)..];
            if (surA.Length >= 7 && surB.Length >= 7 && NormalizedLevenshteinSimilarity(surA, surB) >= 0.80)
            {
                return true;
            }
        }

        return false;
    }

    internal static double NormalizedLevenshteinSimilarity(string a, string b)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 0.0;
        int maxDist = (int)Math.Floor(maxLen * 0.25) + 1;
        int dist = BoundedLevenshtein(a, b, maxDist);
        if (dist > maxDist) return 0.0;
        return 1.0 - (double)dist / maxLen;
    }

    internal static int BoundedLevenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int maxDist)
    {
        if (a.SequenceEqual(b)) return 0;
        if (maxDist < 0) return 1;
        if (Math.Abs(a.Length - b.Length) > maxDist) return maxDist + 1;

        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        if (a.Length < b.Length)
        {
            var tmp = a;
            a = b;
            b = tmp;
        }

        int m = b.Length;
        int[]? rentedPrev = null;
        int[]? rentedCurr = null;
        Span<int> prev = m < 256 ? stackalloc int[256] : (rentedPrev = new int[m + 1]);
        Span<int> curr = m < 256 ? stackalloc int[256] : (rentedCurr = new int[m + 1]);
        prev = prev[..(m + 1)];
        curr = curr[..(m + 1)];

        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];
            char ca = a[i - 1];

            for (int j = 1; j <= m; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                int v = Math.Min(Math.Min(del, ins), sub);
                curr[j] = v;
                if (v < rowMin) rowMin = v;
            }

            if (rowMin > maxDist) return maxDist + 1;

            curr.CopyTo(prev);
        }

        return prev[m];
    }

    internal static double ComputeFuzzyTitleSimilarity(
        IReadOnlyList<string> seedVariants,
        StructuralMarkers fullSeedMarkers,
        IndexedTrack cand)
    {
        if (seedVariants.Count == 0) return 0.0;
        if (!AreStructurallyCompatible(fullSeedMarkers, cand.WorkAndTitleMarkers)) return 0.0;

        double bestSim = 0.0;
        for (int sIdx = 0; sIdx < seedVariants.Count; sIdx++)
        {
            string seedNorm = seedVariants[sIdx];
            if (string.IsNullOrEmpty(seedNorm)) continue;

            string seedCompact = CompactText(seedNorm);
            bool minOk = ContainsEastAsianScript(seedCompact) ? seedCompact.Length >= 2 : seedCompact.Length >= 4;
            if (!minOk) continue;

            double variantWeight = sIdx == 0 ? 1.0 : 0.98;
            StructuralMarkers seedVariantMarkers = sIdx == 0 ? fullSeedMarkers : ExtractStructuralMarkers(seedNorm);
            string[]? seedWords = null;

            foreach (var variant in cand.TitleVariantInfos)
            {
                if (!AreStructurallyCompatible(seedVariantMarkers, variant.Markers)) continue;

                double localSim = 0.0;

                if (seedCompact.Length > 0 && string.Equals(seedCompact, variant.Compact, StringComparison.Ordinal))
                {
                    localSim = 0.98;
                }
                else
                {
                    localSim = Math.Max(localSim, ScoreLevenshteinTitlePair(seedNorm, variant.Normalized));
                    if (!string.Equals(seedCompact, seedNorm, StringComparison.Ordinal)
                        || !string.Equals(variant.Compact, variant.Normalized, StringComparison.Ordinal))
                    {
                        localSim = Math.Max(localSim, ScoreLevenshteinTitlePair(seedCompact, variant.Compact));
                    }

                    if (localSim < 0.95)
                    {
                        seedWords ??= seedNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        string[] candWords = variant.Normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        string[] shorterTokens = seedWords.Length <= candWords.Length ? seedWords : candWords;
                        string[] longerTokens = seedWords.Length <= candWords.Length ? candWords : seedWords;
                        string shorterStr = seedWords.Length <= candWords.Length ? seedNorm : variant.Normalized;
                        string longerStr = seedWords.Length <= candWords.Length ? variant.Normalized : seedNorm;
                        StructuralMarkers shorterMarkers = seedWords.Length <= candWords.Length ? seedVariantMarkers : variant.Markers;

                        if (shorterTokens.Length >= 2 && Math.Min(seedNorm.Length, variant.Normalized.Length) >= 6)
                        {
                            int matched = CountFuzzyMatchedTokens(shorterTokens, longerTokens);
                            double recall = (double)matched / shorterTokens.Length;
                            int unionCount = shorterTokens.Length + longerTokens.Length - matched;
                            double jaccard = unionCount > 0 ? (double)matched / unionCount : 0.0;

                            if ((recall >= 0.80 && jaccard >= 0.55)
                                || (shorterMarkers.Numbers.Count >= 1 && shorterTokens.Length >= 4 && recall >= 1.0 && jaccard >= 0.40))
                            {
                                localSim = Math.Max(localSim, 0.78 + 0.16 * jaccard);
                            }
                            else if (shorterStr.Length >= 8
                                     && longerStr.StartsWith(shorterStr + " ", StringComparison.Ordinal))
                            {
                                localSim = Math.Max(localSim, 0.82);
                            }
                        }

                        string shorterCompact = seedCompact.Length <= variant.Compact.Length ? seedCompact : variant.Compact;
                        string longerCompact = seedCompact.Length <= variant.Compact.Length ? variant.Compact : seedCompact;
                        if (ContainsEastAsianScript(shorterCompact)
                            && shorterCompact.Length >= 4
                            && longerCompact.Length > shorterCompact.Length
                            && longerCompact.StartsWith(shorterCompact, StringComparison.Ordinal)
                            && (double)shorterCompact.Length / longerCompact.Length >= 0.45)
                        {
                            localSim = Math.Max(localSim, 0.82);
                        }
                    }
                }

                if (localSim > 0.0)
                {
                    bestSim = Math.Max(bestSim, localSim * variantWeight);
                }
            }
        }

        return bestSim;
    }

    private static double ScoreLevenshteinTitlePair(string a, string b)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 0.0;

        bool isEastAsian = ContainsEastAsianScript(a) || ContainsEastAsianScript(b);
        if (isEastAsian)
        {
            if (maxLen < 8) return 0.0;
            int dist = BoundedLevenshtein(a, b, 1);
            if (dist != 1 || HasConflictingCjkNumeralDiff(a, b)) return 0.0;
            return 1.0 - (double)dist / maxLen;
        }
        else
        {
            if (maxLen < 6) return 0.0;
            int allowed = maxLen >= 10 ? (int)Math.Floor(maxLen * 0.20) : 1;
            int dist = BoundedLevenshtein(a, b, allowed);
            if (dist > allowed) return 0.0;
            double sim = 1.0 - (double)dist / maxLen;
            bool passes = (maxLen >= 6 && sim >= 0.83) || (maxLen >= 10 && sim >= 0.80);
            return passes ? sim : 0.0;
        }
    }

    private static bool HasConflictingCjkNumeralDiff(string a, string b)
    {
        int prefix = 0;
        int minLen = Math.Min(a.Length, b.Length);
        while (prefix < minLen && a[prefix] == b[prefix]) prefix++;

        int suffix = 0;
        while (suffix < minLen - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;

        for (int i = prefix; i < a.Length - suffix; i++)
        {
            if (IsCjkNumeralChar(a[i])) return true;
        }
        for (int j = prefix; j < b.Length - suffix; j++)
        {
            if (IsCjkNumeralChar(b[j])) return true;
        }
        return false;
    }

    private static int CountFuzzyMatchedTokens(string[] shorterTokens, string[] longerTokens)
    {
        bool[] used = new bool[longerTokens.Length];
        int matched = 0;

        foreach (string st in shorterTokens)
        {
            int hitIdx = -1;
            for (int j = 0; j < longerTokens.Length; j++)
            {
                if (!used[j] && string.Equals(st, longerTokens[j], StringComparison.Ordinal))
                {
                    hitIdx = j;
                    break;
                }
            }

            if (hitIdx < 0 && st.Length >= 6 && !ContainsEastAsianScript(st))
            {
                for (int j = 0; j < longerTokens.Length; j++)
                {
                    if (!used[j] && longerTokens[j].Length >= 6 && !ContainsEastAsianScript(longerTokens[j])
                        && BoundedLevenshtein(st, longerTokens[j], 1) <= 1)
                    {
                        hitIdx = j;
                        break;
                    }
                }
            }

            if (hitIdx >= 0)
            {
                used[hitIdx] = true;
                matched++;
            }
        }

        return matched;
    }

    internal static string NormalizeAlbum(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string preNorm = PreNormalizeUnicode(raw);
        string stripped = AlbumEditionParenRegex().Replace(preNorm, " ");
        stripped = AlbumEditionDashRegex().Replace(stripped, "");
        string norm = NormalizePreNormalizedTokenText(stripped);
        return string.IsNullOrEmpty(norm) ? NormalizePreNormalizedTokenText(preNorm) : norm;
    }

    internal static int ScoreAlbumMatch(string seedAlbumNorm, string candAlbumNorm)
    {
        if (string.IsNullOrEmpty(seedAlbumNorm) || string.IsNullOrEmpty(candAlbumNorm)) return 0;
        if (string.Equals(seedAlbumNorm, candAlbumNorm, StringComparison.Ordinal)) return 4;

        string shorter = seedAlbumNorm.Length <= candAlbumNorm.Length ? seedAlbumNorm : candAlbumNorm;
        string longer = seedAlbumNorm.Length <= candAlbumNorm.Length ? candAlbumNorm : seedAlbumNorm;

        bool minContainLen = ContainsEastAsianScript(shorter) ? shorter.Length >= 2 : shorter.Length >= 4;
        if (minContainLen && IsWholeWordContainment(shorter, longer)) return 2;

        var sw = seedAlbumNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var cw = candAlbumNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (sw.Count > 0 && cw.Count > 0)
        {
            int inter = sw.Count(cw.Contains);
            int union = sw.Count + cw.Count - inter;
            if (union > 0 && (double)inter / union >= 0.65) return 2;
        }

        return 0;
    }

    internal static int ScoreDurationProximity(int seedDurationMs, int candDurationMs)
    {
        if (seedDurationMs <= 0 || candDurationMs <= 0) return 0;
        int diff = Math.Abs(seedDurationMs - candDurationMs);
        if (diff <= 3_000) return 3;
        if (diff <= 10_000) return 2;
        if (diff <= 25_000) return 1;
        if (diff > 60_000) return -1;
        return 0;
    }

    internal static int GetLosslessTier(string? relPath, string? fullPath)
    {
        string? ext = Path.GetExtension(!string.IsNullOrWhiteSpace(relPath) ? relPath : fullPath);
        if (string.IsNullOrEmpty(ext)) return 0;

        return ext.ToLowerInvariant() switch
        {
            ".flac" or ".wav" or ".aiff" or ".alac" or ".ape" or ".wv" or ".tak" or ".dsf" or ".dff" => 2,
            ".m4a" or ".mp4" => 1,
            _ => 0,
        };
    }

    internal static CandidateRank ComputeCandidateRank(
        int titleScore,
        int artistScore,
        bool seedIsLive,
        string seedAlbumNorm,
        int seedDurationMs,
        IndexedTrack cand)
    {
        int liveMatch = seedIsLive == cand.IsLive ? 2 : 0;
        int albumScore = ScoreAlbumMatch(seedAlbumNorm, cand.NormalizedAlbum);
        int durationScore = ScoreDurationProximity(seedDurationMs, cand.Track.DurationMs);
        return new CandidateRank(
            TitleScore: titleScore,
            ArtistScore: artistScore,
            LiveMatchScore: liveMatch,
            AlbumScore: albumScore,
            DurationScore: durationScore,
            LosslessTier: cand.LosslessTier,
            SampleRateHz: cand.Track.SampleRateHz,
            OriginalIndex: cand.OriginalIndex
        );
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"第[一二三四五六七八九十零百千]+")]
    private static partial Regex CjkOrdinalRegex();

    [GeneratedRegex(@"\b(?:remix(?:es|ed)?|rework|bootleg|club\s+mix|dub\s+mix|extended\s+mix|vip\s+mix)\b")]
    private static partial Regex RemixQualifierRegex();

    [GeneratedRegex(@"\b(?:instrumental|off\s+vocal|minus\s+one)\b")]
    private static partial Regex InstrumentalQualifierRegex();

    [GeneratedRegex(@"\b(?:karaoke|ktv)\b")]
    private static partial Regex KaraokeQualifierRegex();

    [GeneratedRegex(@"\b(?:acoustic|unplugged)\b")]
    private static partial Regex AcousticQualifierRegex();

    [GeneratedRegex(@"\b(?:a\s*capp?ell?a)\b")]
    private static partial Regex AcapellaQualifierRegex();

    [GeneratedRegex(@"\b(?:demo)\b")]
    private static partial Regex DemoQualifierRegex();

    [GeneratedRegex(@"\b(?:cover)\b")]
    private static partial Regex CoverQualifierRegex();

    [GeneratedRegex(@"\b(?:sped\s+up|speed\s+up|slowed(?:\s*\+?\s*reverb)?|nightcore)\b")]
    private static partial Regex SpeedModQualifierRegex();

    [GeneratedRegex(@"\b(?:live)\b")]
    private static partial Regex LiveQualifierRegex();

    [GeneratedRegex(@"[\(\[【]\s*[^\)\]】]*(?:deluxe|remaster(?:ed)?|expanded|special|anniversary|collector|bonus\s+track|standard|explicit|clean|edition|豪华版|精选辑|精选集|重制版|重置版|典藏版|纪念版|周年版)[^\)\]】]*[\)\]】]")]
    private static partial Regex AlbumEditionParenRegex();

    [GeneratedRegex(@"(?:\s+[-–—]\s+(?:deluxe(?:\s+edition)?|expanded(?:\s+edition)?|special(?:\s+edition)?|remaster(?:ed)?(?:\s+\d{4})?|single|ep|豪华版|精选辑|重制版))+$")]
    private static partial Regex AlbumEditionDashRegex();
}
