using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api;

// The regions whose way of writing dates and numbers a reader can choose, and how a request's region and language
// are found. The words themselves are in Localization/Words.cs.
public static class Regions
{
    public static readonly string[] Supported =
    [
        "en-US", "en-GB", "en-CA", "en-AU", "en-IE", "en-NZ", "en-IN",
        "de-DE", "de-AT", "de-CH", "fr-FR", "fr-CA", "es-ES", "es-MX", "it-IT", "nl-NL", "pt-PT", "pt-BR",
        "sv-SE", "da-DK", "nb-NO", "fi-FI", "pl-PL", "cs-CZ", "ja-JP", "ko-KR", "zh-CN",
    ];

    public const string Fallback = "en-US";

    public static IReadOnlyList<(string Name, string Label)> Choices =>
        Supported.Select(name => (name, CultureInfo.GetCultureInfo(name).NativeName)).ToList();

    public static string? Clean(string? name) =>
        Supported.FirstOrDefault(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));

    public static void Configure(RequestLocalizationOptions options)
    {
        var cultures = Supported.Select(CultureInfo.GetCultureInfo).ToList();
        options.DefaultRequestCulture = new RequestCulture(Fallback, Localization.Words.English);
        options.SupportedCultures = cultures;
        options.SupportedUICultures = Localization.Words.Languages.Select(language => CultureInfo.GetCultureInfo(language.Code)).ToList();

        // Dates and numbers follow the reader's region; the words follow their language. Each is the reader's own
        // choice first, then the browser's languages, then US English.
        options.RequestCultureProviders =
        [
            new CustomRequestCultureProvider(async http =>
            {
                string? region = null;
                string? language = null;
                if (ShelfReader.IdOf(http.User) is int readerId)
                {
                    var db = http.RequestServices.GetRequiredService<ShelfDb>();
                    var chosen = await db.Readers.AsNoTracking().Where(reader => reader.Id == readerId)
                        .Select(reader => new { reader.Culture, reader.Language })
                        .FirstOrDefaultAsync(http.RequestAborted);
                    region = chosen?.Culture;
                    language = chosen?.Language;
                }

                var browser = BrowserLanguages(http);
                region ??= browser.Select(Clean).FirstOrDefault(name => name is not null)
                    ?? browser.Select(name => Supported.FirstOrDefault(item => item.StartsWith(name.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase))).FirstOrDefault(name => name is not null)
                    ?? Fallback;
                language ??= browser.Select(name => name.Split('-')[0].ToLowerInvariant()).FirstOrDefault(Localization.Words.IsSupported)
                    ?? Localization.Words.English;
                return new ProviderCultureResult(region, language);
            }),
        ];
    }

    // Accept-Language, best first.
    private static List<string> BrowserLanguages(HttpContext http) =>
        Microsoft.Net.Http.Headers.StringWithQualityHeaderValue.TryParseList(http.Request.Headers.AcceptLanguage, out var values)
            ? values.OrderByDescending(value => value.Quality ?? 1).Select(value => value.Value.Value ?? "").Where(value => value.Length > 0 && value != "*").ToList()
            : [];
}
