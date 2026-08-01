using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Domain.Documents;

/// <summary>
/// Связь «материал → документ качества» по ИДЕНТИЧНОСТИ материала (артикул/наименование),
/// а не по индексу строки — поэтому переживает переимпорт набора данных. Подмешивается
/// в поле «ДокументПодтверждающийКачетво» при генерации.
/// </summary>
public class MaterialQualityLink : Entity
{
    public CatalogScope Scope { get; private set; }
    public Guid? ScopeId { get; private set; }

    /// <summary>Нормализованный ключ идентичности материала (артикул или наименование).</summary>
    public string MaterialKey { get; private set; } = null!;

    /// <summary>
    /// Человекочитаемое имя материала на МОМЕНТ ПРИВЯЗКИ — снимок, а не ссылка (issue #554).
    ///
    /// Ключ машинный, и у 41 связки из 113 это голый артикул (<c>mb15-07-01m-54</c> — боковая панель
    /// ВРУ): по нему человек материал не узнаёт, а именно в артикульной половине и сидят неверные
    /// связки (#552). Хранить приходится снимком, потому что резолвить на лету неоткуда: строки
    /// наборов данных не персистятся, глобального реестра материалов нет, а материал вообще может
    /// больше нигде не встречаться.
    ///
    /// Устаревание безобидно: материал переименовали — метка осталась исторической, ключ по-прежнему
    /// матчит. Null у связок, заведённых до появления поля.
    /// </summary>
    public string? MaterialLabel { get; private set; }

    public Guid QualityDocumentId { get; private set; }

    private MaterialQualityLink() { }

    public static MaterialQualityLink Create(CatalogScope scope, Guid? scopeId, string materialKey,
        Guid qualityDocumentId, string? materialLabel = null)
        => new()
        {
            Scope = scope,
            ScopeId = scopeId,
            MaterialKey = materialKey,
            MaterialLabel = Trim(materialLabel),
            QualityDocumentId = qualityDocumentId,
        };

    public void Retarget(Guid qualityDocumentId)
    {
        QualityDocumentId = qualityDocumentId;
        TouchUpdatedAt();
    }

    /// <summary>
    /// Обновляет метку, если она пришла. Пустой меткой НЕ затираем: перепривязка из места, где
    /// человеческого имени под рукой нет, не должна отнимать имя, добытое при первой привязке.
    /// </summary>
    public void DescribeMaterial(string? materialLabel)
    {
        var label = Trim(materialLabel);
        if (label is null || label == MaterialLabel) return;
        MaterialLabel = label;
        TouchUpdatedAt();
    }

    /// <summary>Предел колонки — 512 (issue #554). Метка это склейка ВСЕХ полей идентичности, она
    /// систематически длиннее ключа; переполнение уронило бы весь пакет привязки на 22001 — несоразмерная
    /// плата за декоративный снимок, поэтому обрезаем здесь.</summary>
    public const int MaxLabelLength = 512;

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= MaxLabelLength ? trimmed : trimmed[..(MaxLabelLength - 1)] + "…";
    }
}
