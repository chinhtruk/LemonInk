using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Email;
using ZenRead.ViewModels;

namespace ZenRead.Services.Books;

public class BookService : IBookService
{
    private const string DefaultCoverUrl = "/images/book-cover-thinking-fast-and-slow.svg";
    private const string CoverAssetVersion = "20260513-cover-fix-2";

    private readonly ApplicationDbContext _dbContext;
    private readonly IEmailNotificationService _emailNotifications;
    private readonly ILogger<BookService> _logger;

    public BookService(
        ApplicationDbContext dbContext,
        IEmailNotificationService emailNotifications,
        ILogger<BookService> logger)
    {
        _dbContext = dbContext;
        _emailNotifications = emailNotifications;
        _logger = logger;
    }

    public async Task<LibraryViewModel> GetHomeLibraryAsync(string? userId = null)
    {
        var books = await GetPublicReadyBooks()
            .OrderByDescending(book => book.PublishedAt ?? book.CreatedAt)
            .ToListAsync();
        var weekStart = DateTime.UtcNow.AddDays(-7);
        var weeklyReads = await _dbContext.ReadingProgressEntries
            .AsNoTracking()
            .Where(progress => progress.UpdatedAt >= weekStart)
            .GroupBy(progress => progress.BookId)
            .Select(group => new { BookId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.BookId, item => item.Count);
        var weeklyBookmarks = await _dbContext.UserBookmarks
            .AsNoTracking()
            .Where(bookmark => bookmark.CreatedAt >= weekStart)
            .GroupBy(bookmark => bookmark.BookId)
            .Select(group => new { BookId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.BookId, item => item.Count);
        var featuredBooks = books
            .OrderByDescending(book =>
                weeklyReads.GetValueOrDefault(book.Id) +
                weeklyBookmarks.GetValueOrDefault(book.Id))
            .ThenByDescending(book => book.PublishedAt ?? book.CreatedAt)
            .Take(3)
            .ToList();
        var recentBooks = books
            .Take(6)
            .ToList();

        var continueReading = string.IsNullOrWhiteSpace(userId)
            ? new List<HomeContinueReadingItem>()
            : await GetContinueReadingAsync(userId);

        return new LibraryViewModel
        {
            Categories = BuildCategories(books),
            FeaturedBooks = featuredBooks.Select(ToBookCard).ToList(),
            RecentBooks = recentBooks.Select(ToBookCard).ToList(),
            ContinueReading = continueReading
        };
    }

    public async Task<List<BookCard>> GetPublicLibraryAsync()
    {
        var books = await GetPublicReadyBooks()
            .OrderBy(book => book.Id)
            .ToListAsync();

        return books.Select(ToBookCard).ToList();
    }

    public async Task<BookSearchResult> SearchBooksAsync(BookSearchQuery query, string? userId = null)
    {
        var normalizedQuery = NormalizeFilter(query.Query);
        var source = NormalizeFilter(query.Source);
        var status = NormalizeFilter(query.Status);
        var category = NormalizeFilter(query.Category);
        var sort = NormalizeFilter(query.Sort);

        var baseQuery = GetVisibleLibraryBooks(userId);
        var totalCount = await baseQuery.CountAsync();

        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            baseQuery = baseQuery.Where(book =>
                EF.Functions.ILike(book.Title, $"%{normalizedQuery}%") ||
                EF.Functions.ILike(book.AuthorName, $"%{normalizedQuery}%") ||
                EF.Functions.ILike(book.Category, $"%{normalizedQuery}%") ||
                (book.Description != null && EF.Functions.ILike(book.Description, $"%{normalizedQuery}%")));
        }

        if (!string.IsNullOrWhiteSpace(source) && source != "all")
        {
            if (source is "curated" or "public")
            {
                baseQuery = baseQuery.Where(book => book.SourceType == BookSourceType.Curated);
            }
            else if (source is "upload" or "userupload" or "personal")
            {
                baseQuery = baseQuery.Where(book => book.SourceType == BookSourceType.UserUpload);
            }
        }

        if (!string.IsNullOrWhiteSpace(status) && status != "all")
        {
            baseQuery = status switch
            {
                "ready" => baseQuery.Where(book => book.ProcessingStatus == BookProcessingStatus.Ready),
                "summaryready" => baseQuery.Where(book => book.ProcessingStatus == BookProcessingStatus.SummaryReady),
                "processing" => baseQuery.Where(book =>
                    book.ProcessingStatus == BookProcessingStatus.Uploaded ||
                    book.ProcessingStatus == BookProcessingStatus.ExtractingText ||
                    book.ProcessingStatus == BookProcessingStatus.Extracted ||
                    book.ProcessingStatus == BookProcessingStatus.Summarizing ||
                    book.ProcessingStatus == BookProcessingStatus.GeneratingAudio),
                "failed" => baseQuery.Where(book => book.ProcessingStatus == BookProcessingStatus.Failed),
                _ => baseQuery
            };
        }

        if (!string.IsNullOrWhiteSpace(category) && category != "all" && category != "tat-ca")
        {
            baseQuery = baseQuery.Where(book => EF.Functions.ILike(book.Category, query.Category!.Trim()));
        }

        baseQuery = sort switch
        {
            "rating-desc" => baseQuery
                .OrderByDescending(book => book.Reviews.Any())
                .ThenByDescending(book => book.Reviews.Any() ? book.Reviews.Average(review => review.Rating) : 0)
                .ThenBy(book => book.Title),
            "time-asc" => baseQuery.OrderBy(book => book.ReadingTimeMinutes).ThenBy(book => book.Title),
            "title-asc" => baseQuery.OrderBy(book => book.Title),
            "oldest" => baseQuery.OrderBy(book => book.CreatedAt),
            _ => baseQuery.OrderByDescending(book => book.CreatedAt)
        };

        var filteredCount = await baseQuery.CountAsync();
        var books = await baseQuery
            .Take(80)
            .ToListAsync();

        var categories = await GetVisibleLibraryBooks(userId)
            .Select(book => book.Category)
            .Where(item => item != string.Empty)
            .Distinct()
            .OrderBy(item => item)
            .ToListAsync();

        return new BookSearchResult
        {
            Books = books.Select(ToBookCard).ToList(),
            Categories = new[] { "Tất cả" }.Concat(categories).ToList(),
            TotalCount = totalCount,
            FilteredCount = filteredCount
        };
    }

    public async Task<BookSummary?> GetBookSummaryAsync(int id, string? userId = null)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .AsSplitQuery()
            .Include(book => book.Takeaways)
            .Include(book => book.SummarySections)
            .Include(book => book.GeneratedSummaries)
            .Include(book => book.Audios)
            .Include(book => book.Reviews)
            .FirstOrDefaultAsync(book =>
                book.Id == id &&
                (
                    (book.Visibility == BookVisibility.Public &&
                     (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                      book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                      book.ProcessingStatus == BookProcessingStatus.Ready) &&
                     book.IsSummaryReady) ||
                    (book.SourceType == BookSourceType.UserUpload &&
                     book.OwnerUserId == userId &&
                     book.IsSummaryReady &&
                     (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                      book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                      book.ProcessingStatus == BookProcessingStatus.Ready))
                ));

        if (book is null)
        {
            return null;
        }

        var audio = book.Audios
            .Where(item => item.Status == AudioStatus.Ready)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        var audioDurationSeconds = audio?.DurationSeconds ?? book.AudioDurationSeconds;
        var hasRating = book.Reviews.Count > 0;
        var reviewAverage = hasRating
            ? book.Reviews.Average(review => NormalizeReviewRating(review.Rating))
            : 0;

        var overviewSection = book.SummarySections
            .Where(section => section.SectionType == SummarySectionType.Overview)
            .OrderBy(section => section.SortOrder)
            .FirstOrDefault();
        var generatedSummary = book.GeneratedSummaries
            .OrderByDescending(summary => summary.GeneratedAt)
            .FirstOrDefault();

        var chapters = book.SummarySections
            .Where(section => section.SectionType == SummarySectionType.Chapter)
            .OrderBy(section => section.SortOrder)
            .Select(section => new Chapter
            {
                Id = section.Id,
                Number = section.ChapterNumber ?? section.SortOrder,
                Title = section.Title,
                Content = section.ContentHtml,
                ReadingTimeMinutes = section.ReadingTimeMinutes
            })
            .ToList();

        var audioSyncedChapterMinutes = ReadingTimeCalculator.EstimateChapterMinutesFromAudio(chapters, audioDurationSeconds);
        for (var index = 0; index < chapters.Count; index++)
        {
            chapters[index].ReadingTimeMinutes = audioSyncedChapterMinutes[index];
        }

        return new BookSummary
        {
            Id = book.Id,
            Title = book.Title,
            Author = book.AuthorName,
            CoverUrl = ResolveCoverUrl(book.CoverUrl),
            Category = book.Category,
            Rating = hasRating ? Math.Round((decimal)reviewAverage, 1) : 0,
            HasRating = hasRating,
            ReadingTimeMinutes = ReadingTimeCalculator.GetDisplayMinutes(book.ReadingTimeMinutes, audioDurationSeconds),
            IsAudioReady = book.IsAudioReady && audio is not null,
            AudioUrl = ResolveAudioUrl(audio),
            AudioDurationSeconds = audioDurationSeconds,
            AudioVoiceName = audio?.VoiceName,
            Introduction = ResolveDisplayIntroduction(book),
            Overview = overviewSection?.ContentHtml ?? generatedSummary?.LongSummary,
            KeyTakeaways = book.Takeaways
                .OrderBy(takeaway => takeaway.SortOrder)
                .Select(takeaway => takeaway.Content)
                .ToList(),
            Chapters = chapters
        };
    }

