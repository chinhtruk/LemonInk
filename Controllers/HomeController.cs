using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using ZenRead.Services.Books;
using ZenRead.Services.UserLibrary;
using ZenRead.ViewModels;

namespace ZenRead.Controllers;

public class HomeController : Controller
{
    private readonly IBookService _bookService;
    private readonly IBookmarkService _bookmarkService;

    public HomeController(
        IBookService bookService,
        IBookmarkService bookmarkService)
    {
        _bookService = bookService;
        _bookmarkService = bookmarkService;
    }

    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var viewModel = await _bookService.GetHomeLibraryAsync(userId);
        viewModel.Greeting = GetGreeting();

        return View(viewModel);
    }

    public async Task<IActionResult> Read(int id = 1)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var book = await _bookService.GetBookSummaryAsync(id, userId);

        if (book is null)
        {
            return NotFound();
        }

        return View(book);
    }

    [HttpGet]
    [Route("Books/ReadAudioStatus/{id:int}")]
    public async Task<IActionResult> ReadAudioStatus(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var book = await _bookService.GetBookSummaryAsync(id, userId);

        if (book is null)
        {
            return NotFound();
        }

        return Json(new
        {
            book.Id,
            book.Title,
            book.Author,
            book.CoverUrl,
            book.IsAudioReady,
            book.AudioUrl,
            book.AudioDurationSeconds,
            durationText = book.AudioDurationSeconds is > 0
                ? FormatDuration(book.AudioDurationSeconds.Value)
                : null,
            readingTimeText = book.IsAudioReady && book.AudioDurationSeconds is > 0
                ? $"{FormatDuration(book.AudioDurationSeconds.Value)} audio"
                : $"{book.ReadingTimeMinutes} phút đọc"
        });
    }

    [HttpGet]
    [Route("Books/Detail/{id:int}")]
    public async Task<IActionResult> BookDetail(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var book = await _bookService.GetBookDetailAsync(id, userId);

        if (book is null)
        {
            return NotFound();
        }

        return View(book);
    }

    [Authorize]
    [HttpPost]
    [Route("Books/Review/{id:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReviewBook(int id, int rating, string? comment)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var saved = await _bookService.SaveBookReviewAsync(id, userId, rating, comment);
        TempData[saved ? "UploadSuccess" : "UploadError"] = saved
            ? "Đã lưu đánh giá của bạn."
            : "Không thể lưu đánh giá cho cuốn sách này.";

        return RedirectToAction(nameof(BookDetail), new { id });
    }

    [Authorize]
    [HttpPost]
    [Route("Books/Review/{id:int}/Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteReview(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var deleted = await _bookService.DeleteBookReviewAsync(id, userId);
        TempData[deleted ? "UploadSuccess" : "UploadError"] = deleted
            ? "Đã xoá đánh giá của bạn."
            : "Không thể xoá đánh giá này.";

        return RedirectToAction(nameof(BookDetail), new { id });
    }

    [Authorize]
    [HttpPost]
    [Route("Books/Review/{id:int}/{reviewId:int}/Reply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplyToReview(int id, int reviewId, string? content)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var saved = await _bookService.ReplyToBookReviewAsync(id, reviewId, userId, content);
        TempData[saved ? "UploadSuccess" : "UploadError"] = saved
            ? "Đã gửi phản hồi đánh giá."
            : "Không thể gửi phản hồi này.";

        return RedirectToAction(nameof(BookDetail), new { id });
    }

    public async Task<IActionResult> Library([FromQuery] BookSearchQuery filters)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var books = await _bookService.SearchBooksAsync(filters, userId);
        return View(books);
    }

    [Authorize]
    public async Task<IActionResult> Bookmarks(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var viewModel = await _bookmarkService.GetBookmarkPageAsync(userId, cancellationToken);
        return View(viewModel);
    }

    public IActionResult Settings()
    {
        return RedirectToAction(nameof(Library));
    }

    public IActionResult About()
    {
        return View();
    }

    public IActionResult Faq()
    {
        return View();
    }

    public IActionResult Terms()
    {
        return View();
    }

    public IActionResult Privacy()
    {
        return View();
    }

    private static string GetGreeting()
    {
        var hour = DateTime.Now.Hour;
        if (hour < 12) return "Chào buổi sáng";
        if (hour < 18) return "Chào buổi chiều";
        return "Chào buổi tối";
    }

    private static string FormatDuration(int durationSeconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(1, durationSeconds));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"mm\:ss");
    }

}
