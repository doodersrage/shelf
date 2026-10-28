using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Shelf.Api.Data;
using Shelf.Api.Readers;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

public enum RuleTrigger
{
    Added,
    Started,
    Finished,
    Reread,
    Lent,
    Returned,
    Overdue,
}

public enum RuleActionKind
{
    AddTag,
    RemoveTag,
    AddToCollection,
    RemoveFromCollection,
    Queue,
    Unqueue,
    Love,
    SetPlace,
    Notify,
}

// What a book must be for a rule to act on it; what is left empty asks nothing.
public sealed record RuleConditions(string? Tag = null, string? Author = null, BookFormat? Format = null, int? MinRating = null, string? Series = null,
    bool? Loved = null, string? Place = null);

public sealed record RuleAction(RuleActionKind Kind, string? Value = null);

// A reader's own rule: when something happens to one of their books, and the book is as the rule asks, do these.
public sealed class AutomationRule
{
    public const int MaxNameLength = 80;
    public const int MaxPerReader = 50;
    public const int MaxActions = 8;

    public int Id { get; set; }
    public int OwnerId { get; set; }
    public required string Name { get; set; }
    public RuleTrigger Trigger { get; set; }
    public string Conditions { get; set; } = "{}";
    public string Actions { get; set; } = "[]";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

// One time a rule acted, or would have in a dry run: on which book, and what it did.
public sealed class AutomationRun
{
    public const int MaxOutcomeLength = 500;
    public const int Keep = 200;

    public long Id { get; set; }
    public int RuleId { get; set; }
    public int OwnerId { get; set; }
    public int? BookId { get; set; }
    public DateTimeOffset At { get; set; }
    public bool DryRun { get; set; }
    public required string Outcome { get; set; }
    public int? BatchId { get; set; }
}

public sealed record RuleRequest([Required, MaxLength(AutomationRule.MaxNameLength)] string Name, RuleTrigger Trigger, RuleConditions? Conditions, RuleAction[]? Actions, bool Enabled = true);

public sealed record RuleView(int Id, string Name, RuleTrigger Trigger, RuleConditions Conditions, RuleAction[] Actions, bool Enabled, string Summary, int Runs, DateTimeOffset? LastRun);

public sealed record RunView(long Id, int RuleId, string Rule, int? BookId, string? Title, DateTimeOffset At, bool DryRun, string Outcome, int? BatchId);

public sealed record DryRunResult(int Matched, RunView[] Would);

// Rules a reader writes for their own books. A rule acts in the background after the change that set it off is
// saved; what it changes can be undone like any change of many books. What rules do never sets off rules, so two
// can't keep answering each other.
public static class Automations
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly AsyncLocal<bool> Acting = new();

    // Whether the changes being saved are a rule's.
    public static bool Running => Acting.Value;

