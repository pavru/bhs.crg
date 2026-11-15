using System.Globalization;
using BHS.CRG.Domain.Common;
using BHS.CRG.Modules.Ports;

namespace BHS.CRG.Modules.Work.Data;

/// <summary>
/// Правила отчёта и строки (ТЗ WORK-5, WORK-7, WORK-25, WORK-49, WORK-50, CORE-9; change <c>work-13-report-model</c>)
/// — чистые функции над тем, что прочитано из портов: базы и хоста им не нужно, и тесты на них идут без хоста.
///
/// <para>Нарушение — 400 (<see cref="InvalidRequestException" />) со словами, где названо поле: строку правит
/// человек на телефоне, и «неверный запрос» без поля ему не помог бы.</para>
///
/// <para>Ссылки на архивные записи здесь не проверяются — это вопрос к ядру (<c>IModuleReferenceTargets</c>), и
/// задают его только о НОВЫХ значениях (<see cref="ReportReferenceCheck" />).</para>
/// </summary>
public static class ReportRules
{
    public const string NoSite = "Не названа стройка отчёта (constructionId): отчёт без стройки не принимается.";

    /// <summary>Ключи часов, которых в строке быть не должно (ТЗ WORK-7): время — на отчёт целиком.</summary>
    public static readonly IReadOnlyList<string> LineHoursKeys = ["hours", "часы"];

    /// <summary>Знаков после запятой у часов — столько хранит колонка <c>hours</c> (<c>numeric(5,2)</c>).</summary>
    public const int HoursScale = 2;

    /// <summary>Знаков после запятой у объёма — столько хранит колонка <c>volume</c> (<c>numeric(18,4)</c>).</summary>
    public const int VolumeScale = 4;

    /// <summary>Предел объёма строки: целая часть колонки <c>volume</c> — 14 знаков.</summary>
    public const decimal MaxVolume = 99_999_999_999_999m;

    /// <summary>
    /// Часы отчёта — от 0 до 24, не точнее сотых; <c>null</c> — ещё не названы (черновик). Лишние знаки — отказ, а не
    /// молчаливое округление базой: человек увидел бы потом не то, что ввёл.
    /// </summary>
    public static void EnsureHours(decimal? hours)
    {
        if (hours is not { } h) return;
        if (h < 0 || h > Report.MaxHours)
            throw new InvalidRequestException(
                $"Часы отчёта (hours) — от 0 до {Report.MaxHours.ToString(CultureInfo.InvariantCulture)}, а названо " +
                $"{h.ToString(CultureInfo.InvariantCulture)}.");
        if (decimal.Round(h, HoursScale) != h)
            throw new InvalidRequestException($"Часы отчёта (hours) — не точнее сотых, а названо {h.ToString(CultureInfo.InvariantCulture)}.");
    }

    /// <summary>
    /// Объём строки — не отрицательный, в пределах колонки и не точнее четырёх знаков. Ноль принимается: черновик
    /// строки, где объём ещё не назван (колонка обязательна, «не названо» другим способом не сказать).
    /// </summary>
    public static void EnsureVolume(int position, decimal volume)
    {
        var at = $"Строка {position + 1}";
        if (volume < 0)
            throw new InvalidRequestException($"{at}: объём (volume) не может быть отрицательным.");
        if (volume > MaxVolume)
            throw new InvalidRequestException($"{at}: объём (volume) больше допустимого.");
        if (decimal.Round(volume, VolumeScale) != volume)
            throw new InvalidRequestException($"{at}: объём (volume) — не больше {VolumeScale} знаков после запятой.");
    }

    /// <summary>Текст поля как он ляжет в колонку: пустой и из пробелов — <c>null</c>, иначе без краевых пробелов.</summary>
    public static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// Строка не несёт часов (ТЗ WORK-7): поле молча отброшенным не будет — человек решил бы, что часы по строкам
    /// сохранены. <paramref name="lineKeys" /> — имена полей строки: путь записи спрашивает о ключах сведений
    /// (<see cref="ReportWriter" />), адрес — о полях строки в теле запроса.
    /// </summary>
    public static void EnsureNoLineHours(int position, IEnumerable<string> lineKeys)
    {
        var hours = lineKeys.FirstOrDefault(k => LineHoursKeys.Contains(k.Trim(), StringComparer.OrdinalIgnoreCase));
        if (hours is not null)
            throw new InvalidRequestException(
                $"Строка {position + 1}: поле «{hours}» не принимается — часы указываются на отчёт целиком (hours отчёта).");
    }

    /// <summary>
    /// Строка годна для стройки отчёта: вид работы есть, вариант — его, раздел — этой стройки, материал — по
    /// номенклатуре, участники — только у бригадной работы.
    /// </summary>
    /// <param name="workType">Вид работы строки из порта; <c>null</c> — такого вида нет.</param>
    /// <param name="site">Стройка отчёта из порта.</param>
    public static void EnsureLine(int position, ReportLineValues line, ModuleWorkType? workType, ModuleConstruction site)
    {
        var at = $"Строка {position + 1}";
        if (line.Id == Guid.Empty)
            throw new InvalidRequestException($"{at}: не назван идентификатор строки (id) — его создаёт клиент.");
        if (workType is null)
            throw new InvalidRequestException($"{at}: вид работы (workTypeId) не найден в классификаторе.");
        if (line.UnitId == Guid.Empty)
            throw new InvalidRequestException($"{at}: не названа единица объёма (unitId).");
        EnsureVolume(position, line.Volume);

        EnsureVariant(at, line.VariantId, workType);
        EnsureSection(at, line.SectionId, site);
        EnsureMaterials(at, line.Materials);
        if (!line.IsCrew && line.Participants.Count > 0)
            throw new InvalidRequestException(
                $"{at}: участники (participants) перечисляются только у бригадной работы (isCrew) — у этой строки " +
                "признака бригады нет.");
    }

    /// <summary>Вариант — вариант своего вида работы (ТЗ WORK-25, WORK-49); вид без вариантов принимает строку без варианта.</summary>
    private static void EnsureVariant(string at, Guid? variantId, ModuleWorkType workType)
    {
        if (variantId is not { } id) return;
        if (workType.Variants.FirstOrDefault(v => v.Id == id) is { } own && own.BelongsTo(workType.Id)) return;
        throw new InvalidRequestException(
            $"{at}: вариант (variantId) не принадлежит виду работы «{workType.DisplayName}»" +
            (workType.Variants.Count == 0 ? " — у этого вида вариантов нет." : "."));
    }

    /// <summary>Раздел — раздел стройки отчёта, а не другой.</summary>
    private static void EnsureSection(string at, Guid? sectionId, ModuleConstruction site)
    {
        if (sectionId is not { } id || site.Sections.Any(s => s.Id == id)) return;
        throw new InvalidRequestException(
            $"{at}: раздел (sectionId) не принадлежит стройке «{site.Name}» — выберите раздел этой стройки.");
    }

    /// <summary>Материал — только ссылкой на номенклатуру (ТЗ WORK-50); свободный текст — в примечании.</summary>
    private static void EnsureMaterials(string at, IReadOnlyList<ReportMaterialValues> materials)
    {
        for (var i = 0; i < materials.Count; i++)
            if (materials[i].NomenclatureId is not { } nomenclature || nomenclature == Guid.Empty)
                throw new InvalidRequestException(
                    $"{at}, материал {i + 1}: не названа позиция номенклатуры (nomenclatureId) — материал выбирается " +
                    "из справочника, свободный текст допустим только в примечании (note).");
    }
}
