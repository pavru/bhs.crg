using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Application.Common;

/// <summary>
/// Специализированный репозиторий <see cref="DomainObject"/> (issue #84): загрузка документов
/// комплекта с документной фасетой (у общих данных фасеты нет). Обычные CRUD — из <see cref="IRepository{T}"/>;
/// <see cref="IRepository{T}.GetByIdAsync"/> здесь грузит фасету и её файлы.
/// </summary>
public interface IDomainObjectRepository : IRepository<DomainObject>
{
    /// <summary>Документы комплекта — объекты на оси (Set, setId), у которых есть фасета.
    /// <paramref name="tracked"/>=true — с трекингом и фасетой (для массовых изменений порядка/т.п.).</summary>
    Task<IReadOnlyList<DomainObject>> GetSetDocumentsAsync(Guid setId, bool tracked, CancellationToken ct = default);

    /// <summary>Документы нескольких комплектов (untracked, для списков/пикеров).</summary>
    Task<IReadOnlyList<DomainObject>> GetDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default);

    /// <summary>Все документы заданного типа (с трекингом и фасетой+файлами) — для инвалидации
    /// вывода при изменении шаблона (issue #362): фильтр по пинам делается в памяти.</summary>
    Task<IReadOnlyList<DomainObject>> GetDocumentsOfTypeAsync(Guid documentTypeId, CancellationToken ct = default);

    /// <summary>Число документов по каждому комплекту (лёгкий COUNT, без JSONB) — для счётчиков навигации
    /// и каскадов удаления в дереве стройки. Комплекты без документов в словарь не попадают.</summary>
    Task<IReadOnlyDictionary<Guid, int>> CountDocumentsInSetsAsync(IReadOnlyCollection<Guid> setIds, CancellationToken ct = default);
}
