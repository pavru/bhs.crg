using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Чистая логика расщепления результата одного вызова распознавания счёта на оплату
/// (см. InvoiceFields) на шапку (плоские поля) и таблицу товаров (вложенный JSON-массив в
/// одном поле ответа) — вынесена отдельно от DataSetService ради юнит-тестируемости без БД/blob.
/// </summary>
public static class InvoiceRecognitionSplitter
{
    /// <summary>Шапка по полям ПРОФИЛЯ (issue #406): набор полей задаёт профиль, а не код.</summary>
    public static Dictionary<string, string?> SplitHeader(
        IReadOnlyDictionary<string, string?> values, IReadOnlyList<RecognitionField> headerFields)
        => headerFields.ToDictionary(f => f.Path, f => values.GetValueOrDefault(f.Path));

    /// <summary>Сломанный/не-JSON ответ модели по товарам — не падаем, возвращаем пустой список
    /// (шапка при этом уже распознана независимо): набор данных хранит сырьё и показывает его как
    /// есть. Разбор общий с портом модулей — <see cref="WholeFileRecognition.ReadRows" />.</summary>
    public static List<Dictionary<string, string?>> SplitLineItems(IReadOnlyDictionary<string, string?> values)
        => WholeFileRecognition.ReadRows(values.GetValueOrDefault(InvoiceFields.LineItemsPath)).Rows;
}
