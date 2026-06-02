namespace ZenRead.Entities;

public enum BookFileType
{
    Pdf = 1,
    Epub = 2,
    Txt = 3,
    Docx = 4,
    Other = 99
}

public enum BookFileUploadStatus
{
    Uploaded = 1,
    Extracted = 2,
    Failed = 3
}

public class BookFile
{
    public int Id { get; set; }

    public int BookId { get; set; }

    public Book Book { get; set; } = null!;

    public string OwnerUserId { get; set; } = string.Empty;

    public ApplicationUser OwnerUser { get; set; } = null!;

    public string OriginalFileName { get; set; } = string.Empty;

    public string StoredFilePath { get; set; } = string.Empty;

    public BookFileType FileType { get; set; }

    public long FileSizeBytes { get; set; }

    public string? ExtractedTextPath { get; set; }

    public BookFileUploadStatus UploadStatus { get; set; } = BookFileUploadStatus.Uploaded;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
