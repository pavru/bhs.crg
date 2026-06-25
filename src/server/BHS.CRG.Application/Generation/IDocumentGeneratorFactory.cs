using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Generation;

public interface IDocumentGeneratorFactory
{
    IDocumentGenerator Create(OutputFormat format);
}
