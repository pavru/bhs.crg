using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

/// <summary>
/// C#-аналог ref/merge механизма из NewElementResolverStyles.xsl старой системы.
/// Принимает DocumentInstance, разрешает ссылки на сущности каталога,
/// возвращает собранный GenerationContext.
/// </summary>
public interface IEntityResolver
{
    Task<GenerationContext> ResolveAsync(DocumentInstance instance, CancellationToken ct = default);
}
