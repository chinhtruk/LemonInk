using Microsoft.EntityFrameworkCore;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.ViewModels;

namespace ZenRead.Services.UserLibrary;

public class BookmarkService : IBookmarkService
{
    private const string DefaultCoverGradient = "linear-gradient(135deg, #f6d365 0%, #fda085 100%)";
    private const string DefaultCoverSvg = """<svg viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19.5A2.5 2.5 0 0 1 6.5 17H20"/><path d="M6.5 2H20v20H6.5A2.5 2.5 0 0 1 4 19.5v-15A2.5 2.5 0 0 1 6.5 2z"/></svg>""";

    private readonly ApplicationDbContext _dbContext;

    public BookmarkService(ApplicationDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<BookmarkPageViewModel> GetBookmarkPageAsync(string userId, CancellationToken cancellationToken = default)
    {
        var bookmarks = await _dbContext.UserBookmarks
            .AsNoTracking()
            .Include(bookmark => bookmark.Book)
            .ThenInclude(book => book.Audios)
            .Include(bookmark => bookmark.Book)
            .ThenInclude(book => book.Takeaways)
            .Where(bookmark => bookmark.UserId == userId)
            .OrderByDescending(bookmark => bookmark.CreatedAt)
            .ToListAsync(cancellationToken);

        var bookIds = bookmarks.Select(bookmark => bookmark.BookId).ToList();
        var progressByBook = await _dbContext.ReadingProgressEntries
            .AsNoTracking()
            .Include(progress => progress.SummarySection)
            .Where(progress => progress.UserId == userId && bookIds.Contains(progress.BookId))
            .ToDictionaryAsync(progress => progress.BookId, cancellationToken);

        var notesByBook = await _dbContext.UserNotes
            .AsNoTracking()
            .Where(note => note.UserId == userId && bookIds.Contains(note.BookId))
            .GroupBy(note => note.BookId)
            .Select(group => new
            {
                BookId = group.Key,
                Count = group.Count(),
                Snippet = group.OrderByDescending(note => note.UpdatedAt).Select(note => note.Content).FirstOrDefault()
            })
            .ToDictionaryAsync(item => item.BookId, cancellationToken);

        var items = bookmarks
            .Select((bookmark, index) =>
            {
                progressByBook.TryGetValue(bookmark.BookId, out var progress);
                notesByBook.TryGetValue(bookmark.BookId, out var noteSummary);

                return ToBookmarkItem(bookmark, progress, noteSummary?.Count ?? 0, noteSummary?.Snippet, index + 1);
            })
            .ToList();

        var continueReading = items
            .Where(item => item.ProgressPercent > 0 && item.ProgressPercent < 100)
            .OrderByDescending(item => item.ProgressPercent)
            .ThenBy(item => item.OrderIndex)
            .ToList();

        var continueIds = continueReading.Select(item => item.Id).ToHashSet();
        var savedBooks = items
            .Where(item => !continueIds.Contains(item.Id))
            .OrderBy(item => item.OrderIndex)
            .ToList();

        return new BookmarkPageViewModel
        {
            TotalSavedBooks = items.Count,
            ContinueReadingCount = continueReading.Count,
            AudioReadyCount = items.Count(item => item.HasAudio),
            NotesCount = items.Count(item => item.HasNotes),
            Filters = new List<string> { "Tất cả", "Đang đọc", "Đã đọc xong", "Có audio", "Có ghi chú" },
            ContinueReading = continueReading,
            SavedBooks = savedBooks
        };
    }

    public async Task<BookmarkToggleResult> ToggleAsync(string userId, int bookId, CancellationToken cancellationToken = default)
    {
        var book = await _dbContext.Books
            .FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new BookmarkToggleResult { NotFound = true, Message = "Không tìm thấy sách." };
        }

        if (!CanRead(book, userId))
        {
            return new BookmarkToggleResult { Forbidden = true, Message = "Bạn không có quyền lưu sách này." };
        }

        var existing = await _dbContext.UserBookmarks
            .FirstOrDefaultAsync(item => item.UserId == userId && item.BookId == bookId, cancellationToken);

        if (existing is null)
        {
            _dbContext.UserBookmarks.Add(new UserBookmark
            {
                UserId = userId,
                BookId = bookId,
                CreatedAt = DateTime.UtcNow
            });

            await _dbContext.SaveChangesAsync(cancellationToken);
            return new BookmarkToggleResult
            {
                Succeeded = true,
                IsBookmarked = true,
                Message = "Đã lưu sách vào danh sách đánh dấu."
            };
        }

        _dbContext.UserBookmarks.Remove(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new BookmarkToggleResult
        {
            Succeeded = true,
            IsBookmarked = false,
            Message = "Đã bỏ sách khỏi danh sách đánh dấu."
        };
    }

    public async Task<BookmarkStatusResult> GetStatusAsync(string userId, int bookId, CancellationToken cancellationToken = default)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == bookId, cancellationToken);

