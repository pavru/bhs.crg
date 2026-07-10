namespace BHS.CRG.Infrastructure.DataSets;

/// <summary>
/// Сериализуемое содержимое <see cref="Domain.DataSets.DataSetFile.InvoiceRawData"/> (issue #44) —
/// сырьё профиля «Счёт на оплату»: шапка (одна строка реквизитов) + товары (таблица). Источник истины
/// для кандидатов «Шапка»/«Товары» — источники создаёт пользователь (набор-centric, как у ГОСТ).
/// Непостраничная форма — своя запись, не переиспользует <see cref="GostGroupingData"/>.
/// </summary>
public record InvoiceRawData(Dictionary<string, string?> Header, List<Dictionary<string, string?>> LineItems);