    public static void Map(RouteGroupBuilder books)
    {
        var rules = books.MapGroup("/rules").WithTags("Rules");
        rules.MapGet("/", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await ListAsync(db, cancellationToken)));
        rules.MapPost("/", async Task<IResult> (RuleRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await SaveAsync(db, null, request, cancellationToken) switch
            {
                (int id, null) => TypedResults.Created($"/books/rules/{id}", new { id }),
                (_, var problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Rule"] = [problem!] }),
            });
        rules.MapPut("/{id:int}", async Task<IResult> (int id, RuleRequest request, ShelfDb db, CancellationToken cancellationToken) =>
            await SaveAsync(db, id, request, cancellationToken) switch
            {
                (null, null) => TypedResults.NotFound(),
                (_, null) => TypedResults.NoContent(),
                (_, var problem) => TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["Rule"] = [problem!] }),
            });
        rules.MapDelete("/{id:int}", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await db.AutomationRules.Where(rule => rule.Id == id).ExecuteDeleteAsync(cancellationToken) > 0 ? TypedResults.NoContent() : TypedResults.NotFound());
        rules.MapPost("/{id:int}/try", async Task<IResult> (int id, ShelfDb db, CancellationToken cancellationToken) =>
            await DryRunAsync(db, id, cancellationToken) is { } result ? TypedResults.Ok(result) : TypedResults.NotFound());
        rules.MapGet("/runs", async (ShelfDb db, CancellationToken cancellationToken) => TypedResults.Ok(await RunsAsync(db, cancellationToken: cancellationToken)));
    }

    public static RuleTrigger? TriggerOf(string kind) => kind switch
    {
        EventKinds.BookAdded => RuleTrigger.Added,
        EventKinds.BookStarted => RuleTrigger.Started,
        EventKinds.BookFinished => RuleTrigger.Finished,
        EventKinds.BookReread => RuleTrigger.Reread,
        EventKinds.BookLent => RuleTrigger.Lent,
        EventKinds.BookReturned => RuleTrigger.Returned,
        _ => null,
    };

    public static string TriggerLabel(RuleTrigger trigger) => trigger switch
    {
        RuleTrigger.Added => T("When a book is added"),
        RuleTrigger.Started => T("When I start a book"),
        RuleTrigger.Finished => T("When I finish a book"),
        RuleTrigger.Reread => T("When I read a book again"),
        RuleTrigger.Lent => T("When I lend a book"),
        RuleTrigger.Returned => T("When a book I lent comes back"),
        _ => T("When a book I lent is overdue"),
    };

    public static string ActionLabel(RuleAction action) => action.Kind switch
    {
        RuleActionKind.AddTag => T("tag it “{0}”", action.Value ?? ""),
        RuleActionKind.RemoveTag => T("take off the tag “{0}”", action.Value ?? ""),
        RuleActionKind.AddToCollection => T("put it in the collection “{0}”", action.Value ?? ""),
        RuleActionKind.RemoveFromCollection => T("take it out of the collection “{0}”", action.Value ?? ""),
        RuleActionKind.Queue => T("put it in my queue"),
        RuleActionKind.Unqueue => T("take it out of my queue"),
        RuleActionKind.Love => T("mark it loved"),
        RuleActionKind.SetPlace => T("note it at {0}", action.Value ?? ""),
        _ => T("tell me: “{0}”", action.Value ?? ""),
    };

    public static string ConditionsLabel(RuleConditions conditions)
    {
        var parts = new List<string>();
        if (conditions.Tag is { } tag)
        {
            parts.Add(T("tagged “{0}”", tag));
        }

        if (conditions.Author is { } author)
        {
            parts.Add(T("by {0}", author));
        }

        if (conditions.Series is { } series)
        {
            parts.Add(T("in the series {0}", series));
        }

        if (conditions.Format is { } format)
        {
            parts.Add(BookRules.FormatLabel(format));
        }

        if (conditions.MinRating is int stars)
        {
            parts.Add(T("rated {0} or more", stars));
        }

        if (conditions.Loved == true)
        {
            parts.Add(T("loved"));
        }

        if (conditions.Place is { } place)
        {
            parts.Add(T("at {0}", place));
        }

        return string.Join(", ", parts);
    }

    public static string Summary(RuleTrigger trigger, RuleConditions conditions, IEnumerable<RuleAction> actions)
    {
        var when = ConditionsLabel(conditions);
        var what = string.Join(", ", actions.Select(ActionLabel));
        return when.Length == 0 ? T("{0}: {1}.", TriggerLabel(trigger), what) : T("{0} that is {1}: {2}.", TriggerLabel(trigger), when, what);
    }

    public static RuleConditions ConditionsOf(AutomationRule rule) => Read<RuleConditions>(rule.Conditions) ?? new RuleConditions();

    public static RuleAction[] ActionsOf(AutomationRule rule) => Read<RuleAction[]>(rule.Actions) ?? [];

    private static T? Read<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static async Task<RuleView[]> ListAsync(ShelfDb db, CancellationToken cancellationToken = default)
    {
        var rules = await db.AutomationRules.AsNoTracking().OrderBy(rule => rule.Id).ToListAsync(cancellationToken);
        var ids = rules.Select(rule => rule.Id).ToList();
        var runs = (await db.AutomationRuns.AsNoTracking().Where(run => ids.Contains(run.RuleId) && !run.DryRun).Select(run => new { run.RuleId, run.At }).ToListAsync(cancellationToken))
            .GroupBy(run => run.RuleId).Select(group => new { group.Key, Count = group.Count(), Last = group.Max(run => run.At) })
            .ToDictionary(item => item.Key);
        return [.. rules.Select(rule =>
        {
            var (conditions, actions) = (ConditionsOf(rule), ActionsOf(rule));
            runs.TryGetValue(rule.Id, out var ran);
            return new RuleView(rule.Id, rule.Name, rule.Trigger, conditions, actions, rule.Enabled, Summary(rule.Trigger, conditions, actions), ran?.Count ?? 0, ran?.Last);
        })];
    }

    public static async Task<(int? Id, string? Problem)> SaveAsync(ShelfDb db, int? id, RuleRequest request, CancellationToken cancellationToken = default)
    {
        AutomationRule? rule = null;
        if (id is int existing)
        {
            rule = await db.AutomationRules.FirstOrDefaultAsync(item => item.Id == existing, cancellationToken);
            if (rule is null)
            {
                return (null, null);
            }
        }
        else if (await db.AutomationRules.CountAsync(cancellationToken) >= AutomationRule.MaxPerReader)
        {
            return (null, T("You have {0} rules, the most there can be. Remove one first.", AutomationRule.MaxPerReader));
        }

        if (await Family.IsChildAsync(db, cancellationToken: cancellationToken))
        {
            return (null, T("A child's account cannot do that. Ask the grown-up who made it."));
        }

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > AutomationRule.MaxNameLength)
        {
            return (null, T("Give the rule a name of up to {0} characters.", AutomationRule.MaxNameLength));
        }

        if (!Enum.IsDefined(request.Trigger))
        {
            return (null, T("Choose when the rule acts."));
        }

        var actions = (request.Actions ?? []).Where(action => Enum.IsDefined(action.Kind))
            .Select(action => action with { Value = string.IsNullOrWhiteSpace(action.Value) ? null : action.Value.Trim() }).ToArray();
        if (actions.Length is 0 or > AutomationRule.MaxActions)
        {
            return (null, T("Give the rule between 1 and {0} things to do.", AutomationRule.MaxActions));
        }

        foreach (var action in actions)
        {
            var needsValue = action.Kind is not (RuleActionKind.Queue or RuleActionKind.Unqueue or RuleActionKind.Love);
            if (needsValue && (action.Value is null || action.Value.Length > 200))
            {
                return (null, T("Say what to {0}.", ActionLabel(action with { Value = "…" })));
            }

            if (action.Kind is RuleActionKind.AddTag or RuleActionKind.RemoveTag && BookRules.CanonicalTag(action.Value) is null)
            {
                return (null, T("“{0}” can't be a tag.", action.Value ?? ""));
            }
        }

        var given = request.Conditions ?? new RuleConditions();
        var conditions = new RuleConditions(
            BookRules.CanonicalTag(given.Tag), Blank(given.Author), given.Format is { } format && Enum.IsDefined(format) ? format : null,
            given.MinRating is >= 1 and <= 5 ? given.MinRating : null, Blank(given.Series), given.Loved == true ? true : null, Blank(given.Place));
        rule ??= db.AutomationRules.Add(new AutomationRule { OwnerId = db.ReaderId, Name = name, CreatedAt = DateTimeOffset.UtcNow }).Entity;
        rule.Name = name;
        rule.Trigger = request.Trigger;
        rule.Conditions = JsonSerializer.Serialize(conditions, Json);
        rule.Actions = JsonSerializer.Serialize(actions, Json);
        rule.Enabled = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        return (rule.Id, null);
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(200, text.Trim().Length)];

    public static bool Matches(RuleConditions conditions, Book book) =>
        (conditions.Tag is null || book.Tags.Any(tag => tag.Name.Equals(conditions.Tag, StringComparison.OrdinalIgnoreCase)))
        && (conditions.Author is null || Tidy.AuthorKey(book.Author).Contains(Tidy.AuthorKey(conditions.Author), StringComparison.Ordinal))
        && (conditions.Series is null || string.Equals(book.Series, conditions.Series, StringComparison.OrdinalIgnoreCase))
        && (conditions.Format is null || book.Format == conditions.Format)
        && (conditions.MinRating is null || book.Rating >= conditions.MinRating)
        && (conditions.Loved is null || book.Loved == conditions.Loved)
        && (conditions.Place is null || (book.Location is { } at && (at.Equals(conditions.Place, StringComparison.OrdinalIgnoreCase) || at.StartsWith(conditions.Place + PlaceTree.Separator, StringComparison.OrdinalIgnoreCase))));

    // A rule set off for one book: each thing it does, done (or, in a dry run, only said), and the run kept.
    public static async Task<AutomationRun?> ActAsync(ShelfDb db, AutomationRule rule, int bookId, bool dryRun, Notices? notices = null, CancellationToken cancellationToken = default)
    {
        var book = await db.Books.Include(item => item.Tags).FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);
        if (book is null || !Matches(ConditionsOf(rule), book))
        {
            return null;
        }

        var done = new List<string>();
        int? batchId = null;
        var previous = Acting.Value;
        Acting.Value = true;
        try
        {
            ChangeBatch? batch = null;
            if (!dryRun)
            {
                batch = await History.StartAsync(db, BatchKind.Edit, T("Rule: {0}", rule.Name), cancellationToken);
                batchId = batch.Id;
            }

            using var _ = History.Use(batchId);
            foreach (var action in ActionsOf(rule))
            {
                if (await DoAsync(db, book, action, dryRun, cancellationToken) is { } said)
                {
                    done.Add(said);
                }
            }

            if (!dryRun)
            {
                await db.SaveChangesAsync(cancellationToken);
                await BookRules.RemoveUnusedTagsAsync(db, cancellationToken);
                foreach (var message in ActionsOf(rule).Where(action => action.Kind == RuleActionKind.Notify && action.Value is not null))
                {
                    if (notices is not null)
                    {
                        await notices.SendAsync(rule.OwnerId, null, _ => new Notice(rule.Name, $"{message.Value} · {book.Title}", $"/library/{book.Id}", $"rule-{rule.Id}"), cancellationToken);
                    }
                }
            }
        }
        finally
        {
            Acting.Value = previous;
        }

        var outcome = done.Count == 0 ? T("Nothing to change.") : string.Join("; ", done);
        var run = new AutomationRun
        {
            RuleId = rule.Id,
            OwnerId = rule.OwnerId,
            BookId = book.Id,
            At = DateTimeOffset.UtcNow,
            DryRun = dryRun,
            Outcome = outcome.Length > AutomationRun.MaxOutcomeLength ? outcome[..AutomationRun.MaxOutcomeLength] : outcome,
            BatchId = batchId,
        };
        if (!dryRun)
        {
            db.AutomationRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            await TrimAsync(db, rule.OwnerId, cancellationToken);
        }

        return run;
    }

    private static async Task<string?> DoAsync(ShelfDb db, Book book, RuleAction action, bool dryRun, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case RuleActionKind.AddTag when BookRules.CanonicalTag(action.Value) is { } name:
                if (book.Tags.Any(tag => tag.Name == name) || book.Tags.Count >= BookRules.MaxTags)
                {
                    return null;
                }

                if (!dryRun)
                {
                    await BookRules.SyncTagsAsync(db, book, [.. book.Tags.Select(tag => tag.Name), name], cancellationToken);
                }

                return ActionLabel(action);
            case RuleActionKind.RemoveTag when BookRules.CanonicalTag(action.Value) is { } name:
                if (book.Tags.All(tag => tag.Name != name))
                {
                    return null;
                }

                if (!dryRun)
                {
                    book.Tags.RemoveAll(tag => tag.Name == name);
                }

                return ActionLabel(action);
            case RuleActionKind.AddToCollection when action.Value is { } listName:
                var collection = await db.Collections.FirstOrDefaultAsync(item => item.Name == listName, cancellationToken);
                if (collection is not null && await db.CollectionBooks.AnyAsync(item => item.CollectionId == collection.Id && item.BookId == book.Id, cancellationToken))
                {
                    return null;
                }

                if (!dryRun)
                {
                    collection ??= (await Collections.CreateAsync(db, listName, null, cancellationToken)).Made;
                    if (collection is null || await Collections.AddAsync(db, collection.Id, book.Id, cancellationToken) != Collections.AddResult.Added)
                    {
                        return null;
                    }
                }

                return ActionLabel(action);
            case RuleActionKind.RemoveFromCollection when action.Value is { } listName:
                var holding = await db.Collections.FirstOrDefaultAsync(item => item.Name == listName, cancellationToken);
                if (holding is null || !await db.CollectionBooks.AnyAsync(item => item.CollectionId == holding.Id && item.BookId == book.Id, cancellationToken))
                {
                    return null;
                }

                if (!dryRun)
                {
                    await Collections.RemoveAsync(db, holding.Id, book.Id, cancellationToken);
                }

                return ActionLabel(action);
            case RuleActionKind.Queue:
                if (book.Queued || book.Status != BookStatus.Want)
                {
                    return null;
                }

                if (!dryRun)
                {
                    book.Queued = true;
                }

                return ActionLabel(action);
            case RuleActionKind.Unqueue:
                if (!book.Queued)
                {
                    return null;
                }

                if (!dryRun)
                {
                    (book.Queued, book.QueueOrder, book.HeldUntil, book.HoldNote) = (false, null, null, null);
                }

                return ActionLabel(action);
            case RuleActionKind.Love:
                if (book.Loved)
                {
                    return null;
                }

                if (!dryRun)
                {
                    book.Loved = true;
                }

                return ActionLabel(action);
            case RuleActionKind.SetPlace when action.Value is { } place:
                var path = place.Length > 80 ? place[..80] : place;
                if (book.Location == path)
                {
                    return null;
                }

                if (!dryRun)
                {
                    PlaceTree.Put(book, path, (await PlaceTree.TemporaryPathsAsync(db, cancellationToken)).Contains(path), await PlaceTree.TemporaryPathsAsync(db, cancellationToken));
                }

                return ActionLabel(action);
            case RuleActionKind.Notify when action.Value is not null:
                return ActionLabel(action);
            default:
                return null;
        }
    }

    // What the rule would do now to every book it would act on, for books as they are; nothing changes.
    public static async Task<DryRunResult?> DryRunAsync(ShelfDb db, int ruleId, CancellationToken cancellationToken = default)
    {
        var rule = await db.AutomationRules.AsNoTracking().FirstOrDefaultAsync(item => item.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return null;
        }

        var conditions = ConditionsOf(rule);
        var query = db.Books.AsNoTracking();
        query = rule.Trigger switch
        {
            RuleTrigger.Finished or RuleTrigger.Reread => query.Where(book => book.Status == BookStatus.Finished),
            RuleTrigger.Started => query.Where(book => book.Status == BookStatus.Reading),
            RuleTrigger.Lent => query.Where(book => book.LoanedTo != null),
            RuleTrigger.Overdue => query.Where(book => book.LoanedTo != null && book.DueOn < DateOnly.FromDateTime(DateTime.UtcNow)),
            _ => query,
        };
        if (conditions.Tag is { } tag)
        {
            query = query.Where(book => book.Tags.Any(item => item.Name == tag));
        }

        var ids = await query.OrderByDescending(book => book.Id).Select(book => book.Id).Take(2000).ToListAsync(cancellationToken);
        var would = new List<RunView>();
        var matched = 0;
        foreach (var id in ids)
        {
            if (await ActAsync(db, rule, id, dryRun: true, cancellationToken: cancellationToken) is { } run)
            {
                matched++;
                if (would.Count < 50)
                {
                    var title = await db.Books.AsNoTracking().Where(book => book.Id == id).Select(book => book.Title).FirstAsync(cancellationToken);
                    would.Add(new RunView(0, rule.Id, rule.Name, id, title, run.At, true, run.Outcome, null));
                }
            }
        }

        return new DryRunResult(matched, [.. would]);
    }

    public static async Task<RunView[]> RunsAsync(ShelfDb db, int take = 100, CancellationToken cancellationToken = default)
    {
        var me = db.ReaderId;
        return [.. await db.AutomationRuns.AsNoTracking().Where(run => run.OwnerId == me).OrderByDescending(run => run.Id).Take(take)
            .Select(run => new RunView(run.Id, run.RuleId, db.AutomationRules.Where(rule => rule.Id == run.RuleId).Select(rule => rule.Name).FirstOrDefault() ?? "",
                run.BookId, db.Books.Where(book => book.Id == run.BookId).Select(book => book.Title).FirstOrDefault(), run.At, run.DryRun, run.Outcome, run.BatchId))
            .ToListAsync(cancellationToken)];
    }

    private static async Task TrimAsync(ShelfDb db, int ownerId, CancellationToken cancellationToken)
    {
        var cutoff = await db.AutomationRuns.Where(run => run.OwnerId == ownerId).OrderByDescending(run => run.Id).Skip(AutomationRun.Keep)
            .Select(run => (long?)run.Id).FirstOrDefaultAsync(cancellationToken);
        if (cutoff is long id)
        {
            await db.AutomationRuns.Where(run => run.OwnerId == ownerId && run.Id <= id).ExecuteDeleteAsync(cancellationToken);
        }
    }
}

