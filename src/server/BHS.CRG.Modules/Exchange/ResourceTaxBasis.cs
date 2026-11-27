using System.Text.Json.Serialization;

namespace BHS.CRG.Modules.Exchange;

/// <summary>Основа конкретной сметной цены или суммы. Неизвестная основа сохраняет число,
/// но не позволяет считать его подтверждённой стоимостью без НДС. Значение по умолчанию безопасно
/// для старых редакций и промежуточного JSON, в которых доказательство основы не сохранялось.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResourceTaxBasis>))]
public enum ResourceTaxBasis
{
    Unknown = 0,
    ExcludingVat = 1,
}
