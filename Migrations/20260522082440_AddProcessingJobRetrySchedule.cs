using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZenRead.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingJobRetrySchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "CurrentStep",
                table: "ProcessingJobs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(180)",
                oldMaxLength: 180);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextRunAt",
                table: "ProcessingJobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_Status_Type_NextRunAt_CreatedAt",
                table: "ProcessingJobs",
                columns: new[] { "Status", "Type", "NextRunAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ProcessingJobs_Status_Type_NextRunAt_CreatedAt",
                table: "ProcessingJobs");

            migrationBuilder.DropColumn(
                name: "NextRunAt",
                table: "ProcessingJobs");

            migrationBuilder.AlterColumn<string>(
                name: "CurrentStep",
                table: "ProcessingJobs",
                type: "character varying(180)",
                maxLength: 180,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);
        }
    }
}