// Rules set off by what happens to books, and once an hour by loans gone overdue.
public sealed class AutomationRunner(IServiceScopeFactory scopes, Notices notices, ILogger<AutomationRunner> logger) : BackgroundService, IShelfEventHandler
{
    public async Task HandleAsync(ShelfEvent shelfEvent, CancellationToken cancellationToken)
    {
        if (shelfEvent.ByRule || shelfEvent.BookId is not int bookId || Automations.TriggerOf(shelfEvent.Kind) is not { } trigger)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var owner = await lookup.Books.IgnoreQueryFilters().AsNoTracking().Where(book => book.Id == bookId).Select(book => book.OwnerId).FirstOrDefaultAsync(cancellationToken);
        if (owner is not int ownerId)
        {
            return;
        }

        await RunAsync(ownerId, trigger, [bookId], cancellationToken);
    }

    public async Task<int> RunAsync(int ownerId, RuleTrigger trigger, IReadOnlyCollection<int> bookIds, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(ownerId);
        var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
        var rules = await db.AutomationRules.AsNoTracking().Where(rule => rule.Enabled && rule.Trigger == trigger).OrderBy(rule => rule.Id).ToListAsync(cancellationToken);
        var ran = 0;
        foreach (var rule in rules)
        {
            foreach (var bookId in bookIds)
            {
                if (await Automations.ActAsync(db, rule, bookId, dryRun: false, notices, cancellationToken) is not null)
                {
                    ran++;
                }
            }
        }

        return ran;
    }

