namespace Shelf.Api.Readers;

public sealed class Reader
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public string PasswordHash { get; set; } = "";
    public string? KeyHash { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record ReaderResponse(int Id, string Name);

public sealed record LendRequest(int ReaderId, DateOnly? DueOn = null);

public sealed record BorrowedBook(
    int Id,
    string Title,
    string Author,
    string? CoverUrl,
    string Owner,
    DateOnly? LoanedOn,
    DateOnly? DueOn,
    bool HasEbook = false,
    bool HasAudio = false);
