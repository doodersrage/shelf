using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Shelf.Api.Readers;

// Every email's HTML, made from its plain text so the two always say the same: the subject as a heading under the
// shelf's mark, a paragraph for each block of text, "- " lines as a list, and links as buttons. A line that is only
// "Some words: https://…" becomes a button saying those words; a link on its own becomes one saying the message's
// Button, with the address under it for mail apps that will not follow a button. Styles are inline and the layout is
// tables, as mail apps take nothing else reliably; the colours are the app's own.
public static partial class EmailLayout
{
    private const string Brand = "#263e38";
    private const string OnBrand = "#f5f1e8";
    private const string Canvas = "#f5f1e8";
    private const string Surface = "#fffdf9";
    private const string Line = "#e4ddcd";
    private const string Accent = "#d5a46a";
    private const string Ink = "#252a28";
    private const string Muted = "#66716b";
    private const string Serif = "Lora, Georgia, 'Times New Roman', serif";
    private const string Sans = "Inter, -apple-system, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";

    // site: the shelf's own address (Email:PublicAddress), for the mark's picture and the footer's link; may be empty.
    public static string Html(EmailMessage message, string site)
    {
        var blocks = message.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // A last paragraph after the button is an aside (why the email came, what to do if it should not have), set off
        // as fine print.
        var lastButton = Array.FindLastIndex(blocks, block => !block.Contains('\n') && (LinkOnly().IsMatch(block) || LabelledLink().IsMatch(block)));
        var aside = lastButton >= 0 && lastButton < blocks.Length - 1 && !blocks[^1].StartsWith("- ", StringComparison.Ordinal);
        var body = new StringBuilder();
        for (var i = 0; i < blocks.Length; i++)
        {
            body.Append(aside && i == blocks.Length - 1
                ? $"<p style=\"margin:8px 0 18px;padding-top:16px;border-top:1px solid {Line};font-size:13px;line-height:1.5;color:{Muted};\">{string.Join("<br>", blocks[i].Split('\n', StringSplitOptions.TrimEntries).Select(Linked))}</p>\n"
                : Block(blocks[i], message.Button));
        }

        // The line a mail app shows beside the subject: the first block after the greeting.
        var preview = blocks.Skip(blocks.Length > 1 ? 1 : 0).FirstOrDefault() ?? "";
        var host = Uri.TryCreate(site, UriKind.Absolute, out var address) ? address.Host : "";
        var mark = site.Length > 0
            ? $"""<img src="{Attribute(site)}/icon-192.png" width="28" height="28" alt="" style="display:block;border:0;border-radius:7px;">"""
            : "";
        var footer = site.Length > 0
            ? $"""<a href="{Attribute(site)}" style="color:{Muted};text-decoration:underline;">{Text(host)}</a>"""
            : "Shelf";

        return $"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <meta name="supported-color-schemes" content="light">
            <title>{Text(message.Subject)}</title>
            </head>
            <body style="margin:0;padding:0;background:{Canvas};">
            <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{Text(Clip(Flatten(preview), 140))}</div>
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:{Canvas};">
            <tr><td align="center" style="padding:32px 16px;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="max-width:560px;">
            <tr><td style="background:{Brand};border-radius:12px 12px 0 0;padding:18px 28px;">
            <table role="presentation" cellpadding="0" cellspacing="0" border="0"><tr>
            {(mark.Length > 0 ? $"<td style=\"padding-right:10px;\">{mark}</td>" : "")}<td style="font-family:{Serif};font-size:20px;font-weight:600;color:{OnBrand};letter-spacing:0.2px;">Shelf</td>
            </tr></table>
            </td></tr>
            <tr><td style="background:{Accent};height:3px;line-height:3px;font-size:0;">&nbsp;</td></tr>
            <tr><td style="background:{Surface};border:1px solid {Line};border-top:0;border-radius:0 0 12px 12px;padding:28px 28px 12px;font-family:{Sans};font-size:16px;line-height:1.6;color:{Ink};">
            <h1 style="margin:0 0 20px;font-family:{Serif};font-size:24px;line-height:1.3;font-weight:600;color:{Ink};">{Text(message.Subject)}</h1>
            {body}
            </td></tr>
            <tr><td align="center" style="padding:20px 28px 0;font-family:{Sans};font-size:13px;line-height:1.5;color:{Muted};">
            {footer}
            </td></tr>
            </table>
            </td></tr>
            </table>
            </body>
            </html>
            """;
    }

    private static string Block(string block, string? button)
    {
        var lines = block.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length == 1 && LinkOnly().IsMatch(lines[0]))
        {
            return Button(button ?? lines[0], lines[0], true);
        }

        if (lines.Length == 1 && LabelledLink().Match(lines[0]) is { Success: true } labelled)
        {
            return Button(labelled.Groups["label"].Value, labelled.Groups["url"].Value, false);
        }

        if (lines.All(line => line.StartsWith("- ", StringComparison.Ordinal)))
        {
            var items = string.Concat(lines.Select(line => $"<li style=\"margin:0 0 6px;\">{Linked(line[2..])}</li>"));
            return $"<ul style=\"margin:0 0 18px;padding-left:22px;\">{items}</ul>\n";
        }

        return $"<p style=\"margin:0 0 18px;\">{string.Join("<br>", lines.Select(Linked))}</p>\n";
    }

    private static string Button(string label, string url, bool showAddress) =>
        $"""
        <table role="presentation" cellpadding="0" cellspacing="0" border="0" style="margin:6px 0 {(showAddress ? 10 : 22)}px;"><tr>
        <td style="background:{Brand};border-radius:8px;"><a href="{Attribute(url)}" style="display:inline-block;padding:12px 22px;font-family:{Sans};font-size:15px;font-weight:600;color:{OnBrand};text-decoration:none;border-radius:8px;">{Text(label)}</a></td>
        </tr></table>
        {(showAddress ? $"<p style=\"margin:0 0 22px;font-size:13px;line-height:1.5;color:{Muted};word-break:break-all;\"><a href=\"{Attribute(url)}\" style=\"color:{Muted};\">{Text(url)}</a></p>" : "")}

        """;

    // The text made safe, with any web address in it a link.
    private static string Linked(string text)
    {
        var html = new StringBuilder();
        var at = 0;
        foreach (Match link in Address().Matches(text))
        {
            html.Append(Text(text[at..link.Index]));
            html.Append($"<a href=\"{Attribute(link.Value)}\" style=\"color:{Brand};\">{Text(link.Value)}</a>");
            at = link.Index + link.Length;
        }

        return html.Append(Text(text[at..])).ToString();
    }

    private static string Flatten(string block) => string.Join(' ', block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimStart('-', ' ')));

    private static string Clip(string text, int most) => text.Length <= most ? text : text[..(most - 1)] + "…";

    private static string Text(string text) => WebUtility.HtmlEncode(text);

    private static string Attribute(string text) => WebUtility.HtmlEncode(text);

    [GeneratedRegex(@"^https?://\S+$")]
    private static partial Regex LinkOnly();

    // "See it on Shelf: https://…", with a space or a French non-breaking space before the colon or not.
    [GeneratedRegex(@"^(?<label>[^:]+?)[\s ]*:\s*(?<url>https?://\S+)$")]
    private static partial Regex LabelledLink();

    [GeneratedRegex(@"https?://[^\s<>""]+[^\s<>"".,;:!?)\]]")]
    private static partial Regex Address();
}
