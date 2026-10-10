using Shelf.Api.Books;

namespace Shelf.Api.Tests;

public sealed class CrossoverTests
{
    // An e-book with a cover, a title page, and contents before three chapters of different lengths.
    private static readonly BookPart[] Ebook =
    [
        new("Cover", 10), new("Title Page", 40), new("Contents", 120),
        new("Chapter One", 4000), new("Chapter Two", 8000), new("Chapter Three", 4000),
    ];

    [Fact]
    public void Titles_that_agree_pin_chapters_to_each_other()
    {
        // The recording's chapters are named another way, and run different lengths from the text.
        var audio = new BookPart[] { new("Opening Credits", 30), new("01 - Chapter 1", 900), new("Chapter II", 900), new("Chapter three", 1800) };
        var crossover = new Crossover(audio, Ebook);
        Assert.Equal(3, crossover.Matched);

        Assert.Equal(new PartSpot(4, 0.5), crossover.ToEbook(new PartSpot(2, 0.5)));
        Assert.Equal(new PartSpot(5, 0.25), crossover.ToEbook(new PartSpot(3, 0.25)));
        Assert.Equal(new PartSpot(2, 0.5), crossover.ToAudio(new PartSpot(4, 0.5)));
        // The front matter is before the first chapter in both.
        Assert.Equal(3, crossover.ToEbook(new PartSpot(1, 0)).Part);
    }

    [Fact]
    public void With_no_titles_to_go_by_it_goes_by_how_far_through_each_is()
    {
        var audio = new BookPart[] { new("Track 1", 1000), new("Track 2", 1000), new("Track 3", 1000), new("Track 4", 1000) };
        var crossover = new Crossover(audio, Ebook);
        Assert.Equal(0, crossover.Matched);

        // Halfway through the recording is halfway through the story, which is halfway through its long chapter.
        Assert.Equal(new PartSpot(4, 0.5), crossover.ToEbook(new PartSpot(2, 0)));
        Assert.Equal(new PartSpot(2, 0), crossover.ToAudio(new PartSpot(4, 0.5)));
    }

    [Fact]
    public void As_many_tracks_as_chapters_are_paired_in_order()
    {
        var audio = new BookPart[] { new("Track 1", 600), new("Track 2", 600), new("Track 3", 600) };
        var crossover = new Crossover(audio, Ebook);
        Assert.Equal(3, crossover.Matched);
        Assert.Equal(new PartSpot(4, 0.25), crossover.ToEbook(new PartSpot(1, 0.25)));
        Assert.Equal(new PartSpot(1, 0.25), crossover.ToAudio(new PartSpot(4, 0.25)));
    }

    [Theory]
    [InlineData("Chapter Twenty-One", "chapter 21")]
    [InlineData("03 Chapter XIV", "chapter 14")]
    [InlineData("Épilogue", "epilogue")]
    [InlineData("Part 2: The Return", "part 2 the return")]
    public void Titles_are_compared_as_plain_words_and_numbers(string title, string key) =>
        Assert.Equal(key, string.Join(' ', Crossover.Key(title)));
}
