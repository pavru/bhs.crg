using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class DeviceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_invitations",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RedeemedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "device_sessions",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SessionExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Grant = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AccessHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccessExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RefreshHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PreviousRefreshHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProtectedRotationResponse = table.Column<string>(type: "text", nullable: true),
                    RetryUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revocation = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_sessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_device_sessions_device_invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "work",
                        principalTable: "device_invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_device_invitations_CodeHash",
                schema: "work",
                table: "device_invitations",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_sessions_AccessHash",
                schema: "work",
                table: "device_sessions",
                column: "AccessHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_sessions_EmployeeId",
                schema: "work",
                table: "device_sessions",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_device_sessions_InvitationId",
                schema: "work",
                table: "device_sessions",
                column: "InvitationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_device_sessions_PreviousRefreshHash",
                schema: "work",
                table: "device_sessions",
                column: "PreviousRefreshHash");

            migrationBuilder.CreateIndex(
                name: "IX_device_sessions_RefreshHash",
                schema: "work",
                table: "device_sessions",
                column: "RefreshHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_sessions",
                schema: "work");

            migrationBuilder.DropTable(
                name: "device_invitations",
                schema: "work");
        }
    }
}