    // Each loan overdue sets off a reader's overdue rules once.
    public async Task<int> OverdueAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        List<int> owners;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            owners = await db.AutomationRules.IgnoreQueryFilters().Where(rule => rule.Enabled && rule.Trigger == RuleTrigger.Overdue).Select(rule => rule.OwnerId).Distinct().ToListAsync(cancellationToken);
        }

        var ran = 0;
        foreach (var owner in owners)
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ShelfReader>().Use(owner);
            var db = scope.ServiceProvider.GetRequiredService<ShelfDb>();
            var overdue = await db.Books.AsNoTracking().Where(book => book.LoanedTo != null && book.DueOn < today)
                .Select(book => new { book.Id, Since = book.LoanedOn }).ToListAsync(cancellationToken);
            var rules = await db.AutomationRules.AsNoTracking().Where(rule => rule.Enabled && rule.Trigger == RuleTrigger.Overdue).ToListAsync(cancellationToken);
            foreach (var rule in rules)
            {
                foreach (var loan in overdue)
                {
                    var since = loan.Since is DateOnly lent ? new DateTimeOffset(lent.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : DateTimeOffset.MinValue;
                    // Moments can't be compared in SQLite, so the runs are looked at here.
                    if ((await db.AutomationRuns.AsNoTracking().Where(run => run.RuleId == rule.Id && run.BookId == loan.Id).Select(run => run.At).ToListAsync(cancellationToken)).Any(at => at >= since))
                    {
                        continue;
                    }

                    if (await Automations.ActAsync(db, rule, loan.Id, dryRun: false, notices, cancellationToken) is not null)
                    {
                        ran++;
                    }
                }
            }
        }

        return ran;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await OverdueAsync(stoppingToken);
                Heartbeats.Beat(nameof(AutomationRunner));
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Could not run rules for overdue loans; they will try again in an hour.");
                Heartbeats.Beat(nameof(AutomationRunner), ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
