using System.Text.Json;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>
/// Быстрая разноска (задача F2, issue #1086, ТЗ COST-12): как делить и между чем.
/// </summary>
/// <param name="Method"><c>equal</c> — поровну, <c>percent</c> — по процентам целей, <c>document</c> —
/// пересчитать разноску счёта без строк по появившимся строкам (ТЗ COST-11).</param>
/// <param name="Targets">Цели: <c>construction</c>, <c>section</c> и у «по %» — <c>percent</c>. У пересчёта
/// цели не присылаются — их задаёт прежняя разноска счёта.</param>
public sealed record AllocationPreviewRequest(string? Method, IReadOnlyList<JsonElement>? Targets);

/// <summary>
/// Разноска счёта ЦЕЛИКОМ — матрица «строки × объекты» (F2). Присланное есть новое состояние всех строк и
/// счёта: каждая строка счёта обязана быть в наборе, пустой набор частей означает «строка не разнесена».
/// </summary>
/// <param name="Lines">Строки: <c>line</c> — идентификатор строки, <c>parts</c> — её части.</param>
/// <param name="Document">Части счёта целиком — только у счёта без строк.</param>
public sealed record AllocationMatrixRequest(IReadOnlyList<JsonElement>? Lines, IReadOnlyList<JsonElement>? Document);

/// <summary>Часть в наборе матрицы — те же поля, что принимает запись.</summary>
public sealed record MatrixPart(Guid Construction, Guid? Section, decimal? Quantity, decimal? Amount);

/// <summary>Строка в наборе матрицы.</summary>
public sealed record MatrixLine(Guid Line, IReadOnlyList<MatrixPart> Parts);

/// <summary>Набор матрицы в ответе предпросмотра — ровно то, что форма пошлёт на запись.</summary>
public sealed record MatrixState(IReadOnlyList<MatrixLine> Lines, IReadOnlyList<MatrixPart> Document);

/// <summary>Клетка, в которую ушёл остаток округления: строка (<c>null</c> — счёт целиком) и цель.</summary>
public sealed record RemainderCell(Guid? Line, Guid Construction, Guid? Section);

/// <summary>
/// Предпросмотр быстрой разноски — посчитанный СЕРВЕРОМ и ничего не записавший.
///
/// <para>⚠️ Числа предпросмотра приезжают в ТОМ ЖЕ виде, что у записанного счёта
/// (<see cref="LineAllocationView" />, <see cref="AllocationSummaryView" />), и считает их тот же код.
/// Поэтому «до» и «после» применения совпадают посимвольно не по совпадению, а по устройству: форма
/// рисует оба одной и той же функцией, а применение записывает <see cref="Apply" /> — присланное сюда же
/// состояние.</para>
/// </summary>
/// <param name="Apply">Что записать, если человек согласен, — тело <c>PUT …/allocation</c>.</param>
public sealed record AllocationPreview(
    MatrixState Apply,
    IReadOnlyDictionary<Guid, LineAllocationView> Lines,
    AllocationSummaryView Summary,
    IReadOnlyList<RemainderCell> Remainders);
