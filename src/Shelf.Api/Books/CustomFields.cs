using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum FieldKind
{
    Text,
    Number,
    Date,
    YesNo,
}

// A field a reader adds to their own books, as Calibre's custom columns: "Shelf in the attic", "Bought from", "Lent
// copies". Each reader's are their own, and no one else sees them or the values in them.
public sealed class CustomField
{
    public const int MaxNameLength = 60;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }
    public FieldKind Kind { get; set; }
    public int Position { get; set; }
}

// What one book holds in one field, written one way whatever the reader's language: a number as 1234.5, a date as
// 2026-10-29, yes or no as "yes" and "no".
public sealed class CustomValue
{
    public const int MaxLength = 500;

    public int Id { get; set; }
    public int BookId { get; set; }
    public Book? Book { get; set; }
    public int FieldId { get; set; }
    public CustomField? Field { get; set; }
    public required string Value { get; set; }
}

public sealed record CustomFieldResponse(int Id, string Name, FieldKind Kind);

// On a change: Kind, when given and different, changes the field's kind; ClearUnfit says to clear the values that
// don't fit the new kind, which otherwise stops the change and says how many there are.
public sealed record CustomFieldRequest(string? Name, FieldKind? Kind = null, bool ClearUnfit = false);

public sealed record CustomValueResponse(int FieldId, string Name, FieldKind Kind, string? Value);

public sealed record CustomValuesRequest(Dictionary<int, string?> Values);

public static class CustomFields
{
    public const int MaxFields = 50;

