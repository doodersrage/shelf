using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;

namespace Shelf.Api;

// The regions whose way of writing dates and numbers a reader can choose. The words on the pages stay English.
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
        options.DefaultRequestCulture = new RequestCulture(Fallback);
        options.SupportedCultures = cultures;
        options.SupportedUICultures = cultures;

        // The reader's own choice first, then the browser's languages, then US English.
        options.RequestCultureProviders =
        [
            new CustomRequestCultureProvider(async http =>
            {
                if (ShelfReader.IdOf(http.User) is not int readerId)
                {
                    return null;
                }

                var db = http.RequestServices.GetRequiredService<ShelfDb>();
                var chosen = await db.Readers.AsNoTracking().Where(reader => reader.Id == readerId).Select(reader => reader.Culture).FirstOrDefaultAsync(http.RequestAborted);
                return chosen is null ? null : new ProviderCultureResult(chosen);
            }),
            new AcceptLanguageHeaderRequestCultureProvider(),
        ];
    }
}
