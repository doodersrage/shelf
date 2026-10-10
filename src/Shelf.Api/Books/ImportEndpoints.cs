using Microsoft.AspNetCore.Http.HttpResults;
using Shelf.Api.Data;

namespace Shelf.Api.Books;

public static class ImportEndpoints
{
    // POST /books/import: any number of e-books, audio files, and zips of tracks, each made into a book.
    // A form gets sent back to what it made; asked for JSON, it gets what happened to each file.
    public static async Task<IResult> Import(HttpContext http, ShelfDb db, BookImport import, CancellationToken cancellationToken)
    {
        if (!http.Request.HasFormContentType)
        {
            return TypedResults.BadRequest();
        }

        var form = await http.Request.ReadFormAsync(cancellationToken);
        var keepBoth = bool.TryParse(form["keepBoth"], out var keep) && keep || form["keepBoth"] == "on";
        var files = form.Files.Where(file => file.Length > 0).Select(file => new ImportFile(file.FileName, file.OpenReadStream(), file.Length)).ToList();
        List<ImportedFile> results;
        try
        {
            results = await import.ImportAsync(db, files, keepBoth, cancellationToken);
        }
        finally
        {
            foreach (var file in files)
            {
                await file.Content.DisposeAsync();
            }
        }

        if (http.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            return TypedResults.Ok(results);
        }

        var made = results.Where(result => result.Outcome is ImportOutcome.Added or ImportOutcome.AddedToExisting).ToList();
        if (made.Count == 1 && results.Count == 1)
        {
            return TypedResults.Redirect($"/library/{made[0].BookId}?imported=1");
        }

        var added = results.Count(result => result.Outcome == ImportOutcome.Added);
        var joined = results.Count(result => result.Outcome == ImportOutcome.AddedToExisting);
        var skipped = results.Count(result => result.Outcome == ImportOutcome.AlreadyOnShelf);
        var failed = results.Count(result => result.Outcome is ImportOutcome.Unsupported or ImportOutcome.TooLarge);
        var kindle = results.Count(result => result.Outcome == ImportOutcome.NeedsConverter);
        return TypedResults.Redirect($"/?imported={added}&joined={joined}&skipped={skipped}&failed={failed}" + (kindle > 0 ? $"&kindle={kindle}" : ""));
    }
}
