namespace BHS.CRG.Application.Documents;

/// <summary>Канал чтения: код для хранения и название для человека.</summary>
public record TypeReadChannel(string Code, string Title);

/// <summary>
/// Пути, которыми объекты общей таблицы читают ОПТОМ (ТЗ CORE-18, STG-11). Закрытый тип открыт
/// ровно тем каналам, которые названы у него в списке.
///
/// Словарь объявлен, а не свободен, по той же причине, что и ключи серверных настроек: опечатка в
/// коде канала — это тихо расширенный или тихо сузившийся доступ, а объявленный список делает её
/// отказом при сохранении.
///
/// ⚠️ **Ни один из этих каналов сегодня в список не смотрит.** Признак заведён заранее, вместе с
/// владельцем (issue #955), а действовать начинает работой `STG-11` — переводом всех этих путей на
/// единую проверку прав. Шесть из них ходят в базу прямым запросом мимо слоя доступа, и пока это
/// так, правило одно: того, что показывать нельзя, в общей таблице не держат вовсе. Список здесь —
/// чтобы в тот день было что включать, а не чтобы притвориться, будто уже включено.
/// </summary>
public static class TypeReadChannels
{
    public static readonly TypeReadChannel CommonData = new("commonData", "Общие данные");
    public static readonly TypeReadChannel Mcp = new("mcp", "Инструменты MCP");
    public static readonly TypeReadChannel Backup = new("backup", "Резервная копия");
    public static readonly TypeReadChannel Matching = new("matching", "Поиск сопоставления объектов");
    public static readonly TypeReadChannel References = new("references", "Индекс ссылок");
    public static readonly TypeReadChannel DataSets = new("dataSets", "Наборы данных и источники");

    public static readonly IReadOnlyList<TypeReadChannel> All =
        [CommonData, Mcp, Backup, Matching, References, DataSets];

    public static bool IsKnown(string code) => All.Any(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    /// <summary>Коды, которых нет в словаре. Пусто — все названы верно.</summary>
    public static IReadOnlyList<string> Unknown(IEnumerable<string> codes)
        => [.. codes.Where(c => !IsKnown(c)).Distinct(StringComparer.Ordinal)];
}
