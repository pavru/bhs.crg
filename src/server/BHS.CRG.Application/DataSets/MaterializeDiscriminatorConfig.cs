using System.Text.Json.Serialization;

namespace BHS.CRG.Application.DataSets;

/// <summary>
/// Правило выбора варианта union'а по строке источника (issue #716).
///
/// <see cref="Column"/> — колонка-признак, <see cref="Kind"/> — как её читать, <see cref="Rules"/> —
/// какие типы документов относятся к какому варианту: <c>{"АОСР":["&lt;guid типа&gt;"],…}</c>.
///
/// Вариант без правила ВЫКЛЮЧЕН: строки его типов пропускаются. Это законное состояние, а не
/// недонастройка — реестр вполне может собирать не все виды документов комплекта.
///
/// Живёт в Application, а не рядом с применяющим кодом: тем же объектом настройка приходит с
/// клиента, хранится в источнике и читается резолвером — трёх разных представлений одного правила
/// быть не должно.
/// </summary>
public record MaterializeDiscriminatorConfig(
    string Column,
    string Kind,
    Dictionary<string, List<Guid>> Rules)
{
    /// <summary>Колонка несёт КОД типа документа («АОСР», «РеестрРаботАОСР»).</summary>
    public const string ByTypeCode = "docTypeCode";

    /// <summary>Колонка несёт идентификатор документа — тип берётся у него.</summary>
    public const string ByDocumentId = "docId";

    [JsonIgnore]
    public bool IsByDocumentId => string.Equals(Kind, ByDocumentId, StringComparison.OrdinalIgnoreCase);
}
