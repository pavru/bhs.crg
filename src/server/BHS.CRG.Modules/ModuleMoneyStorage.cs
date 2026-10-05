namespace BHS.CRG.Modules;

/// <summary>
/// Деньги модуля не хранятся в общей таблице объектов (ТЗ STG-6, CORE-24.2, COST-29; задача H1 этапа 2,
/// issue #1104).
///
/// <para><b>Почему правило о МЕСТЕ ХРАНЕНИЯ, а не о путях чтения.</b> Общую таблицу читают оптом три
/// десятка мест — «Общие данные», инструменты MCP, поиск сопоставления, индекс ссылок, наборы данных,
/// генерация с отладочным комплектом, — и ни одно не отбирает по модулю-владельцу типа. Перечень такой
/// длины расходится молча: новый путь чтения появляется в другом файле и в другой день. Инвариант
/// хранения не расходится: суммы, которых в общей таблице нет, не прочитать оттуда никаким путём, и
/// пользователь без права на модуль не находит их не потому, что каждый путь проверен, а потому, что
/// искать негде.</para>
///
/// <para>Проверяется при старте и по ВСЕМ модулям сборки, а не по включённым: тип выключенного модуля
/// уже лежит в базе, и его объекты читались бы теми же путями.</para>
/// </summary>
public static class ModuleMoneyStorage
{
    /// <summary>Что нарушено — по строке на поле; пусто — нарушений нет.</summary>
    public static IReadOnlyList<string> Problems(IEnumerable<IAppModule> modules)
    {
        var all = modules.ToList();
        // Денежные тэги — со всей сборки: поле типа одного модуля вправе нести тэг другого.
        var money = all.SelectMany(m => m.Tags).Where(t => t.Money).Select(t => t.Code)
            .ToHashSet(StringComparer.Ordinal);

        return [.. from module in all
                   from type in module.RecordTypes
                   where type.Storage == ModuleStorage.SharedObject
                   from field in type.Fields
                   from tag in field.Tags
                   where money.Contains(tag)
                   select $"тип «{type.Code}» модуля «{module.Code}»: поле «{field.Key}» несёт денежный тэг " +
                          $"«{tag}», а тип хранится в общей таблице объектов"];
    }

    /// <summary>Отказ старта, называющий тип. Молчаливого «заведём, а потом закроем пути» не бывает.</summary>
    public static void Ensure(IEnumerable<IAppModule> modules)
    {
        var problems = Problems(modules);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            "Деньги модуля объявлены в общей таблице объектов:\n  " + string.Join("\n  ", problems) + "\n" +
            "Общую таблицу читают «Общие данные», MCP, поиск сопоставления, наборы данных и генерация — " +
            "и ни один из этих путей не смотрит на права модуля: сумму увидел бы любой вошедший. " +
            $"Объявите типу носитель {nameof(ModuleStorage)}.{nameof(ModuleStorage.ModuleTable)} и держите " +
            "записи в таблице модуля, под его правами.");
    }
}
