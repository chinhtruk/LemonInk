using Microsoft.EntityFrameworkCore;
using ZenRead.Data;
using ZenRead.Entities;

namespace ZenRead.Services.UserLibrary;

public class ReadingProgressService : IReadingProgressService
{
    private readonly ApplicationDbContext _dbContext;

    public ReadingProgressService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ReadingProgressResult> UpdateAsync(
        string userId,
        ReadingProgressUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == request.BookId, cancellationToken);

        if (book is null)
        {
            return new ReadingProgressResult { NotFound = true };
        }

        if (!CanRead(book, userId))
        {
            return new ReadingProgressResult { Forbidden = true };
        }

        var progress = await _dbContext.ReadingProgressEntries
            .FirstOrDefaultAsync(item => item.UserId == userId && item.BookId == request.BookId, cancellationToken);

        if (progress is null)
        {
            progress = new ReadingProgress
            {
                UserId = userId,
                BookId = request.BookId
            };
            _dbContext.ReadingProgressEntries.Add(progress);
        }

        progress.ProgressPercent = Math.Clamp(request.ProgressPercent, 0, 100);
        progress.SummarySectionId = await ResolveSummarySectionIdAsync(request.BookId, request.SummarySectionId, cancellationToken);
        progress.LastPosition = string.IsNullOrWhiteSpace(request.LastPosition)
            ? null
            : request.LastPosition.Trim()[..Math.Min(request.LastPosition.Trim().Length, 180)];
        progress.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return ToResult(progress);
    }

    public async Task<ReadingProgressResult> GetAsync(
        string userId,
        int bookId,
        CancellationToken cancellationToken = default)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new ReadingProgressResult { NotFound = true };
        }

        if (!CanRead(book, userId))
        {
            return new ReadingProgressResult { Forbidden = true };
        }

        var progress = await _dbContext.ReadingProgressEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.UserId == userId && item.BookId == bookId, cancellationToken);

        return progress is null
            ? new ReadingProgressResult { Succeeded = true, ProgressPercent = 0 }
            : ToResult(progress);
    }

    private async Task<int?> ResolveSummarySectionIdAsync(
        int bookId,
        int? summarySectionId,
        CancellationToken cancellationToken)
    {
        if (!summarySectionId.HasValue)
        {
            return null;
        }

        var exists = await _dbContext.BookSummarySections
            .AsNoTracking()
            .AnyAsync(item => item.Id == summarySectionId.Value && item.BookId == bookId, cancellationToken);

        return exists ? summarySectionId : null;
    }

    private static ReadingProgressResult ToResult(ReadingProgress progress)
    {
        return new ReadingProgressResult
        {
            Succeeded = true,
            ProgressPercent = progress.ProgressPercent,
            SummarySectionId = progress.SummarySectionId,
            LastPosition = progress.LastPosition,
            UpdatedAt = progress.UpdatedAt
        };
    }

    private static bool CanRead(Book book, string userId)
    {
        return book.Visibility == BookVisibility.Public ||
            (book.SourceType == BookSourceType.UserUpload && book.OwnerUserId == userId);
    }
}
