using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ZenRead.Data;
using ZenRead.Entities;
using ZenRead.Services.Books;
using ZenRead.Services.Uploads;
using ZenRead.ViewModels;

namespace ZenRead.Controllers;

[Authorize]
public class BooksController : Controller
{
    private readonly IBookUploadService _bookUploadService;
    private readonly IBookService _bookService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ApplicationDbContext _dbContext;
    private readonly IWebHostEnvironment _environment;
    private readonly IAntiforgery _antiforgery;

    public BooksController(
        IBookUploadService bookUploadService,
        IBookService bookService,
        UserManager<ApplicationUser> userManager,
        ApplicationDbContext dbContext,
        IWebHostEnvironment environment,
        IAntiforgery antiforgery)
    {
        _bookUploadService = bookUploadService;
        _bookService = bookService;
        _userManager = userManager;
        _dbContext = dbContext;
        _environment = environment;
        _antiforgery = antiforgery;
    }

    [HttpGet]
    public async Task<IActionResult> MyLibrary()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var viewModel = await _bookUploadService.BuildUploadFormAsync(user.Id);
        return View(viewModel);
    }

    [HttpGet]
    public IActionResult Processing()
    {
        return RedirectToAction(nameof(MyLibrary), new { status = "processing" });
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Search([FromQuery] BookSearchQuery filters)
    {
        var userId = _userManager.GetUserId(User);
        var result = await _bookService.SearchBooksAsync(filters, userId);

        return Json(new
        {
            result.TotalCount,
            result.FilteredCount,
            result.Categories,
            books = result.Books.Select(book => new
            {
                book.Id,
                book.Title,
                book.Author,
                book.Category,
                book.Rating,
                book.HasRating,
                book.ReadingTimeMinutes,
                book.CoverUrl,
                book.CoverGradient,
                book.Source,
                book.Status,
                book.Visibility,
                book.IsAudioReady,
                book.CanRead,
                readUrl = book.CanRead ? Url.Action("BookDetail", "Home", new { id = book.Id }) : null
            })
        });
    }

    [HttpGet]
    public IActionResult Upload()
    {
        return RedirectToAction(nameof(MyLibrary));
    }

    [HttpPost]
    [EnableRateLimiting("upload")]
    [RequestSizeLimit(31 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 31 * 1024 * 1024)]
    public async Task<IActionResult> Upload(BookUploadFormViewModel form)
    {
        try
        {
            await _antiforgery.ValidateRequestAsync(HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            TempData["UploadError"] = "Phiên tải lên đã hết hạn. Hãy tải lại trang rồi upload lại.";
            return RedirectToAction(nameof(MyLibrary));
        }

        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await _bookUploadService.UploadAsync(form, user.Id);
        if (!result.Succeeded)
        {
            var viewModel = await _bookUploadService.BuildUploadFormAsync(user.Id);
            viewModel.Title = form.Title;
            viewModel.AuthorName = form.AuthorName;
            viewModel.Category = form.Category;
            viewModel.Language = form.Language;
            ViewData["Error"] = result.Message;
            return View(nameof(MyLibrary), viewModel);
        }

        TempData["UploadSuccess"] = result.Message;
        return RedirectToAction(nameof(MyLibrary), new { uploadedId = result.BookId });
    }

    [HttpGet]
    [Route("Books/ProcessingStatus/{bookId:int}")]
    public async Task<IActionResult> ProcessingStatus(int bookId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var status = await _bookUploadService.GetProcessingStatusAsync(bookId, user.Id);
        if (status is null)
        {
            return NotFound();
        }

        return Json(new
        {
            status.Id,
            status.Title,
            processingStatus = status.ProcessingStatus.ToString(),
            latestJobStatus = status.LatestJobStatus?.ToString(),
            progress = status.OverallProgressPercent,
            estimatedTime = status.EstimatedTimeText,
            step = status.LatestJobStep,
            failedReason = status.FailedReason,
            canRead = status.ProcessingStatus is BookProcessingStatus.SummaryReady or BookProcessingStatus.GeneratingAudio or BookProcessingStatus.Ready,
            readUrl = Url.Action("Read", "Home", new { id = status.Id })
        });
    }

    [HttpPost]
    [Route("Books/RetryProcessing/{bookId:int}")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("upload")]
    public async Task<IActionResult> RetryProcessing(int bookId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await _bookUploadService.RetryProcessingAsync(bookId, user.Id);
        TempData[result.Succeeded ? "UploadSuccess" : "UploadError"] = result.Message;
        return RedirectToAction(nameof(MyLibrary));
    }

    [HttpPost]
    [Route("Books/ReprocessFromSource/{bookId:int}")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("upload")]
    public async Task<IActionResult> ReprocessFromSource(int bookId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await _bookUploadService.ReprocessFromSourceAsync(bookId, user.Id);
        TempData[result.Succeeded ? "UploadSuccess" : "UploadError"] = result.Message;
        return RedirectToAction(nameof(MyLibrary));
    }

    [HttpPost]
    [Route("Books/DeleteUpload/{bookId:int}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteUpload(int bookId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var result = await _bookUploadService.DeleteUploadAsync(bookId, user.Id);
        TempData[result.Succeeded ? "UploadSuccess" : "UploadError"] = result.Message;
        return RedirectToAction(nameof(MyLibrary));
    }

    [HttpGet]
    [AllowAnonymous]
    [Route("Books/Audio/{audioId:int}")]
    public async Task<IActionResult> Audio(int audioId)
    {
        var audio = await _dbContext.BookAudios
            .AsNoTracking()
            .Include(item => item.Book)
            .FirstOrDefaultAsync(item => item.Id == audioId && item.Status == AudioStatus.Ready);

        if (audio is null)
        {
            return NotFound();
        }

        if (audio.Book.Visibility != BookVisibility.Public)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user is null || audio.Book.OwnerUserId != user.Id)
            {
                return Forbid();
            }
        }

        var physicalPath = ResolveAppDataPath(audio.AudioUrl);
        if (physicalPath is null || !System.IO.File.Exists(physicalPath))
        {
            return NotFound();
        }

        return PhysicalFile(physicalPath, ResolveAudioContentType(physicalPath), enableRangeProcessing: true);
    }

    private string? ResolveAppDataPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !relativePath.StartsWith("App_Data", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var contentRoot = Path.GetFullPath(_environment.ContentRootPath);
        var physicalPath = Path.GetFullPath(Path.Combine(contentRoot, relativePath));

        return physicalPath.StartsWith(contentRoot, StringComparison.Ordinal)
            ? physicalPath
            : null;
    }

    private static string ResolveAudioContentType(string physicalPath)
    {
        return Path.GetExtension(physicalPath).ToLowerInvariant() switch
        {
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".ogg" => "audio/ogg",
            _ => "audio/mpeg"
        };
    }
}
