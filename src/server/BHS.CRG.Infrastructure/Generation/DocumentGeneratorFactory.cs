using BHS.CRG.Application.Generation;
using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Infrastructure.Generation;

public class DocumentGeneratorFactory(TypstGenerator typst) : IDocumentGeneratorFactory
{
    public IDocumentGenerator Create(OutputFormat format) => format switch
    {
        OutputFormat.Pdf => typst,
        OutputFormat.Docx => throw new NotSupportedException("DOCX output is not supported. Only PDF (Typst) is available."),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };
}
