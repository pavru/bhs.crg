using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public interface IDataSetResolver
{
    Task InjectAsync(GenerationContext ctx, DocumentInstance instance, CancellationToken ct = default);
}
