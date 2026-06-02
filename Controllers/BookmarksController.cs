using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenRead.Services.UserLibrary;

namespace ZenRead.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class BookmarksController : ControllerBase
{
    private readonly IBookmarkService _bookmarkService;

    public BookmarksController(IBookmarkService bookmarkService)
    {
        _bookmarkService = bookmarkService;
    }

    [HttpPost("Toggle/{bookId:int}")]
    public async Task<IActionResult> Toggle(int bookId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _bookmarkService.ToggleAsync(userId, bookId, cancellationToken);
        if (result.NotFound)
        {
            return NotFound(new { message = result.Message });
        }

        if (result.Forbidden)
        {
            return Forbid();
        }

        return Ok(new
        {
            isBookmarked = result.IsBookmarked,
            message = result.Message
        });
    }

    [HttpGet("Status/{bookId:int}")]
    public async Task<IActionResult> Status(int bookId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _bookmarkService.GetStatusAsync(userId, bookId, cancellationToken);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Forbidden)
        {
            return Forbid();
        }

        return Ok(new { isBookmarked = result.IsBookmarked });
    }
}