    public async Task<BookDetailViewModel?> GetBookDetailAsync(int id, string? userId = null)
    {
        var book = await _dbContext.Books
            .AsNoTracking()
            .AsSplitQuery()
            .Include(book => book.Takeaways)
            .Include(book => book.SummarySections)
            .Include(book => book.Audios)
            .Include(book => book.Reviews)
                .ThenInclude(review => review.User)
            .FirstOrDefaultAsync(book =>
                book.Id == id &&
                (
                    (book.Visibility == BookVisibility.Public &&
                     book.IsSummaryReady &&
                     (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                      book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                      book.ProcessingStatus == BookProcessingStatus.Ready)) ||
                    (!string.IsNullOrWhiteSpace(userId) &&
                     book.SourceType == BookSourceType.UserUpload &&
                     book.OwnerUserId == userId &&
                     book.IsSummaryReady)
                ));

        if (book is null)
        {
            return null;
        }

        var reviews = book.Reviews
            .OrderByDescending(review => review.UpdatedAt)
            .ToList();
        var audio = book.Audios
            .Where(item => item.Status == AudioStatus.Ready)
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
        var audioDurationSeconds = audio?.DurationSeconds ?? book.AudioDurationSeconds;
        var hasRating = reviews.Count > 0;
        var reviewAverage = reviews.Count > 0
            ? reviews.Average(review => NormalizeReviewRating(review.Rating))
            : 0;
        var userReview = string.IsNullOrWhiteSpace(userId)
            ? null
            : reviews.FirstOrDefault(review => review.UserId == userId);

        return new BookDetailViewModel
        {
            Id = book.Id,
            Title = book.Title,
            Author = book.AuthorName,
            Category = book.Category,
            CoverUrl = ResolveCoverUrl(book.CoverUrl),
            CoverGradient = book.CoverGradient,
            Introduction = ResolveDisplayIntroduction(book),
            Description = book.Description,
            Rating = hasRating ? Math.Round((decimal)reviewAverage, 1) : 0,
            HasRating = hasRating,
            ReviewCount = reviews.Count,
            ReadingTimeMinutes = ReadingTimeCalculator.GetDisplayMinutes(book.ReadingTimeMinutes, audioDurationSeconds),
            AudioDurationSeconds = audioDurationSeconds,
            ChaptersCount = book.SummarySections.Count(section => section.SectionType == SummarySectionType.Chapter),
            IsAudioReady = book.IsAudioReady && audio is not null,
            CanRead = book.IsSummaryReady &&
                (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                 book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                 book.ProcessingStatus == BookProcessingStatus.Ready),
            CanReview = !string.IsNullOrWhiteSpace(userId),
            UserReview = userReview is null ? null : ToReviewItem(userReview, userId),
            KeyTakeaways = book.Takeaways
                .OrderBy(takeaway => takeaway.SortOrder)
                .Take(4)
                .Select(takeaway => takeaway.Content)
                .ToList(),
            Reviews = reviews
                .Take(6)
                .Select(review => ToReviewItem(review, userId))
                .ToList()
        };
    }

