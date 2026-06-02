using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ZenRead.Services.UserLibrary;

namespace ZenRead.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class ReadingProgressController : ControllerBase
{
    private readonly IReadingProgressService _readingProgressService;

    public ReadingProgressController(IReadingProgressService readingProgressService)
    {
        _readingProgressService = readingProgressService;
    }

    [HttpPost("Update")]
    public async Task<IActionResult> Update(
        [FromBody] ReadingProgressUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _readingProgressService.UpdateAsync(userId, request, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("{bookId:int}")]
    public async Task<IActionResult> Get(int bookId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _readingProgressService.GetAsync(userId, bookId, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult(ReadingProgressResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Forbidden)
        {
            return Forbid();
        }

        return Ok(new
        {
            progressPercent = result.ProgressPercent,
            summarySectionId = result.SummarySectionId,
            lastPosition = result.LastPosition,
            updatedAt = result.UpdatedAt
        });
    }
}
