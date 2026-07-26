namespace BHS.CRG.Application.Reconciliation;

/// <summary>Как сравнивать числа сторон.</summary>
public enum ComparisonOperator
{
    /// <summary>Слева должно быть столько же, сколько справа.</summary>
    Equal,

    /// <summary>Слева должно быть не меньше, чем справа (проложено ≥ заявлено в реестре).</summary>
    GreaterOrEqual,

    /// <summary>Слева должно быть не больше, чем справа.</summary>
    LessOrEqual,
}

/// <summary>Допуск в абсолютной величине либо в процентах от правой стороны.</summary>
public enum ToleranceKind { Absolute, Percent }

/// <summary>
/// Одна сторона сверки: источник и то, что из него брать.
/// </summary>
/// <param name="KeyColumns">Колонки, образующие ДОМЕННЫЙ ключ (марка, сечение). Порядок значим —
/// стороны обязаны перечислять их согласованно, иначе ключи не сойдутся.</param>
/// <param name="ValueColumn">Колонка количества. Строки с одинаковым ключом суммируются: в кабельном
/// журнале одна марка идёт десятком линий, и сравнивать надо итог по марке, а не отдельные строки.</param>
/// <param name="Sources">Свод по нескольким источникам (issue #450): «сумма по четырём листам шкафов
/// против сводной спецификации». Задан — <paramref name="SourceId"/> и колонки рядом игнорируются.</param>
/// <param name="LabelColumn">Чем позицию назвать человеку. Пусто — берётся первая ключевая колонка.</param>
public record ReconciliationSide(
    Guid SourceId,
    IReadOnlyList<string> KeyColumns,
    string ValueColumn,
    string? LabelColumn = null,
    IReadOnlyList<SideSource>? Sources = null)
{
    /// <summary>
    /// Источники стороны с их колонками. Пусто — сторона одиночная, как и была: спеки уже лежат в БД
    /// со старым полем, и ломать их ради формы записи нельзя.
    /// </summary>
    public IReadOnlyList<SideSource> EffectiveSources =>
        Sources is { Count: > 0 } ? Sources : [new SideSource(SourceId, KeyColumns, ValueColumn, LabelColumn)];
}

/// <summary>
/// Один источник в составе стороны (issue #450). Колонки у каждого СВОИ: листы шкафов называют их
/// по-разному, и требовать единообразия значило бы заставить пользователя править исходники ради сверки.
/// </summary>
public record SideSource(
    Guid SourceId,
    IReadOnlyList<string> KeyColumns,
    string ValueColumn,
    string? LabelColumn = null);

/// <param name="Tolerance">Ноль — точное сравнение. Допуск гасит расхождения округления, которые
/// иначе забьют отчёт находками на сотые доли.</param>
public record ComparisonRule(
    ComparisonOperator Operator,
    double Tolerance = 0,
    ToleranceKind ToleranceKind = ToleranceKind.Absolute);

/// <summary>
/// Спека сверки — хранится данными (jsonb на определении), а не кодом: новый раздел не должен
/// означать новый C# (решение #414). Нормализация значений (бухты в метры, полная марка) остаётся за
/// вычисляемыми колонками источника на Jint — второго языка выражений мы не заводим.
/// </summary>
public record ReconciliationSpec(
    ReconciliationSide Left,
    ReconciliationSide Right,
    ComparisonRule Comparison);

/// <summary>
/// Единые настройки (де)сериализации спеки. Одни на всех — движок, обработчики, тесты и эндпоинты:
/// разойдись они, спека, записанная одним путём, перестала бы читаться другим, и прогон падал бы уже
/// на рабочих данных.
///
/// Перечисления строками намеренно: спека лежит в БД, и «GreaterOrEqual» в jsonb читается глазами, а
/// «1» требует заглянуть в исходники.
/// </summary>
public static class ReconciliationSpecJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };
}
