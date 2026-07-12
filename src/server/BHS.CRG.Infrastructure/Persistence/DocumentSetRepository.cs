using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Infrastructure.Persistence;

/// <summary>
/// Репозиторий комплекта. Документы комплекта больше не навигация (issue #84) — их грузит
/// <see cref="DomainObjectRepository"/> по расположению (Set, setId); здесь только сам комплект.
/// </summary>
public class DocumentSetRepository(AppDbContext db) : Repository<DocumentSet>(db);
