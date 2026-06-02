using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ZenRead.Services.Chat;

namespace ZenRead.Controllers;

[Authorize]
[ApiController]
[Route("[controller]")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chatService;

    public ChatController(IChatService chatService)
    {
        _chatService = chatService;
    }

    [HttpPost("Ask")]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> Ask([FromBody] ChatAskRequest request, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _chatService.AskAsync(userId, request.BookId, request.Message, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("History/{bookId:int}")]
    public async Task<IActionResult> History(int bookId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _chatService.GetHistoryAsync(userId, bookId, cancellationToken);
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Forbidden)
        {
            return Forbid();
        }

        if (!result.Succeeded)
        {
            return BadRequest(new { message = result.ErrorMessage ?? "Không tải được lịch sử chat." });
        }

        return Ok(new { messages = result.Messages });
    }

    [HttpPost("Clear/{bookId:int}")]
    public async Task<IActionResult> Clear(int bookId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Challenge();
        }

        var result = await _chatService.ClearAsync(userId, bookId, cancellationToken);
        return ToActionResult(result);
    }

    private IActionResult ToActionResult(ChatServiceResult result)
    {
        if (result.NotFound)
        {
            return NotFound();
        }

        if (result.Forbidden)
        {
            return Forbid();
        }

        if (!result.Succeeded)
        {
            return BadRequest(new { message = result.ErrorMessage ?? "LemonAI chưa trả lời được lúc này." });
        }

        return Ok(new
        {
            message = result.Message?.Content,
            citations = Array.Empty<ChatCitationDto>()
        });
    }
}
