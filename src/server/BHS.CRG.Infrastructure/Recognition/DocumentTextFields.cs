using BHS.CRG.Application.QualityDocs;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Поле для ленивого извлечения всего текста документа (issue #51) — по образцу InvoiceFields/
/// GostTableFields, но проще: один скалярный вызов вместо реестра колонок. Субстрат для
/// вычисляемых колонок (напр. regex-извлечение доп-полей: ТекстЛиста.match(/паттерн/)).
/// </summary>
public static class DocumentTextFields
{
    /// <summary>Ключ поля в проекции «Документы» и в ответе распознавателя.</summary>
    public const string Path = "ТекстЛиста";

    public static readonly RecognitionField Field = new(Path,
        "Весь текст документа одной строкой: все страницы по порядку, на каждой странице — сверху вниз, " +
        "слева направо; фрагменты объединены ОДНИМ пробелом, без переносов строк", "string");
}
