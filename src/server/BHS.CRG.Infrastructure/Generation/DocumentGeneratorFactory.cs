using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Infrastructure.Generation;

public class DocumentGeneratorFactory(TypstGenerator typst) : IDocumentGeneratorFactory
{
    public IDocumentGenerator Create(OutputFormat format) => format switch
    {
        OutputFormat.Pdf => typst,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
