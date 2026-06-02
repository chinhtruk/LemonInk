using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ZenRead.Entities;

namespace ZenRead.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Book> Books => Set<Book>();

    public DbSet<BookFile> BookFiles => Set<BookFile>();

    public DbSet<BookContentChunk> BookContentChunks => Set<BookContentChunk>();

    public DbSet<GeneratedBookSummary> GeneratedBookSummaries => Set<GeneratedBookSummary>();

    public DbSet<BookSummarySection> BookSummarySections => Set<BookSummarySection>();

    public DbSet<BookTakeaway> BookTakeaways => Set<BookTakeaway>();

    public DbSet<BookAudio> BookAudios => Set<BookAudio>();

    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<BookProcessingQualityReport> BookProcessingQualityReports => Set<BookProcessingQualityReport>();
    public DbSet<BookSummaryPassCheckpoint> BookSummaryPassCheckpoints => Set<BookSummaryPassCheckpoint>();
    public DbSet<AiModelMonitor> AiModelMonitors => Set<AiModelMonitor>();
    public DbSet<AiModelOperationEvent> AiModelOperationEvents => Set<AiModelOperationEvent>();

    public DbSet<UserBookmark> UserBookmarks => Set<UserBookmark>();

    public DbSet<ReadingProgress> ReadingProgressEntries => Set<ReadingProgress>();

    public DbSet<UserNote> UserNotes => Set<UserNote>();

    public DbSet<BookReview> BookReviews => Set<BookReview>();

    public DbSet<BookReviewReply> BookReviewReplies => Set<BookReviewReply>();

    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    public DbSet<ChatCitation> ChatCitations => Set<ChatCitation>();

    public DbSet<AuthenticationOtpChallenge> AuthenticationOtpChallenges => Set<AuthenticationOtpChallenge>();

    public DbSet<AuthenticationAuditEvent> AuthenticationAuditEvents => Set<AuthenticationAuditEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(user => user.FullName).HasMaxLength(160);
            entity.Property(user => user.AvatarUrl).HasMaxLength(600);
            entity.Property(user => user.PendingEmail).HasMaxLength(256);
            entity.Property(user => user.PreferredReadingFontSize).HasMaxLength(32);
            entity.Property(user => user.PreferredLineHeight).HasMaxLength(32);
            entity.Property(user => user.PreferredAudioSpeed).HasPrecision(4, 2);
        });

        builder.Entity<Book>(entity =>
        {
            entity.HasIndex(book => book.Slug).IsUnique();
            entity.HasIndex(book => new { book.SourceType, book.Visibility, book.ProcessingStatus });
            entity.HasIndex(book => book.OwnerUserId);

            entity.Property(book => book.Title).HasMaxLength(260);
            entity.Property(book => book.Slug).HasMaxLength(280);
            entity.Property(book => book.AuthorName).HasMaxLength(180);
            entity.Property(book => book.CoverUrl).HasMaxLength(700);
            entity.Property(book => book.Category).HasMaxLength(120);
            entity.Property(book => book.CoverGradient).HasMaxLength(320);
            entity.Property(book => book.Language).HasMaxLength(16);
            entity.Property(book => book.Rating).HasPrecision(4, 2);

            entity.HasOne(book => book.OwnerUser)
                .WithMany(user => user.UploadedBooks)
                .HasForeignKey(book => book.OwnerUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<BookFile>(entity =>
        {
            entity.Property(file => file.OriginalFileName).HasMaxLength(260);
            entity.Property(file => file.StoredFilePath).HasMaxLength(700);
            entity.Property(file => file.ExtractedTextPath).HasMaxLength(700);

            entity.HasOne(file => file.Book)
                .WithMany(book => book.Files)
                .HasForeignKey(file => file.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(file => file.OwnerUser)
                .WithMany()
                .HasForeignKey(file => file.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookContentChunk>(entity =>
        {
            entity.HasIndex(chunk => new { chunk.BookId, chunk.ChunkIndex }).IsUnique();

            entity.HasOne(chunk => chunk.Book)
                .WithMany(book => book.ContentChunks)
                .HasForeignKey(chunk => chunk.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(chunk => chunk.SummarySection)
                .WithMany(section => section.ContentChunks)
                .HasForeignKey(chunk => chunk.SummarySectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<GeneratedBookSummary>(entity =>
        {
            entity.Property(summary => summary.GeneratedBy).HasMaxLength(120);

            entity.HasOne(summary => summary.Book)
                .WithMany(book => book.GeneratedSummaries)
                .HasForeignKey(summary => summary.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookSummarySection>(entity =>
        {
            entity.HasIndex(section => new { section.BookId, section.SortOrder });
            entity.Property(section => section.Title).HasMaxLength(260);

            entity.HasOne(section => section.Book)
                .WithMany(book => book.SummarySections)
                .HasForeignKey(section => section.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookTakeaway>(entity =>
        {
            entity.HasIndex(takeaway => new { takeaway.BookId, takeaway.SortOrder });

            entity.HasOne(takeaway => takeaway.Book)
                .WithMany(book => book.Takeaways)
                .HasForeignKey(takeaway => takeaway.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookAudio>(entity =>
        {
            entity.Property(audio => audio.AudioUrl).HasMaxLength(700);
            entity.Property(audio => audio.VoiceName).HasMaxLength(120);
            entity.Property(audio => audio.Provider).HasMaxLength(120);

            entity.HasOne(audio => audio.Book)
                .WithMany(book => book.Audios)
                .HasForeignKey(audio => audio.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProcessingJob>(entity =>
        {
            entity.HasIndex(job => new { job.Status, job.Type, job.CreatedAt });
            entity.HasIndex(job => new { job.Status, job.Type, job.NextRunAt, job.CreatedAt });
            entity.Property(job => job.CurrentStep).HasMaxLength(500);

            entity.HasOne(job => job.Book)
                .WithMany(book => book.ProcessingJobs)
                .HasForeignKey(job => job.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(job => job.User)
                .WithMany()
                .HasForeignKey(job => job.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<BookProcessingQualityReport>(entity =>
        {
            entity.HasIndex(report => new { report.BookId, report.Stage, report.CreatedAt });
            entity.HasIndex(report => report.ProcessingJobId);
            entity.Property(report => report.Stage).HasConversion<string>().HasMaxLength(40);
            entity.Property(report => report.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(report => report.SummaryCoveragePercent).HasPrecision(5, 2);
            entity.Property(report => report.AudioCoveragePercent).HasPrecision(6, 2);
            entity.Property(report => report.WarningsJson).HasColumnType("jsonb");
            entity.Property(report => report.Notes).HasMaxLength(700);

            entity.HasOne(report => report.Book)
                .WithMany(book => book.QualityReports)
                .HasForeignKey(report => report.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(report => report.ProcessingJob)
                .WithMany(job => job.QualityReports)
                .HasForeignKey(report => report.ProcessingJobId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<BookSummaryPassCheckpoint>(entity =>
        {
            entity.HasIndex(checkpoint => new { checkpoint.BookId, checkpoint.PassIndex }).IsUnique();
            entity.Property(checkpoint => checkpoint.SourceHash).HasMaxLength(64);

            entity.HasOne(checkpoint => checkpoint.Book)
                .WithMany()
                .HasForeignKey(checkpoint => checkpoint.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AiModelMonitor>(entity =>
        {
            entity.HasIndex(metric => new { metric.Task, metric.Model }).IsUnique();
            entity.Property(metric => metric.Task).HasMaxLength(40);
            entity.Property(metric => metric.Model).HasMaxLength(160);
        });

        builder.Entity<AiModelOperationEvent>(entity =>
        {
            entity.HasIndex(operation => operation.OccurredAt);
            entity.HasIndex(operation => new { operation.AiModelMonitorId, operation.OccurredAt });
            entity.Property(operation => operation.FailureKind).HasMaxLength(40);
            entity.Property(operation => operation.ErrorMessage).HasMaxLength(500);
            entity.HasOne(operation => operation.Monitor)
                .WithMany(metric => metric.Events)
                .HasForeignKey(operation => operation.AiModelMonitorId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<UserBookmark>(entity =>
        {
            entity.HasIndex(bookmark => new { bookmark.UserId, bookmark.BookId }).IsUnique();

            entity.HasOne(bookmark => bookmark.User)
                .WithMany(user => user.Bookmarks)
                .HasForeignKey(bookmark => bookmark.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(bookmark => bookmark.Book)
                .WithMany(book => book.Bookmarks)
                .HasForeignKey(bookmark => bookmark.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReadingProgress>(entity =>
        {
            entity.HasIndex(progress => new { progress.UserId, progress.BookId }).IsUnique();
            entity.Property(progress => progress.LastPosition).HasMaxLength(180);

            entity.HasOne(progress => progress.User)
                .WithMany(user => user.ReadingProgressEntries)
                .HasForeignKey(progress => progress.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(progress => progress.Book)
                .WithMany(book => book.ReadingProgressEntries)
                .HasForeignKey(progress => progress.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(progress => progress.SummarySection)
                .WithMany()
                .HasForeignKey(progress => progress.SummarySectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<UserNote>(entity =>
        {
            entity.HasOne(note => note.User)
                .WithMany(user => user.Notes)
                .HasForeignKey(note => note.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(note => note.Book)
                .WithMany(book => book.Notes)
                .HasForeignKey(note => note.BookId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(note => note.SummarySection)
                .WithMany()
                .HasForeignKey(note => note.SummarySectionId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<BookReview>(entity =>
        {
            entity.HasIndex(review => new { review.UserId, review.BookId }).IsUnique();
            entity.HasIndex(review => new { review.BookId, review.CreatedAt });
            entity.Property(review => review.Comment).HasMaxLength(1200);

            entity.HasOne(review => review.User)
                .WithMany()
                .HasForeignKey(review => review.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(review => review.Book)
                .WithMany(book => book.Reviews)
                .HasForeignKey(review => review.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<BookReviewReply>(entity =>
        {
            entity.HasIndex(reply => new { reply.BookReviewId, reply.CreatedAt });
            entity.Property(reply => reply.Content).HasMaxLength(1200);

            entity.HasOne(reply => reply.Review)
                .WithMany(review => review.Replies)
                .HasForeignKey(reply => reply.BookReviewId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(reply => reply.User)
                .WithMany()
                .HasForeignKey(reply => reply.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChatSession>(entity =>
        {
            entity.HasIndex(session => new { session.UserId, session.BookId, session.UpdatedAt });
            entity.Property(session => session.Title).HasMaxLength(220);

            entity.HasOne(session => session.User)
                .WithMany(user => user.ChatSessions)
                .HasForeignKey(session => session.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(session => session.Book)
                .WithMany(book => book.ChatSessions)
                .HasForeignKey(session => session.BookId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChatMessage>(entity =>
        {
            entity.HasIndex(message => new { message.ChatSessionId, message.CreatedAt });

            entity.HasOne(message => message.ChatSession)
                .WithMany(session => session.Messages)
                .HasForeignKey(message => message.ChatSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChatCitation>(entity =>
        {
            entity.HasOne(citation => citation.ChatMessage)
                .WithMany(message => message.Citations)
                .HasForeignKey(citation => citation.ChatMessageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(citation => citation.BookContentChunk)
                .WithMany(chunk => chunk.ChatCitations)
                .HasForeignKey(citation => citation.BookContentChunkId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<AuthenticationOtpChallenge>(entity =>
        {
            entity.HasIndex(challenge => new
            {
                challenge.NormalizedEmail,
                challenge.Purpose,
                challenge.SentAt
            });
            entity.HasIndex(challenge => new
            {
                challenge.NormalizedEmail,
                challenge.Purpose,
                challenge.ConsumedAt,
                challenge.InvalidatedAt
            });
            entity.Property(challenge => challenge.NormalizedEmail).HasMaxLength(256);
            entity.Property(challenge => challenge.Purpose).HasMaxLength(40);
            entity.Property(challenge => challenge.CodeHash).HasMaxLength(32);
            entity.Property(challenge => challenge.CodeSalt).HasMaxLength(16);
        });

        builder.Entity<AuthenticationAuditEvent>(entity =>
        {
            entity.HasIndex(audit => audit.CreatedAt);
            entity.HasIndex(audit => new { audit.UserId, audit.CreatedAt });
            entity.HasIndex(audit => new { audit.NormalizedEmail, audit.CreatedAt });
            entity.Property(audit => audit.Action).HasMaxLength(80);
            entity.Property(audit => audit.NormalizedEmail).HasMaxLength(256);
            entity.Property(audit => audit.Detail).HasMaxLength(240);
            entity.Property(audit => audit.IpAddress).HasMaxLength(64);
            entity.Property(audit => audit.UserAgent).HasMaxLength(500);

            entity.HasOne(audit => audit.User)
                .WithMany()
                .HasForeignKey(audit => audit.UserId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
