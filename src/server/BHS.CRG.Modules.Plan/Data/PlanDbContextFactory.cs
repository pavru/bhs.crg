using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BHS.CRG.Modules.Plan.Data;

/// <summary>
/// Как <c>dotnet ef</c> создаёт контекст модуля вне приложения — по образцу
/// <c>CostsDbContextFactory</c>, где причины расписаны полностью.
///
/// <para>Коротко: без своей фабрики <c>dotnet ef</c> искал бы контекст в контейнере хоста, а его
/// регистрирует сам модуль и только включённым, — генерация миграции зависела бы от
/// <c>Modules__Enabled</c> у разработчика.</para>
///
/// <para>⚠️ Строки подключения здесь НЕТ: базу называет только окружение
/// (<c>ConnectionStrings__Postgres</c>). Вписанный дев-стенд дал бы <c>dotnet ef database update</c>,
/// который молча мигрирует не ту базу, что настроена у приложения, и отвечает успехом. Генерации
/// миграций база не нужна.</para>
///
/// <para>Команда — в <c>CLAUDE.md</c> и <c>AGENTS.md</c>: у модуля свой <c>--context</c> и свой
/// <c>--project</c>.</para>
/// </summary>
public sealed class PlanDbContextFactory : IDesignTimeDbContextFactory<PlanDbContext>
{
    /// <summary>Адрес-заглушка: не резолвится ни в одну базу и в тексте отказа говорит, чего не хватает.</summary>
    internal const string NoConnection =
        "Host=задайте-ConnectionStrings__Postgres;Database=нет;Timeout=1";

    public PlanDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlanDbContext>();
        PlanDbContext.Configure(
            options,
            Environment.GetEnvironmentVariable("ConnectionStrings__Postgres") is { Length: > 0 } fromEnv
                ? fromEnv
                : NoConnection);

        return new PlanDbContext(options.Options);
    }
}
