using Microsoft.EntityFrameworkCore.Migrations;

namespace BHS.CRG.Infrastructure.Migrations;

public sealed partial class WorkContentRollbackBarrier : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Запись EF в журнале миграций нужна до первого запуска WorkContentMaintenance.
        // update.sh сравнивает этот журнал со снимком и останавливает возврат к старому API.
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Операция меняет только запись EF в журнале при явном понижении схемы.
        // Штатный откат через update.sh проверяет журнал перед запуском прежнего API.
    }
}