    public async Task<bool> SaveBookReviewAsync(int bookId, string userId, int rating, string? comment)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var book = await _dbContext.Books.FirstOrDefaultAsync(book =>
            book.Id == bookId &&
            (
                (book.Visibility == BookVisibility.Public &&
                 book.IsSummaryReady &&
                 (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                  book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                  book.ProcessingStatus == BookProcessingStatus.Ready)) ||
                (book.SourceType == BookSourceType.UserUpload &&
                 book.OwnerUserId == userId &&
                 book.IsSummaryReady)
            ));

        if (book is null)
        {
            return false;
        }

        var normalizedRating = Math.Clamp(rating, 1, 5);
        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (normalizedComment?.Length > 1200)
        {
            normalizedComment = normalizedComment[..1200];
        }

        var existingReview = await _dbContext.BookReviews
            .FirstOrDefaultAsync(review => review.BookId == bookId && review.UserId == userId);
        var now = DateTime.UtcNow;

        if (existingReview is null)
        {
            _dbContext.BookReviews.Add(new BookReview
            {
                BookId = bookId,
                UserId = userId,
                Rating = normalizedRating,
                Comment = normalizedComment,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        else
        {
            existingReview.Rating = normalizedRating;
            existingReview.Comment = normalizedComment;
            existingReview.UpdatedAt = now;
        }

        await _dbContext.SaveChangesAsync();

        await RecalculateBookRatingAsync(bookId, now);

        return true;
    }

    public async Task<bool> DeleteBookReviewAsync(int bookId, string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return false;
        }

        var review = await _dbContext.BookReviews
            .FirstOrDefaultAsync(review => review.BookId == bookId && review.UserId == userId);
        if (review is null)
        {
            return false;
        }

        _dbContext.BookReviews.Remove(review);
        await _dbContext.SaveChangesAsync();
        await RecalculateBookRatingAsync(bookId, DateTime.UtcNow);

        return true;
    }

    public async Task<bool> ReplyToBookReviewAsync(int bookId, int reviewId, string userId, string? content)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalizedContent = content.Trim();
        if (normalizedContent.Length > 1200)
        {
            normalizedContent = normalizedContent[..1200];
        }

        var review = await _dbContext.BookReviews
            .Include(item => item.User)
            .Include(item => item.Book)
            .FirstOrDefaultAsync(item =>
                item.Id == reviewId &&
                item.BookId == bookId &&
                (
                    (item.Book.Visibility == BookVisibility.Public &&
                     item.Book.IsSummaryReady &&
                     (item.Book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                      item.Book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                      item.Book.ProcessingStatus == BookProcessingStatus.Ready)) ||
                    (item.Book.SourceType == BookSourceType.UserUpload &&
                     item.Book.OwnerUserId == userId &&
                     item.Book.IsSummaryReady)
                ));

        if (review is null)
        {
            return false;
        }

        _dbContext.BookReviewReplies.Add(new BookReviewReply
        {
            BookReviewId = review.Id,
            UserId = userId,
            Content = normalizedContent,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _dbContext.SaveChangesAsync();

        if (!string.Equals(review.UserId, userId, StringComparison.Ordinal))
        {
            try
            {
                await _emailNotifications.SendReviewReplyAsync(
                    review.User.Email,
                    review.User.FullName,
                    review.Book.Title,
                    normalizedContent,
                    bookId);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Could not send review reply email for review {ReviewId}.", reviewId);
            }
        }

        return true;
    }

    private async Task RecalculateBookRatingAsync(int bookId, DateTime updatedAt)
    {
        var book = await _dbContext.Books.FirstOrDefaultAsync(book => book.Id == bookId);
        if (book is null)
        {
            return;
        }

        var savedRatings = await _dbContext.BookReviews
            .Where(review => review.BookId == bookId)
            .Select(review => review.Rating)
            .ToListAsync();

        book.Rating = savedRatings.Count == 0
            ? 0
            : Math.Round((decimal)savedRatings.Select(NormalizeReviewRating).Average() * 2, 1);
        book.UpdatedAt = updatedAt;
        await _dbContext.SaveChangesAsync();
    }

    private IQueryable<Book> GetPublicReadyBooks()
    {
        return _dbContext.Books
            .AsNoTracking()
            .Include(book => book.Reviews)
            .Where(book =>
                book.Visibility == BookVisibility.Public &&
                (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                 book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                 book.ProcessingStatus == BookProcessingStatus.Ready) &&
                 book.IsSummaryReady);
    }

    private IQueryable<Book> GetVisibleLibraryBooks(string? userId)
    {
        return _dbContext.Books
            .AsNoTracking()
            .Include(book => book.Reviews)
            .Where(book =>
                (book.Visibility == BookVisibility.Public &&
                 book.IsSummaryReady &&
                 (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                  book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                  book.ProcessingStatus == BookProcessingStatus.Ready)) ||
                (!string.IsNullOrWhiteSpace(userId) &&
                 book.SourceType == BookSourceType.UserUpload &&
                 book.OwnerUserId == userId));
    }

    private async Task<List<HomeContinueReadingItem>> GetContinueReadingAsync(string userId)
    {
        var progressEntries = await _dbContext.ReadingProgressEntries
            .AsNoTracking()
            .Include(progress => progress.Book)
            .ThenInclude(book => book.Reviews)
            .Include(progress => progress.SummarySection)
            .Where(progress =>
                progress.UserId == userId &&
                progress.ProgressPercent > 0 &&
                progress.ProgressPercent < 100 &&
                progress.Book.IsSummaryReady &&
                (progress.Book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                 progress.Book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                 progress.Book.ProcessingStatus == BookProcessingStatus.Ready) &&
                (progress.Book.Visibility == BookVisibility.Public ||
                 (progress.Book.SourceType == BookSourceType.UserUpload &&
                  progress.Book.OwnerUserId == userId)))
            .OrderByDescending(progress => progress.UpdatedAt)
            .Take(2)
            .ToListAsync();

        return progressEntries.Select(ToContinueReadingItem).ToList();
    }

    private static List<string> BuildCategories(IEnumerable<Book> books)
    {
        return new[] { "Tất cả" }
            .Concat(books
                .Select(book => book.Category)
                .Where(category => !string.IsNullOrWhiteSpace(category))
                .Distinct()
                .OrderBy(category => category))
            .ToList();
    }

    private static BookCard ToBookCard(Book book)
    {
        var rating = ResolveReviewRating(book);

        return new BookCard
        {
            Id = book.Id,
            Title = book.Title,
            Author = book.AuthorName,
            Category = book.Category,
            Rating = rating.Value,
            HasRating = rating.HasRating,
            ReadingTimeMinutes = ReadingTimeCalculator.GetDisplayMinutes(book.ReadingTimeMinutes, book.AudioDurationSeconds),
            CoverUrl = ResolveCoverUrl(book.CoverUrl),
            CoverGradient = book.CoverGradient,
            CoverSvg = book.CoverSvg,
            Source = book.SourceType.ToString(),
            Status = book.ProcessingStatus.ToString(),
            Visibility = book.Visibility.ToString(),
            IsAudioReady = book.IsAudioReady,
            CanRead = book.IsSummaryReady &&
                (book.ProcessingStatus == BookProcessingStatus.SummaryReady ||
                 book.ProcessingStatus == BookProcessingStatus.GeneratingAudio ||
                 book.ProcessingStatus == BookProcessingStatus.Ready)
        };
    }

    private static (decimal Value, bool HasRating) ResolveReviewRating(Book book)
    {
        if (book.Reviews.Count == 0)
        {
            return (0, false);
        }

        var average = book.Reviews
            .Select(review => NormalizeReviewRating(review.Rating))
            .Average();

        return (Math.Round((decimal)average, 1), true);
    }

    private static string NormalizeFilter(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static HomeContinueReadingItem ToContinueReadingItem(ReadingProgress progress)
    {
        var percent = Math.Clamp(progress.ProgressPercent, 0, 100);

        return new HomeContinueReadingItem
        {
            Book = ToBookCard(progress.Book),
            Progress = percent,
            ProgressLabel = BuildProgressLabel(progress.SummarySection, percent),
            LastOpened = BuildLastOpenedLabel(progress.UpdatedAt)
        };
    }

    private static BookReviewItem ToReviewItem(BookReview review, string? currentUserId = null)
    {
        return new BookReviewItem
        {
            Id = review.Id,
            UserName = string.IsNullOrWhiteSpace(review.User.FullName)
                ? review.User.Email ?? "Bạn đọc LemonInk"
                : review.User.FullName,
            Rating = NormalizeReviewRating(review.Rating),
            Comment = review.Comment,
            CreatedAt = review.UpdatedAt,
            CanManage = !string.IsNullOrWhiteSpace(currentUserId) && review.UserId == currentUserId
        };
    }

    private static int NormalizeReviewRating(int rating)
    {
        return rating > 5
            ? Math.Clamp((int)Math.Round(rating / 2d, MidpointRounding.AwayFromZero), 1, 5)
            : Math.Clamp(rating, 1, 5);
    }

    private static string BuildProgressLabel(BookSummarySection? section, int percent)
    {
        if (section is null)
        {
            return $"{percent}% đã đọc";
        }

        if (section.ChapterNumber is { } chapterNumber)
        {
            return $"Chương {chapterNumber} · {percent}%";
        }

        return $"{section.Title} · {percent}%";
    }

    private static string BuildLastOpenedLabel(DateTime updatedAt)
    {
        var elapsed = DateTime.UtcNow - updatedAt;

        if (elapsed.TotalMinutes < 1)
        {
            return "Vừa mở";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes)} phút trước";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours)} giờ trước";
        }

        if (elapsed.TotalDays < 7)
        {
            return $"{Math.Max(1, (int)elapsed.TotalDays)} ngày trước";
        }

        return updatedAt.ToLocalTime().ToString("dd/MM/yyyy");
    }

    private static string ResolveDisplayIntroduction(Book book)
    {
        if (book.SourceType != BookSourceType.UserUpload)
        {
            return book.Introduction;
        }

        var normalized = string.Join(
            " ",
            (book.Introduction ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var sentences = Regex.Matches(normalized, @"[^.!?]+[.!?]?")
            .Select(match => match.Value.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Take(3)
            .ToList();
        var introduction = sentences.Count == 0
            ? normalized
            : string.Join(" ", sentences);
        var words = introduction.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length <= 55
            ? introduction
            : $"{string.Join(" ", words.Take(55)).TrimEnd('.', ',', ';', ':')}...";
    }

    private static string ResolveCoverUrl(string? coverUrl)
    {
        var resolvedUrl = string.IsNullOrWhiteSpace(coverUrl) ? DefaultCoverUrl : coverUrl.Trim();

        if (resolvedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            resolvedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            resolvedUrl.Contains('?'))
        {
            return resolvedUrl;
        }

        return $"{resolvedUrl}?v={CoverAssetVersion}";
    }

    private static string? ResolveAudioUrl(BookAudio? audio)
    {
        if (audio is null || string.IsNullOrWhiteSpace(audio.AudioUrl))
        {
            return null;
        }

        if (audio.AudioUrl.StartsWith("App_Data", StringComparison.OrdinalIgnoreCase))
        {
            return $"/Books/Audio/{audio.Id}";
        }

        return audio.AudioUrl;
    }
}
