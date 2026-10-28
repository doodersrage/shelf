using System.Net;
using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Books;

// Why the last trade with a service (Readwise, Hardcover) went wrong, kept on the reader until one goes right, so
// Connections and Account can say what to do rather than leave a failure to the server's log.
public enum SyncProblem
{
    // The service turned the token down: revoked, or from another account.
    Refused,

    // It asked Shelf to slow down.
    Busy,

    // It couldn't be reached, or had trouble of its own.
    Unreachable,

    // It answered, but didn't take what was sent.
    Failed,

    // The token kept here can't be opened: the shelf's keys changed, as after a restore without the keys folder.
    Unsealed,
}

public static class SyncProblems
{
    // What an answer to a check of the token says; null when the token is taken.
    public static SyncProblem? FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => SyncProblem.Refused,
        HttpStatusCode.TooManyRequests => SyncProblem.Busy,
        >= HttpStatusCode.InternalServerError => SyncProblem.Unreachable,
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => null,
        _ => SyncProblem.Failed,
    };

    public static string Describe(SyncProblem problem, string service) => problem switch
    {
        SyncProblem.Refused => T("{0} turned the token down; it may have been revoked. Connect again with a new one.", service),
        SyncProblem.Busy => T("{0} asked Shelf to slow down. Try again a little later.", service),
        SyncProblem.Unreachable => T("{0} couldn't be reached. Try again a little later.", service),
        SyncProblem.Unsealed => T("The {0} token kept here can't be read, as the shelf's keys have changed (restored without the keys folder, say). Connect again.", service),
        _ => T("{0} didn't take the last changes. Try again a little later.", service),
    };
}
