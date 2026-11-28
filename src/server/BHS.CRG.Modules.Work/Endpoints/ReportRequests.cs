using System.Text.Json;
using System.Text.Json.Serialization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Endpoints;

/// <summary>Материал строки в теле запроса: номенклатура и примечание (ТЗ WORK-50).</summary>
public sealed record ReportMaterialRequest(Guid? NomenclatureId, string? Note);

/// <summary>Фото строки в теле запроса: идентификатор от клиента (ТЗ MOB-15, <c>work-17</c>); файл догоняет отдельно.</summary>
public sealed record ReportPhotoRequest(Guid? Id, string? OriginalName = null);

/// <summary>
/// Строка работы в теле запроса. Поля, которых у строки нет, собираются в <see cref="Extra" />: иначе часы, положенные
/// в строку, молча пропали бы при разборе тела, а правило WORK-7 требует отказа с названием поля.
/// </summary>
public sealed record ReportLineRequest(
    Guid? Id, Guid? WorkTypeId, Guid? VariantId, decimal? Volume, Guid? UnitId, Guid? SectionId, string? Location,
    string? Comment, bool? IsCrew, JsonElement? Details, IReadOnlyList<ReportMaterialRequest>? Materials,
    IReadOnlyList<Guid>? Participants, IReadOnlyList<ReportPhotoRequest>? Photos = null, string? CrewRole = null)
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>
/// Тело подачи отчёта (ТЗ WORK-10, MOB-15, MOB-17) — необязательное: без него момент — время сервера.
/// </summary>
/// <param name="ChangeId">Идентификатор изменения от клиента; не назван — его создаёт сервер.</param>
/// <param name="DeviceTime">Время действия на устройстве — момент подачи, но не позже конца окна.</param>
public sealed record SubmitRequest(Guid? ChangeId, DateTimeOffset? DeviceTime)
{
    public bool ConfirmManagerEdit { get; init; }
}

/// <summary>
/// Тело заведения и правки отчёта (ТЗ WORK-4, MOB-15, MOB-17).
/// </summary>
/// <param name="Id">Идентификатор отчёта от клиента — у заведения обязателен (MOB-15); у правки — только ради сверки.</param>
/// <param name="EmployeeId">У своих отчётов — только ради отказа: сотрудника задаёт связь учётной записи. У ввода за
/// сотрудника — за кого вводится отчёт.</param>
/// <param name="ChangeId">Идентификатор изменения от клиента: у правки обязателен, повтор с ним не применяется второй
/// раз (MOB-15).</param>
/// <param name="DeviceTime">Время действия по часам устройства (MOB-17) — заявление; не названо — пишется пометкой.</param>
public sealed record ReportRequest(
    Guid? Id, Guid? EmployeeId, Guid? ConstructionId, DateOnly? Date, string? Shift, decimal? Hours, string? Note,
    Guid? ChangeId, DateTimeOffset? DeviceTime, IReadOnlyList<ReportLineRequest>? Lines)
{
    /// <summary>Причина обязательна для исправления закрытого отчёта.</summary>
    public string? Reason { get; init; }

    public bool ConfirmManagerEdit { get; init; }

    /// <summary>Шапка отчёта: стройка и дата обязательны (ТЗ WORK-58), смена по умолчанию — дневная (ТЗ MOB-35).</summary>
    public ReportHeaderValues Header() => new(
        ConstructionId ?? throw new InvalidRequestException(ReportRules.NoSite),
        Date ?? throw new InvalidRequestException("Не названа дата отчёта (date)."),
        Report.ParseShift(Shift), Hours, Note);

    /// <summary>Строки в порядке тела; часы в строке — отказ с номером строки и названием поля (ТЗ WORK-7).</summary>
    public IReadOnlyList<ReportLineValues> LineValues() => [.. (Lines ?? []).Select(LineOf)];

    private static ReportLineValues LineOf(ReportLineRequest line, int i)
    {
        ReportRules.EnsureNoLineHours(i, line.Extra?.Keys ?? Enumerable.Empty<string>());
        var at = $"Строка {i + 1}: ";
        return new ReportLineValues(
            line.Id ?? throw new InvalidRequestException(at + "не назван идентификатор строки (id) — его создаёт клиент."),
            line.WorkTypeId ?? throw new InvalidRequestException(at + "не назван вид работы (workTypeId)."),
            line.VariantId,
            line.Volume ?? 0m,
            line.UnitId ?? throw new InvalidRequestException(at + "не названа единица измерения (unitId)."),
            line.SectionId, line.Location, line.Comment, line.IsCrew ?? false,
            line.Details is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } details ? details.GetRawText() : null,
            [.. (line.Materials ?? []).Select(m => new ReportMaterialValues(m.NomenclatureId, m.Note))],
            [.. line.Participants ?? []],
            PhotosOf(at, line.Photos), line.CrewRole);
    }

    /// <summary>
    /// Перечень фото строки: не прислан — <c>null</c>, фото не трогаются (правка без знания о фото их не теряет).
    /// Идентификатор обязателен и один на фото.
    /// </summary>
    private static IReadOnlyList<ReportPhotoValues>? PhotosOf(string at, IReadOnlyList<ReportPhotoRequest>? photos)
    {
        if (photos is null) return null;
        var named = photos.Select(p => p.Id is { } id && id != Guid.Empty
            ? new ReportPhotoValues(id, PhotoName(at, p.OriginalName))
            : throw new InvalidRequestException(at + "у фото не назван идентификатор (photos[].id) — его создаёт клиент.")).ToList();
        if (named.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidRequestException(at + $"фото {twice.Key} названо дважды.");
        return named;
    }

    private static string? PhotoName(string at, string? name)
    {
        if (name is null) return null;
        var path = name.Replace('\\', '/');
        var fileName = ReportRules.Text(path[(path.LastIndexOf('/') + 1)..]);
        if (fileName is null) return null;
        if (fileName.Length > LinePhoto.OriginalNameLimit || fileName.Any(char.IsControl))
            throw new InvalidRequestException(at + $"имя файла фото должно содержать до {LinePhoto.OriginalNameLimit} знаков без управляющих символов.");
        return fileName;
    }
}
