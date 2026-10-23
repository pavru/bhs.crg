using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;

namespace BHS.CRG.Api.Endpoints.Documents;

/// <summary>
/// Общее у адресов, пишущих записи общих данных, — общего, сотрудников и видов работ: разбор поля
/// <c>data</c>, ответ «такая запись есть в архиве» и списочный ответ без тяжёлых листьев. В одном
/// месте, чтобы двери не расходились: экран узнаёт отказ по полям, и дверь, ответившая иначе,
/// оставила бы его без кнопки.
/// </summary>
internal static class RecordRequests
{
    internal const string DataNotJson =
        "Поле «data» — строка с JSON, а разобрать её не удалось. Обратите внимание: это строка, " +
        "а не вложенный объект.";

    /// <summary>
    /// Реквизиты из запроса — или <c>null</c>, если это не JSON (нашло ревью PR #1053). Пустое — пустой
    /// объект: новая карточка без данных осмысленна.
    ///
    /// <para>Без разбора здесь негодная строка уходила бы наружу как 500 «внутренняя ошибка сервера»
    /// с предложением сообщить об ошибке и записью в журнале ошибок — то есть ошибка ввода
    /// выглядела бы поломкой сервера.</para>
    /// </summary>
    internal static JsonDocument? ParseData(string? raw)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// «Есть в архиве» — полями, а не словами (issue #1185): экран предлагает вернуть запись кнопкой
    /// и повторить создание с <c>createAnyway</c>, и разбирать для этого фразу не должен.
    /// </summary>
    internal static IResult ArchivedTwin(ArchivedTwinException ex) => Results.Conflict(new
    {
        error = ex.Message, code = "archived-twin",
        archivedId = ex.ArchivedId, archivedName = ex.ArchivedName, archivedScope = ex.ArchivedScope,
    });

    /// <summary>
    /// Списочный ответ без тяжёлой полезной нагрузки (issue #520). Зовётся ЯВНО в списочных местах и
    /// никогда внутри <see cref="CommonDataEntryDto.From"/>: тот обслуживает и чтение одной записи, и
    /// ответы POST/PUT — отсечение там молча обрезало бы редактор и round-trip записи.
    /// </summary>
    internal static CommonDataEntryDto Elide(CommonDataEntryDto dto) =>
        dto with { Data = HeavyLeafElision.WithoutHeavyLeaves(dto.Data) };
}