        if (book is null)
        {
            return new BookmarkStatusResult { NotFound = true };
        }

        if (!CanRead(book, userId))
        {
            return new BookmarkStatusResult { Forbidden = true };
        }

        var isBookmarked = await _dbContext.UserBookmarks
            .AsNoTracking()
            .AnyAsync(item => item.UserId == userId && item.BookId == bookId, cancellationToken);

        return new BookmarkStatusResult
        {
            IsBookmarked = isBookmarked
        };
    }

    private static BookmarkItem ToBookmarkItem(
        UserBookmark bookmark,
        ReadingProgress? progress,
        int notesCount,
        string? noteSnippet,
        int orderIndex)
    {
        var book = bookmark.Book;
        var progressPercent = Math.Clamp(progress?.ProgressPercent ?? 0, 0, 100);
        var hasAudio = book.IsAudioReady && book.Audios.Any(audio => audio.Status == AudioStatus.Ready);
        var hasNotes = notesCount > 0;
        var statusLabel = ResolveStatusLabel(progressPercent);

        return new BookmarkItem
        {
            Id = book.Id,
            Title = book.Title,
            Author = book.AuthorName,
            Category = book.Category,
            Rating = book.Rating ?? 0,
            ReadingTimeMinutes = book.ReadingTimeMinutes,
            CoverGradient = string.IsNullOrWhiteSpace(book.CoverGradient) ? DefaultCoverGradient : book.CoverGradient,
            CoverSvg = string.IsNullOrWhiteSpace(book.CoverSvg) ? DefaultCoverSvg : book.CoverSvg,
            CoverUrl = book.CoverUrl ?? string.Empty,
            ProgressPercent = progressPercent,
            StatusLabel = statusLabel,
            ProgressLabel = ResolveProgressLabel(progress, progressPercent),
            LastOpenedLabel = ResolveLastOpenedLabel(progress?.UpdatedAt ?? bookmark.CreatedAt),
            HasAudio = hasAudio,
            HasNotes = hasNotes,
            NoteSnippet = ResolveNoteSnippet(noteSnippet, book),
            IsContinueReading = progressPercent > 0 && progressPercent < 100,
            OrderIndex = orderIndex
        };
    }

    private static string ResolveStatusLabel(int progressPercent)
    {
        if (progressPercent >= 100)
        {
            return "Đã đọc xong";
        }

        return progressPercent > 0 ? "Đang đọc" : "Chưa mở";
    }

    private static string ResolveProgressLabel(ReadingProgress? progress, int progressPercent)
    {
        if (progressPercent >= 100)
        {
            return "Đã đọc xong";
        }

        if (progress?.SummarySection is not null)
        {
            var chapter = progress.SummarySection.ChapterNumber.HasValue
                ? $"Chương {progress.SummarySection.ChapterNumber}"
                : progress.SummarySection.Title;

            return $"{chapter} · {progressPercent}%";
        }

        return progressPercent > 0 ? $"{progressPercent}% đã đọc" : "Lưu để đọc sau";
    }

    private static string ResolveLastOpenedLabel(DateTime value)
    {
        var elapsed = DateTime.UtcNow - value;
        if (elapsed.TotalMinutes < 3)
        {
            return "Vừa mở";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} phút trước";
        }

        if (elapsed.TotalHours < 24)
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)} giờ trước";
        }

        return $"{Math.Max(1, (int)elapsed.TotalDays)} ngày trước";
    }

    private static string ResolveNoteSnippet(string? noteSnippet, Book book)
    {
        if (!string.IsNullOrWhiteSpace(noteSnippet))
        {
            return TrimToLength(noteSnippet, 120);
        }

        var takeaway = book.Takeaways
            .OrderBy(item => item.SortOrder)
            .Select(item => item.Content)
            .FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));

        if (!string.IsNullOrWhiteSpace(takeaway))
        {
            return TrimToLength(takeaway, 120);
        }

        return TrimToLength(book.Description ?? book.Introduction, 120);
    }

    private static string TrimToLength(string? value, int maxLength)
    {
        var clean = (value ?? string.Empty).Trim();
        return clean.Length <= maxLength ? clean : $"{clean[..maxLength].Trim()}...";
    }

    private static bool CanRead(Book book, string userId)
    {
        return book.Visibility == BookVisibility.Public ||
            (book.SourceType == BookSourceType.UserUpload && book.OwnerUserId == userId);
    }
}
