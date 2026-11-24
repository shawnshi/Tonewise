using System.Buffers.Binary;
using System.Text;

namespace Tonewise.SyncHost.Playlists;

/// <summary>
/// Lightweight zero-dependency converter from CJK Unified Ideographs (U+4E00..U+9FFF)
/// to compact tone-stripped Pinyin strings, backed by the embedded ICU-generated table
/// and curated song-title polyphone variants.
/// </summary>
internal static class HanziPinyinConverter
{
    private const char CjkStart = '\u4E00';
    private const char CjkEnd = '\u9FFF';
    private const int CjkCount = CjkEnd - CjkStart + 1;
    private const string ResourceName = "Tonewise.SyncHost.protocol.hanzi_pinyin_table.txt";

    private static readonly Dictionary<char, string[]> PolyphoneOverrides = new()
    {
        ['长'] = ["chang", "zhang"],
        ['乐'] = ["le", "yue"],
        ['还'] = ["hai", "huan"],
        ['行'] = ["xing", "hang"],
        ['重'] = ["zhong", "chong"],
        ['觉'] = ["jue", "jiao"],
        ['调'] = ["diao", "tiao"],
        ['着'] = ["zhe", "zhao"],
        ['了'] = ["le", "liao"],
        ['都'] = ["dou", "du"],
        ['的'] = ["de", "di"],
        ['地'] = ["di", "de"],
        ['得'] = ["de", "dei"],
        ['朝'] = ["chao", "zhao"],
        ['传'] = ["chuan", "zhuan"],
        ['弹'] = ["tan", "dan"],
        ['藏'] = ["cang", "zang"],
        ['曾'] = ["ceng", "zeng"],
        ['单'] = ["dan", "shan"],
        ['落'] = ["luo", "la"],
        ['血'] = ["xue", "xie"],
        ['露'] = ["lu", "lou"],
    };

    private static readonly Lazy<(string[] Syllables, ushort[] CharToSyllable)> Table = new(LoadTable);

    private static (string[] Syllables, ushort[] CharToSyllable) LoadTable()
    {
        using var stream = typeof(HanziPinyinConverter).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource: {ResourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string headerLine = reader.ReadLine() ?? "";
        string dataLine = reader.ReadLine() ?? "";

        string[] syllables = headerLine.Split(',');
        byte[] rawBytes = Convert.FromBase64String(dataLine.Trim());
        var charToSyllable = new ushort[CjkCount];

        int entryCount = Math.Min(CjkCount, rawBytes.Length / 2);
        for (int i = 0; i < entryCount; i++)
        {
            charToSyllable[i] = BinaryPrimitives.ReadUInt16LittleEndian(rawBytes.AsSpan(i * 2, 2));
        }

        return (syllables, charToSyllable);
    }

    internal static bool ContainsHanzi(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
        {
            if (c >= CjkStart && c <= CjkEnd) return true;
        }
        return false;
    }

    internal static bool IsPureAsciiAlphaNum(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!ok) return false;
        }
        return true;
    }

    internal static IReadOnlyList<string> ToCompactPinyinVariants(string normalized, int maxVariants = 4)
    {
        if (string.IsNullOrEmpty(normalized) || !ContainsHanzi(normalized) || maxVariants <= 0)
        {
            return [];
        }

        var (syllables, charToSyllable) = Table.Value;
        var builders = new List<StringBuilder>(maxVariants) { new(normalized.Length * 4) };

        foreach (char c in normalized)
        {
            if (c >= CjkStart && c <= CjkEnd)
            {
                if (PolyphoneOverrides.TryGetValue(c, out string[]? readings) && readings.Length > 1 && builders.Count < maxVariants)
                {
                    int currentCount = builders.Count;
                    for (int ri = 1; ri < readings.Length && builders.Count < maxVariants; ri++)
                    {
                        string alt = readings[ri];
                        for (int bi = 0; bi < currentCount && builders.Count < maxVariants; bi++)
                        {
                            var copy = new StringBuilder(builders[bi].ToString(), builders[bi].Length + alt.Length + 8);
                            copy.Append(alt);
                            builders.Add(copy);
                        }
                    }

                    string primary = readings[0];
                    for (int bi = 0; bi < currentCount; bi++)
                    {
                        builders[bi].Append(primary);
                    }
                }
                else
                {
                    int id = charToSyllable[c - CjkStart];
                    if (id > 0 && id < syllables.Length)
                    {
                        string syll = syllables[id];
                        for (int bi = 0; bi < builders.Count; bi++)
                        {
                            builders[bi].Append(syll);
                        }
                    }
                }
            }
            else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                for (int bi = 0; bi < builders.Count; bi++)
                {
                    builders[bi].Append(c);
                }
            }
        }

        var result = new List<string>(builders.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sb in builders)
        {
            if (sb.Length == 0) continue;
            string value = sb.ToString();
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }
        return result;
    }
}
