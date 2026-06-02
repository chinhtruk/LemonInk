using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ZenRead.Migrations
{
    /// <inheritdoc />
    public partial class PersistAiModelOperationTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiModelMonitors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Task = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiModelMonitors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiModelOperationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AiModelMonitorId = table.Column<long>(type: "bigint", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    FailureKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    IsRetryable = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CooldownUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiModelOperationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiModelOperationEvents_AiModelMonitors_AiModelMonitorId",
                        column: x => x.AiModelMonitorId,
                        principalTable: "AiModelMonitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiModelMonitors_Task_Model",
                table: "AiModelMonitors",
                columns: new[] { "Task", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiModelOperationEvents_AiModelMonitorId_OccurredAt",
                table: "AiModelOperationEvents",
                columns: new[] { "AiModelMonitorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiModelOperationEvents_OccurredAt",
                table: "AiModelOperationEvents",
                column: "OccurredAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiModelOperationEvents");

            migrationBuilder.DropTable(
                name: "AiModelMonitors");
        }
    }
}
