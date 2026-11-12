using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UserRecentRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_recent_records",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recent_records", x => new { x.UserId, x.RecordType, x.RecordId });
                    table.ForeignKey(
                        name: "FK_user_recent_records_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_recent_records_UserId_OpenedAt",
                table: "user_recent_records",
                columns: new[] { "UserId", "OpenedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_recent_records");
        }
    }
}
