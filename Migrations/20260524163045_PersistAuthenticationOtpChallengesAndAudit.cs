using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ZenRead.Migrations
{
    /// <inheritdoc />
    public partial class PersistAuthenticationOtpChallengesAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthenticationAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Detail = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationAuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuthenticationAuditEvents_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AuthenticationOtpChallenges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CodeSalt = table.Column<byte[]>(type: "bytea", maxLength: 16, nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResendAvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    InvalidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthenticationOtpChallenges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationAuditEvents_CreatedAt",
                table: "AuthenticationAuditEvents",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationAuditEvents_NormalizedEmail_CreatedAt",
                table: "AuthenticationAuditEvents",
                columns: new[] { "NormalizedEmail", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationAuditEvents_UserId_CreatedAt",
                table: "AuthenticationAuditEvents",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationOtpChallenges_NormalizedEmail_Purpose_Consume~",
                table: "AuthenticationOtpChallenges",
                columns: new[] { "NormalizedEmail", "Purpose", "ConsumedAt", "InvalidatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuthenticationOtpChallenges_NormalizedEmail_Purpose_SentAt",
                table: "AuthenticationOtpChallenges",
                columns: new[] { "NormalizedEmail", "Purpose", "SentAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthenticationAuditEvents");

            migrationBuilder.DropTable(
                name: "AuthenticationOtpChallenges");
        }
    }
}
