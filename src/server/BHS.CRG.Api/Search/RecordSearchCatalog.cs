using BHS.CRG.Modules;
using BHS.CRG.Modules.Exchange;

namespace BHS.CRG.Api.Search;

/// <summary>Группа поиска и чья она: код модуля или <see cref="CoreRecordSearch.Module" />.</summary>
public sealed record RecordSearchSource(string Module, RecordSearchGroup Group);

/// <summary>
/// Группы поиска экземпляра — проверенные при старте (shell-05, задача 1.6; ТЗ AUTH-8.2).
///
/// <para><b>Почему при старте.</b> Группа без ключа доступа отвечала бы всем, кто вошёл, — и показала бы
/// номер счёта бухгалтеру без права на счета (тот самый «ломается так» из <c>design.md</c>). Найденная на
/// первом запросе, она уже отдала бы записи; найденная при старте — не отдаст ни одной. Так же устроены
/// таблицы модулей: объявление без ключа — отказ старта.</para>
///
/// <para>Что проверяется: у группы есть код, название и код типа записи; ключ доступа назван и это
/// объявленное право; код группы и код типа записи на экземпляре уникальны — по коду клиент выбирает
/// чип, по типу «Недавние» находят владельца записи, и двум владельцам одного ключа быть нельзя.</para>
/// </summary>
public sealed class RecordSearchCatalog
{
    public RecordSearchCatalog(IEnumerable<(string Module, IReadOnlyList<RecordSearchGroup> Groups)> providers,
        PermissionCatalog permissions)
    {
        Sources = [.. providers.SelectMany(p => p.Groups.Select(g => new RecordSearchSource(p.Module, g)))];

        var problems = Sources.SelectMany(s => ProblemsOf(s, permissions)).ToList();
        foreach (var twice in Sources.GroupBy(s => s.Group.Code, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"код группы «{twice.Key}» объявлен несколько раз: {Owners(twice)}");
        foreach (var twice in Sources.GroupBy(s => s.Group.RecordType, StringComparer.Ordinal).Where(g => g.Count() > 1))
            problems.Add($"тип записи «{twice.Key}» у нескольких групп: {Owners(twice)}");

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Группы поиска записей объявлены негодно — приложение не стартует (ТЗ AUTH-8.2, CORE-34):\n  " +
                string.Join("\n  ", problems));
    }

    /// <summary>Группы в порядке состава поставки: модули, затем ядро.</summary>
    public IReadOnlyList<RecordSearchSource> Sources { get; }

    public RecordSearchSource? Find(string code) =>
        Sources.FirstOrDefault(s => string.Equals(s.Group.Code, code, StringComparison.Ordinal));

    public RecordSearchSource? ForRecordType(string recordType) =>
        Sources.FirstOrDefault(s => string.Equals(s.Group.RecordType, recordType, StringComparison.Ordinal));

    private static string Owners(IEnumerable<RecordSearchSource> sources) =>
        string.Join(", ", sources.Select(s => $"«{s.Module}» ({s.Group.Title})"));

    private static IEnumerable<string> ProblemsOf(RecordSearchSource s, PermissionCatalog permissions)
    {
        var name = $"группа «{s.Group.Code}» модуля «{s.Module}»";
        if (string.IsNullOrWhiteSpace(s.Group.Code)) yield return $"у группы модуля «{s.Module}» нет кода";
        if (string.IsNullOrWhiteSpace(s.Group.Title)) yield return $"{name}: нет названия";
        if (string.IsNullOrWhiteSpace(s.Group.RecordType)) yield return $"{name}: не назван код типа записи";
        if (string.IsNullOrWhiteSpace(s.Group.Requires))
            yield return $"{name}: не назван ключ доступа — без него группа отвечала бы любому вошедшему";
        else if (!permissions.Declares(s.Group.Requires))
            yield return $"{name}: ключ доступа «{s.Group.Requires}» — не объявленное право";
        if (!Enum.IsDefined(s.Group.Archive))
            yield return $"{name}: не решено, что группа делает с архивом (ТЗ CORE-34.4)";
    }

    /// <summary>
    /// Каталог, служба и группы ядра — в контейнер. Каталог собирается фабрикой из собранного контейнера:
    /// группы называют реализации контракта включённых модулей, а их видно только через порт контрактов в
    /// области запроса. Разрешается при старте (<c>StartupTasks</c>).
    /// </summary>
    public static IServiceCollection AddTo(IServiceCollection services)
    {
        services.AddScoped<CoreRecordSearch>();
        services.AddSingleton(new RecordSearchLimits());
        services.AddSingleton(sp =>
        {
            using var scope = sp.CreateScope();
            var modules = scope.ServiceProvider.GetRequiredService<IModuleContracts>().GetAll<IRecordSearch>();
            return new RecordSearchCatalog(
                [.. modules.Select(p => (p.Module, p.Contract.Groups)), (CoreRecordSearch.Module, CoreRecordSearch.Declared)],
                sp.GetRequiredService<PermissionCatalog>());
        });
        services.AddScoped<RecordSearchService>();
        return services;
    }
}

/// <summary>
/// Пределы поиска. Одиночкой в контейнере, а не константами: тесту отказа по времени нужен предел в
/// десятки миллисекунд, а не в секунды.
/// </summary>
/// <param name="Take">Записей на группу (<c>design.md</c>: «не больше пяти и „есть ещё“»).</param>
/// <param name="PerGroup">Сколько ждать одну группу: медленный поставщик не держит весь ответ, а отвечает
/// отказом у своей группы — как таймаут подсказок поля (#1530).</param>
public sealed record RecordSearchLimits(int Take = 5, TimeSpan PerGroup = default)
{
    public TimeSpan PerGroup { get; init; } = PerGroup == default ? TimeSpan.FromSeconds(2) : PerGroup;
}