    public static void Map(RouteGroupBuilder books)
    {
        books.MapGet("/fields", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken))).WithTags("Fields");
        books.MapPost("/fields", Create).WithTags("Fields");
        books.MapPut("/fields/{id:int}", Rename).WithTags("Fields");
        books.MapDelete("/fields/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await RemoveAsync(db, id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound()).WithTags("Fields");
        books.MapGet("/{id:int}/fields", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await ForBookAsync(db, id, cancellationToken) is { } values ? TypedResults.Ok(values) : TypedResults.NotFound()).WithTags("Fields");
        books.MapPut("/{id:int}/fields", SetForBook).WithTags("Fields");
    }

    public static async Task<CustomFieldResponse[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default) =>
        [.. (await db.CustomFields.AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(field => field.Position).ThenBy(field => field.Id)
            .Select(field => new CustomFieldResponse(field.Id, field.Name, field.Kind))];

    // A new field, named as no other of the reader's is, in any case.
    public static async Task<(CustomFieldResponse? Field, string? Problem)> CreateAsync(ShelfDb db, string? name, FieldKind kind, CancellationToken cancellationToken = default)
    {
        var fields = await db.CustomFields.ToListAsync(cancellationToken);
        if (Name(name, fields, null) is { Problem: { } problem })
        {
            return (null, problem);
        }

        if (fields.Count >= MaxFields)
        {
            return (null, T("A reader can have up to {0} fields.", MaxFields));
        }

        if (!Enum.IsDefined(kind))
        {
            return (null, T("Choose text, a number, a date, or yes or no."));
        }

        var field = new CustomField { OwnerId = db.ReaderId, Name = name!.Trim(), Kind = kind, Position = fields.Count == 0 ? 0 : fields.Max(item => item.Position) + 1 };
        db.CustomFields.Add(field);
        await db.SaveChangesAsync(cancellationToken);
        return (new CustomFieldResponse(field.Id, field.Name, field.Kind), null);
    }

    // A field renamed; its kind stays, as the values in it were written for that kind.
    public static async Task<(bool Found, string? Problem)> RenameAsync(ShelfDb db, int id, string? name, CancellationToken cancellationToken = default)
    {
        var fields = await db.CustomFields.ToListAsync(cancellationToken);
        if (fields.FirstOrDefault(item => item.Id == id) is not { } field)
        {
            return (false, null);
        }

        if (Name(name, fields, id) is { Problem: { } problem })
        {
            return (true, problem);
        }

        field.Name = name!.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    // A field's kind changed, each value rewritten for it: a number kept as text, text that is a date made a date. A
    // value that doesn't fit is never dropped unasked: the change stops and says how many, unless clearUnfit.
    public static async Task<(bool Found, int Unfit, string? Problem)> ChangeKindAsync(ShelfDb db, int id, FieldKind kind, bool clearUnfit, CancellationToken cancellationToken = default)
    {
        if (await db.CustomFields.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } field)
        {
            return (false, 0, null);
        }

        if (field.Kind == kind)
        {
            return (true, 0, null);
        }

        var values = await db.CustomValues.Where(value => value.FieldId == id).ToListAsync(cancellationToken);
        var rewritten = values.Select(value => (Row: value, New: Normalize(kind, field.Kind == FieldKind.YesNo && kind == FieldKind.Text ? Show(field.Kind, value.Value) : value.Value).Value)).ToList();
        var unfit = rewritten.Count(item => item.New is null);
        if (unfit > 0 && !clearUnfit)
        {
            return (true, unfit, unfit == 1
                ? T("1 book holds something in {0} that isn't a {1}. Change it anyway to clear it, or keep the field as it is.", field.Name, KindLabel(kind).ToLowerInvariant())
                : T("{0} books hold something in {1} that isn't a {2}. Change it anyway to clear them, or keep the field as it is.", unfit, field.Name, KindLabel(kind).ToLowerInvariant()));
        }

        foreach (var (row, value) in rewritten)
        {
            if (value is null)
            {
                db.CustomValues.Remove(row);
            }
            else
            {
                row.Value = value;
            }
        }

        field.Kind = kind;
        await db.SaveChangesAsync(cancellationToken);
        return (true, unfit, null);
    }

    // A field and every value in it.
    public static async Task<bool> RemoveAsync(ShelfDb db, int id, CancellationToken cancellationToken = default)
    {
        if (await db.CustomFields.FirstOrDefaultAsync(item => item.Id == id, cancellationToken) is not { } field)
        {
            return false;
        }

        await db.CustomValues.IgnoreQueryFilters().Where(value => value.FieldId == id).ExecuteDeleteAsync(cancellationToken);
        db.CustomFields.Remove(field);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    // Every field of the reader's, with what this book holds in each; null when the book isn't theirs.
    public static async Task<CustomValueResponse[]?> ForBookAsync(ShelfDb db, int bookId, CancellationToken cancellationToken = default)
    {
        if (!await db.Books.AnyAsync(book => book.Id == bookId, cancellationToken))
        {
            return null;
        }

        var values = await db.CustomValues.AsNoTracking().Where(value => value.BookId == bookId).ToDictionaryAsync(value => value.FieldId, value => value.Value, cancellationToken);
        return [.. (await ListAsync(db, cancellationToken)).Select(field => new CustomValueResponse(field.Id, field.Name, field.Kind, values.GetValueOrDefault(field.Id)))];
    }

    // Values for a book, each checked against its field's kind first: all are kept, or none. A blank one clears it.
    // Fields not named are left as they are. Problems come back keyed by the field's name.
    public static async Task<Dictionary<string, string[]>?> SetForBookAsync(ShelfDb db, int bookId, IReadOnlyDictionary<int, string?> given, CancellationToken cancellationToken = default)
    {
        var fields = (await db.CustomFields.AsNoTracking().ToListAsync(cancellationToken)).ToDictionary(field => field.Id);
        var problems = new Dictionary<string, string[]>();
        var clean = new Dictionary<int, string?>();
        foreach (var (fieldId, text) in given)
        {
            if (!fields.TryGetValue(fieldId, out var field))
            {
                problems[fieldId.ToString(CultureInfo.InvariantCulture)] = [T("That field is not one of yours.")];
                continue;
            }

            var (value, problem) = Normalize(field.Kind, text);
            if (problem is not null)
            {
                problems[field.Name] = [problem];
            }
            else
            {
                clean[fieldId] = value;
            }
        }

        if (problems.Count > 0)
        {
            return problems;
        }

        var kept = await db.CustomValues.Where(value => value.BookId == bookId).ToListAsync(cancellationToken);
        foreach (var (fieldId, value) in clean)
        {
            var existing = kept.FirstOrDefault(item => item.FieldId == fieldId);
            if (value is null)
            {
                if (existing is not null)
                {
                    db.CustomValues.Remove(existing);
                }
            }
            else if (existing is null)
            {
                db.CustomValues.Add(new CustomValue { BookId = bookId, FieldId = fieldId, Value = value });
            }
            else
            {
                existing.Value = value;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return null;
    }

    // What a reader typed, in the one form it is kept in; a blank clears the value.
    public static (string? Value, string? Problem) Normalize(FieldKind kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var given = text.Trim();
        switch (kind)
        {
            case FieldKind.Text:
                return given.Length <= CustomValue.MaxLength ? (given, null) : (null, T("Keep it to {0} characters.", CustomValue.MaxLength));
            case FieldKind.Number:
                // 3.5 as written anywhere first, so no language reads a decimal point as a thousands mark.
                return decimal.TryParse(given, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    || decimal.TryParse(given, NumberStyles.Number, CultureInfo.CurrentCulture, out number)
                    ? (number.ToString(CultureInfo.InvariantCulture), null)
                    : (null, T("Give a number, such as 12 or 3.5."));
            case FieldKind.Date:
                return DateOnly.TryParseExact(given, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    || DateOnly.TryParse(given, CultureInfo.CurrentCulture, DateTimeStyles.None, out date)
                    ? (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), null)
                    : (null, T("Give a date, such as 2026-10-29."));
            default:
                return given.ToLowerInvariant() switch
                {
                    "yes" or "true" or "y" or "1" => ("yes", null),
                    "no" or "false" or "n" or "0" => ("no", null),
                    _ => (null, T("Choose yes or no.")),
                };
        }
    }

    // A value as the reader reads it: a number and a date in their own language's way, yes and no in words.
    public static string Show(FieldKind kind, string value) => kind switch
    {
        FieldKind.Number when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number.ToString("G", CultureInfo.CurrentCulture),
        FieldKind.Date when DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date.Medium(),
        FieldKind.YesNo => value == "yes" ? T("Yes") : T("No"),
        _ => value,
    };

    public static string KindLabel(FieldKind kind) => kind switch
    {
        FieldKind.Number => T("Number"),
        FieldKind.Date => T("Date"),
        FieldKind.YesNo => T("Yes or no"),
        _ => T("Text"),
    };

    private static (string? Name, string? Problem) Name(string? name, List<CustomField> fields, int? except)
    {
        var given = name?.Trim() ?? "";
        if (given.Length == 0 || given.Length > CustomField.MaxNameLength)
        {
            return (null, T("Give the field a name of up to {0} characters.", CustomField.MaxNameLength));
        }

        return fields.Any(field => field.Id != except && string.Equals(field.Name, given, StringComparison.OrdinalIgnoreCase))
            ? (null, T("You have a field by that name already."))
            : (given, null);
    }

    private static async Task<Results<Created<CustomFieldResponse>, ValidationProblem>> Create(CustomFieldRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var (field, problem) = await CreateAsync(db, request.Name, request.Kind ?? FieldKind.Text, cancellationToken);
        return field is not null
            ? TypedResults.Created($"/books/fields/{field.Id}", field)
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem!] });
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> Rename(int id, CustomFieldRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        var (found, problem) = await RenameAsync(db, id, request.Name, cancellationToken);
        if (found && problem is null && request.Kind is { } kind)
        {
            var (_, _, unfit) = await ChangeKindAsync(db, id, kind, request.ClearUnfit, cancellationToken);
            if (unfit is not null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Kind"] = [unfit] });
            }
        }

        return !found ? TypedResults.NotFound()
            : problem is not null ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [problem] })
            : TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetForBook(int id, CustomValuesRequest request, ShelfDb db, CancellationToken cancellationToken)
    {
        if (!await db.Books.AnyAsync(book => book.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return await SetForBookAsync(db, id, request.Values ?? [], cancellationToken) is { } problems
            ? TypedResults.ValidationProblem(problems)
            : TypedResults.NoContent();
    }
}
