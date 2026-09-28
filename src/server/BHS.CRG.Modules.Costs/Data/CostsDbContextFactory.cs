using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BHS.CRG.Modules.Costs.Data;

/// <summary>
/// Как <c>dotnet ef</c> создаёт контекст модуля вне приложения (задача A2a этапа 2, issue #1072).
///
/// <para>Зачем своя фабрика, когда рядом есть приложение. Без неё <c>dotnet ef</c> поднимает хост
/// API и ищет контекст в его контейнере — а контекст регистрирует САМ МОДУЛЬ, и только когда он
/// включён. То есть генерация миграции зависела бы от того, что написано в <c>Modules__Enabled</c> у
/// разработчика, и на обычной сборке (где включён только <c>id</c>) отвечала бы «контекста нет» —
/// сообщением, по которому причина не угадывается.</para>
///
/// <para>⚠️ Строка подключения здесь нужна только для выбора провайдера: <c>migrations add</c> к базе
/// не подключается. Значение из окружения (<c>ConnectionStrings__Postgres</c>), иначе дев-стенд —
/// порт 5433, как везде в решении. Применяет миграции приложение при старте, а не эта фабрика.</para>
///
/// <para>Команда — в <c>CLAUDE.md</c> и <c>AGENTS.md</c>: у модуля свой <c>--context</c> и свой
/// <c>--project</c>, и без них миграция уезжает в набор ядра.</para>
/// </summary>
public sealed class CostsDbContextFactory : IDesignTimeDbContextFactory<CostsDbContext>
{
    public CostsDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5433;Username=postgres;Password=xxsystem;Database=bhs_crg";

        var options = new DbContextOptionsBuilder<CostsDbContext>();
        CostsDbContext.Configure(options, connection);
        return new CostsDbContext(options.Options);
    }
}
