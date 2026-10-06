using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Costs.Endpoints;

/// <summary>Что справочник говорит о ссылке, которой у записи ещё не было.</summary>
public enum NewReference
{
    /// <summary>Запись этого вида на месте и предлагается на выбор.</summary>
    Fine,

    /// <summary>Запись этого вида есть, но она в архиве: уже стоящую ссылку на неё держат, новую — нет.</summary>
    Archived,

    /// <summary>Записи этого вида с таким идентификатором нет: её удалили либо она другого вида.</summary>
    Missing,
}

/// <summary>
/// Правило записи модуля — одно на счёт, строки, разноску и накладную (ТЗ CORE-34.4, issue #1185):
/// <b>новая</b> ссылка обязана вести к записи нужного вида, которая на месте и не в архиве; ссылка,
/// которая у документа уже стояла, принимается любой — потерянной и архивной тоже.
///
/// <para>«Уже стояла» решает звавший: только он знает, что лежит в документе. Сюда приходят одни
/// новые ссылки — и поэтому правило нельзя применить «заодно» к сохранённым: счёт закрытого периода
/// с архивным поставщиком перестал бы сохраняться целиком.</para>
///
/// <para>Одно место, а не по проверке на адрес: проверок было четыре, и накладная от остальных уже
/// отставала — не принимала стоявшую потерянную позицию. Архив, добавленный в три места из четырёх,
/// оставил бы дверь, через которую архивная запись возвращается в новые документы.</para>
/// </summary>
public static class NewReferences
{
    /// <summary>
    /// Вердикт по каждой спрошенной ссылке; <c>null</c> — вида в системе нет вовсе, и судить не о чем.
    /// Что с этим делать, решает звавший: строке счёта без справочника ссылаться не на что, а счёт
    /// без типа «Организация» завести всё равно нельзя.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, NewReference>?> JudgeAsync(
        IModuleCatalog catalog, string typeCode, IReadOnlyCollection<Guid> fresh, CancellationToken ct)
    {
        if (fresh.Count == 0) return new Dictionary<Guid, NewReference>();
        // По идентификаторам порт отдаёт и архивные записи — с признаком: иначе архивную было бы не
        // отличить от удалённой, а чинятся они по-разному.
        if (await catalog.RefsAsync(typeCode, fresh, ct) is not { } found) return null;

        var known = found.ToDictionary(r => r.Id, r => r.Archived ? NewReference.Archived : NewReference.Fine);
        return fresh.Distinct().ToDictionary(id => id, id => known.GetValueOrDefault(id, NewReference.Missing));
    }

    /// <summary>
    /// Номера строк, чья НОВАЯ ссылка получила этот вердикт. Стоявшие ссылки сюда не попадают: их
    /// среди спрошенных нет, и вердикта у них нет.
    /// </summary>
    public static IReadOnlyList<int> Rows(
        IEnumerable<(int Number, Guid? Id)> rows, IReadOnlyDictionary<Guid, NewReference> verdicts, NewReference verdict) =>
        [.. rows.Where(r => r.Id is { } id && verdicts.TryGetValue(id, out var found) && found == verdict)
            .Select(r => r.Number)];

    /// <summary>Строки с новой ссылкой на архивную запись — отказ, названы все разом.</summary>
    public static void EnsureNoneArchived(
        IEnumerable<(int Number, Guid? Id)> rows, IReadOnlyDictionary<Guid, NewReference> verdicts, string what)
    {
        if (Rows(rows, verdicts, NewReference.Archived) is { Count: > 0 } archived)
            throw InArchive((archived.Count == 1 ? "Строка " : "Строки ") + string.Join(", ", archived), what);
    }

    /// <summary>
    /// Отказ на новую ссылку в архив — одними словами у всех четырёх адресов.
    /// </summary>
    /// <param name="where">Где стоит ссылка: «Поставщик», «Строка 3», «Часть 2».</param>
    /// <param name="what">Что выбрано, в именительном: «организация», «позиция номенклатуры».</param>
    public static InvalidRequestException InArchive(string where, string what) => new(
        $"{where}: {what} в архиве — в выборе её нет, и новая ссылка на неё не записывается. Там, где " +
        "она уже стоит, она остаётся. Выберите действующую запись либо верните эту из архива.");
}
