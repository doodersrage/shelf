using Shelf.Api.Books;
using Shelf.Api.Components.Pages;

namespace Shelf.Api.Tests;

public sealed class BookFormMergeTests
{
    private static BookFormModel Form(BookStatus status, DateOnly? finished = null, string title = "A Book", string tags = "") =>
        new() { Title = title, Author = "Someone", Status = status, FinishedOn = finished, Tags = tags };

    // A tab left open saves only what it changed: a finish made elsewhere since stays; a field changed both here and
    // there, to different values, is named; one changed to the same value on both is no clash.
    [Fact]
    public void A_stale_form_writes_only_what_it_changed()
    {
        var finished = new DateOnly(2026, 10, 1);
        var opened = Form(BookStatus.Reading);
        var now = Form(BookStatus.Finished, finished);
        var (merged, clashes) = BookFormModel.Merge(opened, Form(BookStatus.Reading, title: "A Better Title"), now);
        Assert.Empty(clashes);
        Assert.Equal(("A Better Title", BookStatus.Finished, finished), (merged.Title, merged.Status, merged.FinishedOn));

        (_, clashes) = BookFormModel.Merge(opened, Form(BookStatus.Abandoned), now);
        Assert.Equal(["Status"], clashes);

        (merged, clashes) = BookFormModel.Merge(opened, Form(BookStatus.Finished, finished), now);
        Assert.Empty(clashes);
        Assert.Equal(BookStatus.Finished, merged.Status);

        (merged, _) = BookFormModel.Merge(opened, opened, Form(BookStatus.Reading, tags: "added elsewhere"));
        Assert.Equal("added elsewhere", merged.Tags);
    }
}
