using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Shelf.Api.Books;

// A place in an EPUB, as KOReader writes it and as Shelf's reader does, one into the other. Both are paths through the
// chapter's XHTML. KOReader's is an XPointer: /body/DocFragment[3]/body/div/p[5]/text().120, the third file of the
// spine, its body's first div, that div's fifth p, 120 characters into its first words. Shelf's is an EPUB CFI:
// epubcfi(/6/6!/4/2/10/1:120), counting every element child as an even step, and each text node, as epub.js counts
// them (the nth of its parent's text nodes, 2n - 1), as an odd one.
// Words of nothing but spaces between blocks are passed over in KOReader's count of text(), as its engine drops them.
public static partial class KoreaderPlace
{
    public static int? ChapterOf(string xpointer) =>
        Fragment().Match(xpointer) is { Success: true } match && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1
            ? number - 1
            : null;

    // The chapter a CFI is in: its spine step, /6/2 the first.
    public static int? ChapterOfCfi(string cfi) =>
        Cfi().Match(cfi) is { Success: true } match && int.TryParse(match.Groups["spine"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var step) && step >= 2
            ? step / 2 - 1
            : null;

    // Which of two places in a book comes first, by their paths: less than 0 when the first does, 0 when they are one,
    // null when either is not a CFI. A place in an element comes after the start of the element itself.
    public static int? Compare(string first, string second)
    {
        if (Steps(first) is not { } one || Steps(second) is not { } other)
        {
            return null;
        }

        for (var i = 0; i < Math.Min(one.Count, other.Count); i++)
        {
            if (one[i] != other[i])
            {
                return one[i].CompareTo(other[i]);
            }
        }

        return one.Count.CompareTo(other.Count);
    }

    // A CFI's steps, the spine's first and the offset last.
    private static List<int>? Steps(string cfi)
    {
        var match = Cfi().Match(cfi);
        if (!match.Success)
        {
            return null;
        }

        var steps = new List<int> { int.Parse(match.Groups["spine"].Value, CultureInfo.InvariantCulture) };
        steps.AddRange(match.Groups["path"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(step => int.Parse(Assertion().Replace(step, ""), CultureInfo.InvariantCulture)));
        if (match.Groups["offset"].Success)
        {
            steps.Add(int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture));
        }

        return steps;
    }

    // KOReader's place in the chapter, as a CFI; null when the path is not one in this chapter.
    public static string? ToCfi(string xpointer, int chapter, XDocument document)
    {
        var match = Pointer().Match(xpointer);
        if (!match.Success || document.Root is not { } html || Child(html, "body") is not { } node)
        {
            return null;
        }

        var steps = new List<int> { 2 * (ElementIndex(node) + 1) };
        foreach (Capture capture in match.Groups["step"].Captures)
        {
            var (name, number) = Step(capture.Value);
            var next = node.Elements().Where(element => element.Name.LocalName == name).Skip(number - 1).FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            node = next;
            steps.Add(2 * (ElementIndex(node) + 1));
        }

        var offset = "";
        if (match.Groups["text"].Success)
        {
            var number = match.Groups["textn"].Success ? int.Parse(match.Groups["textn"].Value, CultureInfo.InvariantCulture) : 1;
            var texts = Texts(node).Where(text => !string.IsNullOrWhiteSpace(text.Value)).ToList();
            if (number >= 1 && number <= texts.Count)
            {
                var text = texts[number - 1];
                var at = match.Groups["offset"].Success ? int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
                steps.Add(2 * Texts(node).IndexOf(text) + 1);
                offset = ":" + Math.Clamp(at, 0, text.Value.Length).ToString(CultureInfo.InvariantCulture);
            }
        }

        var path = new StringBuilder($"epubcfi(/6/{2 * (chapter + 1)}!");
        foreach (var step in steps)
        {
            path.Append('/').Append(step.ToString(CultureInfo.InvariantCulture));
        }

        return path.Append(offset).Append(')').ToString();
    }

    // Shelf's place as KOReader's; null when the CFI does not lead anywhere in the chapter it names.
    public static string? ToXPointer(string cfi, XDocument document)
    {
        var match = Cfi().Match(cfi);
        if (!match.Success || document.Root is not XElement node)
        {
            return null;
        }

        var chapter = int.Parse(match.Groups["spine"].Value, CultureInfo.InvariantCulture) / 2 - 1;
        var steps = match.Groups["path"].Value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(step => int.Parse(Assertion().Replace(step, ""), CultureInfo.InvariantCulture)).ToList();
        var pointer = new StringBuilder($"/body/DocFragment[{chapter + 1}]");
        XText? text = null;
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step % 2 == 1)
            {
                // Words: the parent's ((step - 1) / 2 + 1)th text node; the last step there can be.
                text = Texts(node).ElementAtOrDefault((step - 1) / 2);
                break;
            }

            var next = node.Elements().Skip(step / 2 - 1).FirstOrDefault();
            if (next is null)
            {
                return null;
            }

            node = next;
            if (i > 0 || node.Name.LocalName == "body")
            {
                var same = node.ElementsBeforeSelf().Count(element => element.Name.LocalName == node.Name.LocalName) + 1;
                pointer.Append('/').Append(node.Name.LocalName);
                if (same > 1 || node.ElementsAfterSelf().Any(element => element.Name.LocalName == node.Name.LocalName))
                {
                    pointer.Append('[').Append(same.ToString(CultureInfo.InvariantCulture)).Append(']');
                }
            }
        }

        var offset = match.Groups["offset"].Success ? int.Parse(match.Groups["offset"].Value, CultureInfo.InvariantCulture) : 0;
        if (text is not null && !string.IsNullOrWhiteSpace(text.Value))
        {
            var number = Texts(node).Where(item => !string.IsNullOrWhiteSpace(item.Value)).ToList().IndexOf(text) + 1;
            pointer.Append("/text()");
            if (number > 1)
            {
                pointer.Append('[').Append(number.ToString(CultureInfo.InvariantCulture)).Append(']');
            }

            pointer.Append('.').Append(Math.Clamp(offset, 0, text.Value.Length).ToString(CultureInfo.InvariantCulture));
        }
        else if (Texts(node).Any(item => !string.IsNullOrWhiteSpace(item.Value)))
        {
            pointer.Append("/text().0");
        }

        return pointer.ToString();
    }

    // A parent's text nodes, as the browser's DOM has them: CDATA is a node of another kind there.
    private static List<XText> Texts(XElement parent) => [.. parent.Nodes().OfType<XText>().Where(text => text is not XCData)];

    private static XElement? Child(XElement parent, string name) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == name);

    private static int ElementIndex(XElement element) => element.ElementsBeforeSelf().Count();

    private static (string Name, int Number) Step(string step)
    {
        var open = step.IndexOf('[');
        return open < 0 ? (step, 1) : (step[..open], int.Parse(step[(open + 1)..^1], CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"DocFragment\[(\d+)\]")]
    private static partial Regex Fragment();

    [GeneratedRegex(@"^/body/DocFragment\[\d+\]/body(?:/(?<step>[A-Za-z][\w-]*(?:\[\d+\])?))*?(?:/(?<text>text\(\))(?:\[(?<textn>\d+)\])?)?(?:\.(?<offset>\d+))?$")]
    private static partial Regex Pointer();

    [GeneratedRegex(@"^epubcfi\(/6/(?<spine>\d+)(?:\[[^\]]*\])?!(?<path>(?:/\d+(?:\[[^\]]*\])?)*)(?::(?<offset>\d+))?(?:\[[^\]]*\])?\)$")]
    private static partial Regex Cfi();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex Assertion();
}
