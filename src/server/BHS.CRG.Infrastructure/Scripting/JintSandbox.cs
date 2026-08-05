using Jint;
using Jint.Runtime;

namespace BHS.CRG.Infrastructure.Scripting;

/// <summary>
/// Единая песочница для пользовательских JS-выражений: вычисляемые колонки наборов данных
/// (<c>DataSetComputedColumnExecutor</c>) и вычисляемые поля документов
/// (<c>JintExpressionEvaluator</c>).
/// </summary>
/// <remarks>
/// <para>
/// Конфигурация одна на оба места намеренно. Раньше она была скопирована, и копии обязаны были
/// совпадать «по договорённости» — а расходятся такие копии молча: ужесточили одну, вторая осталась
/// прежней, и обходной путь остался ровно там, где о нём никто не помнит.
/// </para>
/// <para>
/// Выражения задают: для колонок — любой пользователь, для полей — администратор, но исполняются
/// они у всех и на каждой строке источника, повторно при каждой генерации.
/// </para>
/// </remarks>
public static class JintSandbox
{
    /// <summary>
    /// Потолок памяти. Таймаут сам по себе процесс не спасает: экспоненциальный рост строки
    /// (<c>s = s + s</c> в цикле) успевает съесть гигабайты внутри отведённой секунды, а
    /// <c>OutOfMemoryException</c> процесс уже не переживёт — упадёт весь сервер, вместе с чужими
    /// генерациями. 16 МБ с запасом хватает любому осмысленному выражению над строкой таблицы.
    /// </summary>
    private const long MemoryLimitBytes = 16L * 1024 * 1024;

    /// <summary>
    /// Потолок числа инструкций. Второй предохранитель к таймауту: он ловит зацикливание раньше,
    /// чем истечёт секунда, и не зависит от того, насколько занята машина.
    /// </summary>
    private const int StatementLimit = 500_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    /// <summary>Ограничение рекурсии — против переполнения стека на самовызове.</summary>
    private const int RecursionLimit = 32;

    /// <summary>Движок с общими ограничениями. Значения и помощники выставляет вызывающий.</summary>
    public static Engine Create() => new(cfg => cfg
        .TimeoutInterval(Timeout)
        .LimitRecursion(RecursionLimit)
        .LimitMemory(MemoryLimitBytes)
        .MaxStatements(StatementLimit));

    /// <summary>
    /// Выражение упёрлось в предел ресурсов (а не просто ошиблось на конкретных данных)?
    ///
    /// Различать это нужно там, где выражение исполняется на КАЖДОЙ строке: ошибка на негодных
    /// данных — обычное дело и повод положить null в одну ячейку, а исчерпание ресурса означает,
    /// что выражение негодно само по себе, и остальные строки его не исправят.
    /// </summary>
    public static bool IsResourceLimit(Exception ex) => ex
        is MemoryLimitExceededException
        or StatementsCountOverflowException
        or RecursionDepthOverflowException
        or ExecutionCanceledException
        or TimeoutException;
}
