using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shelf.Api.Tests;

// Every sentence the pages show goes through T("…") (or Say("…") for one shown later, such as an activity log line),
// and every one must be in each language's table with the same placeholders. The browser's own sentences go
// through t("…") and live in wwwroot/words.js.
public sealed partial class TranslationTests
{
    private static readonly string Source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Shelf.Api"));
    private static readonly string[] Languages = ["es", "fr", "de"];

    [Fact]
    public void Every_sentence_on_the_pages_is_in_every_language()
    {
        var sentences = ServerSentences();
        Assert.True(sentences.Count > 0);
        var problems = new List<string>();
        foreach (var language in Languages)
        {
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Source, "Localization", $"{language}.json")))!;
            foreach (var (sentence, where) in sentences)
            {
                if (!table.TryGetValue(sentence, out var translated) || string.IsNullOrWhiteSpace(translated))
                {
                    problems.Add($"{language}: missing \"{sentence}\" ({where})");
                }
                else if (!Placeholders(sentence).SetEquals(Placeholders(translated)))
                {
                    problems.Add($"{language}: placeholders differ in \"{sentence}\"");
                }
            }

            problems.AddRange(table.Keys.Where(key => !sentences.ContainsKey(key)).Select(key => $"{language}: \"{key}\" is no longer used"));
        }

        Assert.True(problems.Count == 0, $"{problems.Count} translation problems:\n" + string.Join("\n", problems.Take(80)));
    }

    [Fact]
    public void Sentences_are_written_out_where_they_are_translated()
    {
        // T($"…") would look up a different sentence every time; values go in as T("… {0} …", value).
        var interpolated = Files("*.razor", "*.cs")
            .SelectMany(path => Interpolated().Matches(File.ReadAllText(path)).Select(match => $"{Path.GetFileName(path)}: {match.Value}"))
            .ToList();
        Assert.True(interpolated.Count == 0, string.Join("\n", interpolated));
    }

    [Fact]
    public void Every_sentence_in_the_browser_is_in_every_language()
    {
        var words = File.ReadAllText(Path.Combine(Source, "wwwroot", "words.js"));
        var sentences = Files("*.js").Where(path => !path.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}") && Path.GetFileName(path) != "words.js")
            .SelectMany(path => BrowserCall().Matches(File.ReadAllText(path)).Select(match => Unescape(match.Groups[1].Value)))
            .Concat(DataT().Matches(File.ReadAllText(Path.Combine(Source, "wwwroot", "offline.html"))).Select(match => match.Groups[1].Value.Trim()))
            .ToHashSet();
        Assert.NotEmpty(sentences);
        var missing = new List<string>();
        foreach (var language in Languages)
        {
            var block = Regex.Match(words, $@"\b{language}: \{{(.*?)\n  \}}", RegexOptions.Singleline).Groups[1].Value;
            var keys = BrowserEntry().Matches(block).Select(match => Unescape(match.Groups[1].Value)).ToHashSet();
            missing.AddRange(sentences.Where(sentence => !keys.Contains(sentence)).Select(sentence => $"{language}: \"{sentence}\""));
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    private static Dictionary<string, string> ServerSentences()
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Files("*.razor", "*.cs"))
        {
            var text = File.ReadAllText(path);
            foreach (Match match in Call().Matches(text))
            {
                found.TryAdd(Unescape(match.Groups[2].Value), $"{Path.GetFileName(path)}:{text[..match.Index].Count(character => character == '\n') + 1}");
            }
        }

        return found;
    }

    private static IEnumerable<string> Files(params string[] patterns) =>
        patterns.SelectMany(pattern => Directory.EnumerateFiles(Source, pattern, SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static HashSet<string> Placeholders(string text) => Placeholder().Matches(text).Select(match => match.Value).ToHashSet();

    private static string Unescape(string text) => text.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");

    // T("…") or Say("…"), as a call, not as part of another name.
    [GeneratedRegex(@"(?<![\w.])(?:(?:Localization\.)?Words\.)?(T|Say)\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex Call();

    [GeneratedRegex(@"(?<![\w.])(?:(?:Localization\.)?Words\.)?(T|Say)\(\s*\$")]
    private static partial Regex Interpolated();

    [GeneratedRegex(@"(?<![\w.])t\(\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex BrowserCall();

    [GeneratedRegex(@"^\s*""((?:[^""\\]|\\.)*)""\s*:", RegexOptions.Multiline)]
    private static partial Regex BrowserEntry();

    [GeneratedRegex(@"data-t(?:=""[^""]*"")?>([^<]+)<")]
    private static partial Regex DataT();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
