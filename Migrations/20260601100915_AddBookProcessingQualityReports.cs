using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ZenRead.Migrations
{
    /// <inheritdoc />
    public partial class AddBookProcessingQualityReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookProcessingQualityReports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ProcessingJobId = table.Column<int>(type: "integer", nullable: true),
                    Stage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    EstimatedPageCount = table.Column<int>(type: "integer", nullable: true),
                    SourceChunkCount = table.Column<int>(type: "integer", nullable: false),
                    ExtractedWordCount = table.Column<int>(type: "integer", nullable: false),
                    DetectedChapterCount = table.Column<int>(type: "integer", nullable: false),
                    ExpectedChapterCount = table.Column<int>(type: "integer", nullable: false),
                    CoveredChapterCount = table.Column<int>(type: "integer", nullable: false),
                    MissingChapterCount = table.Column<int>(type: "integer", nullable: false),
                    SummarySectionCount = table.Column<int>(type: "integer", nullable: false),
                    SummaryWordCount = table.Column<int>(type: "integer", nullable: false),
                    AudioDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    ExpectedAudioDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    AudioSegmentCount = table.Column<int>(type: "integer", nullable: true),
                    AudioScriptCharacterCount = table.Column<int>(type: "integer", nullable: true),
                    SummaryCoveragePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    AudioCoveragePercent = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Notes = table.Column<string>(type: "character varying(700)", maxLength: 700, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookProcessingQualityReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookProcessingQualityReports_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookProcessingQualityReports_ProcessingJobs_ProcessingJobId",
                        column: x => x.ProcessingJobId,
                        principalTable: "ProcessingJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookProcessingQualityReports_BookId_Stage_CreatedAt",
                table: "BookProcessingQualityReports",
                columns: new[] { "BookId", "Stage", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BookProcessingQualityReports_ProcessingJobId",
                table: "BookProcessingQualityReports",
                column: "ProcessingJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookProcessingQualityReports");
        }
    }
}
