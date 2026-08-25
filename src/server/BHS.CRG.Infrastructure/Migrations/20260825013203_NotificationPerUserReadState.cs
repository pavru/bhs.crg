using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Признак «прочитано» переезжает с уведомления на пару (уведомление, пользователь) — issue #821.
    ///
    /// Порядок шагов важен: таблица состояний создаётся ДО удаления колонки, иначе переносить будет
    /// нечего. Скаффолд ставил DropColumn первым.
    /// </summary>
    public partial class NotificationPerUserReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_user_states",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    IsDismissed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_user_states", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_user_states_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_user_states_notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalTable: "notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_user_states_NotificationId_UserId",
                table: "notification_user_states",
                columns: new[] { "NotificationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_user_states_UserId",
                table: "notification_user_states",
                column: "UserId");

            // Личные прочитанные — состояние заводится их владельцу. EXISTS обязателен: у
            // notifications."UserId" внешнего ключа нет, и там может лежать id уже удалённого
            // пользователя — без проверки вставка легла бы на новый FK.
            migrationBuilder.Sql("""
                INSERT INTO notification_user_states ("Id", "NotificationId", "UserId", "IsRead", "IsDismissed", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), n."Id", n."UserId", TRUE, FALSE, now(), now()
                FROM notifications n
                WHERE n."UserId" IS NOT NULL
                  AND n."IsRead"
                  AND EXISTS (SELECT 1 FROM "AspNetUsers" u WHERE u."Id" = n."UserId");
                """);

            // Общесистемные прочитанные: кто именно их прочёл, история не хранит — до сих пор отметка
            // была общей. Отмечаем прочитанными всем существующим пользователям: так экран остаётся
            // ровно таким, каким они его видели вчера. Иначе обновление подняло бы счётчик
            // непрочитанного у всех разом.
            migrationBuilder.Sql("""
                INSERT INTO notification_user_states ("Id", "NotificationId", "UserId", "IsRead", "IsDismissed", "CreatedAt", "UpdatedAt")
                SELECT gen_random_uuid(), n."Id", u."Id", TRUE, FALSE, now(), now()
                FROM notifications n
                CROSS JOIN "AspNetUsers" u
                WHERE n."UserId" IS NULL AND n."IsRead";
                """);

            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "notifications");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "notifications",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Обратно признак схлопывается: прочитано хотя бы кем-то — прочитано. Точнее откатить
            // нельзя, одной колонки на всех для этого и не хватало.
            migrationBuilder.Sql("""
                UPDATE notifications n SET "IsRead" = TRUE
                WHERE EXISTS (
                    SELECT 1 FROM notification_user_states s
                    WHERE s."NotificationId" = n."Id" AND s."IsRead");
                """);

            migrationBuilder.DropTable(
                name: "notification_user_states");
        }
    }
}
