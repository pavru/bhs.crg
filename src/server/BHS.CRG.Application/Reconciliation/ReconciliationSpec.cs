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
/// <param name="LabelColumn">Чем позицию назвать человеку. Пусто — берётся первая ключевая колонка.</param>
public record ReconciliationSide(
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
