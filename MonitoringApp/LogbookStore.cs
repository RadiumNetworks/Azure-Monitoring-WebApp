using Microsoft.EntityFrameworkCore;

namespace MonitoringApp;

/// <summary>
/// Reads logbook entries and appends new comments with server-generated audit data.
/// </summary>
public sealed class LogbookStore(IDbContextFactory<AlertDbContext> dbContextFactory)
{
    public async Task<IReadOnlyList<LogbookEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await context.LogbookEntries
            .AsNoTracking()
            .OrderByDescending(entry => entry.CreatedAt)
            .ThenByDescending(entry => entry.Id)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<LogbookEntry> AddAsync(
        string user,
        string comment,
        CancellationToken cancellationToken = default)
    {
        var normalizedUser = user.Trim();
        var normalizedComment = comment.Trim();

        if (string.IsNullOrWhiteSpace(normalizedUser))
        {
            throw new InvalidOperationException("A logged-on user is required to write a logbook entry.");
        }

        if (normalizedUser.Length > 256)
        {
            throw new InvalidOperationException("The username may contain at most 256 characters.");
        }

        if (string.IsNullOrWhiteSpace(normalizedComment))
        {
            throw new InvalidOperationException("Enter a comment before saving.");
        }

        if (normalizedComment.Length > 4000)
        {
            throw new InvalidOperationException("The comment may contain at most 4000 characters.");
        }

        var entry = new LogbookEntry
        {
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            User = normalizedUser,
            Comment = normalizedComment
        };

        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        context.LogbookEntries.Add(entry);
        await context.SaveChangesAsync(cancellationToken);
        return entry;
    }
}
