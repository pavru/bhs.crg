using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Modules.Work.Migrations
{
    /// <inheritdoc />
    public partial class DeviceObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_observations",
                schema: "work",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApkVersionCode = table.Column<int>(type: "integer", nullable: false),
                    PendingReports = table.Column<int>(type: "integer", nullable: false),
                    OldestPendingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastContactAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_device_observations", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_device_observations_device_sessions_DeviceId",
                        column: x => x.DeviceId,
                        principalSchema: "work",
                        principalTable: "device_sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_observations",
                schema: "work");
        }
    }
}
