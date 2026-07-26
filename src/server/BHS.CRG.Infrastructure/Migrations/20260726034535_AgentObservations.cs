using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AgentObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_observations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Detail = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    References = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ReportedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_observations_Scope_ScopeId_Key",
                table: "agent_observations",
                columns: new[] { "Scope", "ScopeId", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_observations");
        }
    }
}
