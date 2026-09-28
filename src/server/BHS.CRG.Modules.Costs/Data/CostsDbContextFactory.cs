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
/// <para>⚠️ Строки подключения здесь НЕТ, и это решение, а не пропуск (ревью PR #1107). Первая
/// редакция подставляла дев-стенд (порт 5433), если переменная окружения не задана, — и тогда
/// <c>dotnet ef database update</c> для модуля молча мигрировал ЧУЖУЮ базу: не ту, что настроена у
/// приложения, а ту, что вписана здесь. Отвечал он при этом успехом. Поэтому базу называет только
/// окружение: <c>ConnectionStrings__Postgres</c>.</para>
///
/// <para>Без переменной генерация миграций работает как обычно — <c>migrations add</c> к базе не
/// подключается, ему нужен лишь провайдер, — а всё, что базу трогает (<c>database update</c>,
/// <c>migrations list</c>), отказывает с указанием адреса-заглушки. Отказ здесь дешевле успеха не на
/// той базе: применяет миграции модуля приложение при старте, и обычно руками этого делать не
/// нужно.</para>
///
/// <para>Команда — в <c>CLAUDE.md</c> и <c>AGENTS.md</c>: у модуля свой <c>--context</c> и свой
/// <c>--project</c>, и без них миграция уезжает в набор ядра.</para>
/// </summary>
public sealed class CostsDbContextFactory : IDesignTimeDbContextFactory<CostsDbContext>
{
    /// <summary>
    /// Адрес-заглушка: не резолвится ни в одну базу. Виден в тексте отказа, поэтому назван так, чтобы
    /// прочитавший понял, чего не хватает.
    /// </summary>
    internal const string NoConnection =
        "Host=задайте-ConnectionStrings__Postgres;Database=нет;Timeout=1";

    public CostsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CostsDbContext>();
        CostsDbContext.Configure(
            options,
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") is { Length: > 0 } fromEnv
                ? fromEnv
                : NoConnection);

        return new CostsDbContext(options.Options);
    }
}
