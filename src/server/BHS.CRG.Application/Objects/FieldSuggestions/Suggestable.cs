using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Application.Objects.FieldSuggestions;

/// <summary>
/// Подсказывается ли поле значениями, введёнными ранее (ТЗ TYPE-2, issue #1529, эпик #1527).
///
/// <para>Решение нужно трём местам — адресу подсказок (отвечает пустым списком), полю формы (не зовёт
/// адрес зря) и редактору типа (флажок или причина вместо него). Три своих проверки разошлись бы на
/// первом же новом тэге, поэтому правило одно, истина — здесь, а на клиенте — зеркало
/// (<c>shared/api/suggestable.ts</c>). Расхождение ловит общий файл случаев
/// <c>shared/api/suggestableCases.json</c>: его читают тесты обеих сторон.</para>
///
/// <para>Ответ — КОД причины, а не да/нет: редактор показывает причину вместо флажка. Коды — строки, а
/// не числа перечисления: их сверяет общий файл и читает клиент, а число молча сдвинулось бы при
/// вставке.</para>
/// </summary>
public static class Suggestable
{
    /// <summary>Поле подсказывается.</summary>
    public const string Yes = "suggested";
    /// <summary>Вид поля — не строка: в <c>text</c> пишут абзацы, в <c>primitive</c> — ИНН, КПП, телефоны.</summary>
    public const string NotString = "type";
    /// <summary>Значение вычисляется, человек его не вводит.</summary>
    public const string Computed = "computed";
    /// <summary>Поле заперто модулем или ядром — значение кладёт код.</summary>
    public const string Locked = "locked";
    /// <summary>Выключено вручную тэгом <see cref="FunctionalTag.InputNoSuggest" />.</summary>
    public const string Manual = "manual";

    /// <summary>
    /// Тэги однозначности и их коды — В ПОРЯДКЕ проверки: у поля с несколькими такими тэгами код —
    /// первого из них. Значение такого поля у каждой записи своё, и подсказка чужого вела бы к дублю:
    /// второй записи с тем же номером, ИНН или артикулом.
    ///
    /// ⚠️ Перечень сверяет перепись <c>SuggestableTagCensusTests</c>: строковый тэг ядра без решения
    /// «однозначен / нет» роняет её с названием тэга.
    ///
    /// <para><see cref="FunctionalTag.Identity" /> здесь НЕТ (решение владельца 23.10.2026, issue #1533):
    /// это компонент составного ключа, сам по себе он не уникален — у заказчика его несёт «Номенклатура →
    /// Производитель», главный потребитель подсказки. Повтор ключа целиком ловят «такая уже есть» и
    /// сверка двойников.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Tag, string Reason)> UniqueTags =
    [
        (FunctionalTag.OrgTaxId, "inn"),
        (FunctionalTag.DocNumber, "docNumber"),
        (FunctionalTag.EmployeePersonnelNumber, "personnelNumber"),
        // Решение В-4 эпика (22.10.2026): оба — тэги однозначности.
        (FunctionalTag.NomenclatureArticle, "article"),
        (FunctionalTag.OrgName, "orgName"),
        // Поле ядра с этим тэгом заперто и отсекается раньше, но тэг ставится любому строковому
        // полю — и там подсказка чужой учётной записи привязала бы к ней второго сотрудника.
        (FunctionalTag.EmployeeAccount, "account"),
    ];

    /// <summary>
    /// Код причины по полю схемы. Порядок проверок фиксирован и записан в общем файле случаев:
    /// вид → вычисляемое → запертое → тэги однозначности → выключено вручную.
    /// </summary>
    /// <param name="type">Вид поля (<c>SchemaField.type</c>).</param>
    /// <param name="tags">Записи тэгов поля как в схеме — с номером или без (<c>identity:1</c>).</param>
    public static string Reason(string type, bool computed, bool locked, IReadOnlyCollection<string> tags)
    {
        if (type != "string") return NotString;
        if (computed) return Computed;
        if (locked) return Locked;

        var codes = tags.Select(TagCode.CodeOf).ToHashSet(StringComparer.Ordinal);
        foreach (var (tag, reason) in UniqueTags)
            if (codes.Contains(tag)) return reason;

        return codes.Contains(FunctionalTag.InputNoSuggest) ? Manual : Yes;
    }
}
