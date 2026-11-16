using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ActivityChannelIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_activity_log_channel_series_heads",
                table: "activity_log",
                columns: new[] { "Channel", "OccurredAt", "Id" },
                descending: new[] { false, true, true },
                filter: "\"SeriesHead\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_activity_log_channel_series_heads",
                table: "activity_log");
        }
    }
}
