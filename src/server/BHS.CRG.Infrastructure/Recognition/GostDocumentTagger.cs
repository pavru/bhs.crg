using BHS.CRG.Domain.Schema;

namespace BHS.CRG.Infrastructure.Recognition;

/// <summary>
/// Определяет функциональный тэг документа ГОСТ-профиля (тип таблицы) по НаименованиеДокумента —
/// авто-подсказка при распознавании; пользователь может переопределить в редакторе разбиения.
/// Чистая функция (тестируемо без БД/LLM).
/// </summary>
public static class GostDocumentTagger
{
    /// <summary>Тэг типа таблицы по наименованию, либо null (документ не является таблицей известного типа).</summary>
    public static string? DetectTableTag(string? documentName)
    {
        var n = (documentName ?? "").ToLowerInvariant();
        if (n.Length == 0) return null;
        // Кабельный журнал — проверяем раньше спецификации (у него своё устойчивое сочетание).
        if (n.Contains("кабельн") && n.Contains("журнал")) return FunctionalTag.GostDocCableJournal;
        if (n.Contains("специфик")) return FunctionalTag.GostDocSpecification;
        if (n.Contains("ведомост") && (n.Contains("материал") || n.Contains("оборудован")))
            return FunctionalTag.GostDocSpecification;
        return null;
    }
}
