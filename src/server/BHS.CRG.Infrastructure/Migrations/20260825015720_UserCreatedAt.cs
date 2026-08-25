using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BHS.CRG.Infrastructure.Migrations
{
    /// <summary>
    /// Дата заведения учётной записи — по ней список уведомлений отсекает общесистемные, выпущенные
    /// до появления пользователя (issue #821).
    ///
    /// Значение по умолчанию — 0001-01-01, и это существенно: существующим пользователям оно
    /// достаётся при обновлении, а любое уведомление новее — значит для них не меняется ничего.
    /// Подставь сюда «сейчас», обновление разом спрятало бы у всех весь накопленный список.
    /// </summary>
    public partial class UserCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "AspNetUsers");
        }
    }
}
