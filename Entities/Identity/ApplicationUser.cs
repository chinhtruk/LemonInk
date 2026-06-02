using Microsoft.AspNetCore.Identity;

namespace ZenRead.Entities;

public class ApplicationUser : IdentityUser
{
    public string FullName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public string? PendingEmail { get; set; }

    public DateTime? PendingEmailRequestedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public string PreferredReadingFontSize { get; set; } = "medium";

    public string PreferredLineHeight { get; set; } = "comfortable";

    public decimal PreferredAudioSpeed { get; set; } = 1.0m;

    public BookVisibility DefaultBookVisibility { get; set; } = BookVisibility.Private;

    public List<Book> UploadedBooks { get; set; } = new();

    public List<UserBookmark> Bookmarks { get; set; } = new();

    public List<ReadingProgress> ReadingProgressEntries { get; set; } = new();

    public List<UserNote> Notes { get; set; } = new();

    public List<ChatSession> ChatSessions { get; set; } = new();
}
