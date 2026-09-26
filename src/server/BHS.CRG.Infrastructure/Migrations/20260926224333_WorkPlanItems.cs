using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Таблица перечня работ стройки (ТЗ CORE-10, CORE-11, issue #964).
    ///
    /// <para>Позиция — якорь всех модулей: «вид работы + стройка + раздел + единица измерения» и
    /// ничего больше. Колонок ровно пять, и места под объём, цену, сроки или статус здесь нет
    /// намеренно — мнение о работе принадлежит модулю, а не общей точке (CORE-11).</para>
    ///
    /// <para>⚠️ Уникальный ключ — с <c>NULLS NOT DISTINCT</c>. По умолчанию PostgreSQL считает NULL
    /// в уникальном индексе разными значениями, то есть две позиции «стройка в целом, без раздела»
    /// легли бы обе, а это штатный случай (CORE-Q2): смета у заказчика бывает на стройку целиком.
    /// Дальше «найди или создай» находил бы то одну, то другую.</para>
    ///
    /// <para>Таблица создаётся ПУСТОЙ и в этапе 1 не наполняется ничем: позиции заводит сервер по
    /// «найди или создай» с первым потребителем — импортом сметы в этапе 3 (CORE-12).</para>
    /// </summary>
    public partial class WorkPlanItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_plan_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkTypeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConstructionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_plan_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_work_plan_items_constructions_ConstructionId",
                        column: x => x.ConstructionId,
                        principalTable: "constructions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_work_plan_items_domain_objects_UnitId",
                        column: x => x.UnitId,
                        principalTable: "domain_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_plan_items_domain_objects_WorkTypeId",
                        column: x => x.WorkTypeId,
                        principalTable: "domain_objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_plan_items_sections_SectionId",
                        column: x => x.SectionId,
                        principalTable: "sections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_plan_items_ConstructionId_SectionId",
                table: "work_plan_items",
                columns: new[] { "ConstructionId", "SectionId" });

            migrationBuilder.CreateIndex(
                name: "IX_work_plan_items_SectionId",
                table: "work_plan_items",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_work_plan_items_UnitId",
                table: "work_plan_items",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_work_plan_items_WorkTypeId_ConstructionId_SectionId_UnitId",
                table: "work_plan_items",
                columns: new[] { "WorkTypeId", "ConstructionId", "SectionId", "UnitId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_plan_items");
        }
    }
}
