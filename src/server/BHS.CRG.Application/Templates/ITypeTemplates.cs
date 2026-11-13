namespace BHS.CRG.Application.Templates;

/// <summary>
/// Шаблоны типа документа — идентификатор и «действует ли версия», без строк шаблонов (issue #1960).
/// Проверка ссылок документа зовётся на каждый щелчок флажка и каждую правку параметра; общий
/// репозиторий тянул бы ради неё весь Typst-текст каждой версии типа.
/// </summary>
public interface ITypeTemplates
{
    /// <summary>Все версии шаблонов типа: id → действует ли.</summary>
    Task<IReadOnlyDictionary<Guid, bool>> ActiveByIdAsync(Guid documentTypeId, CancellationToken ct = default);
}
