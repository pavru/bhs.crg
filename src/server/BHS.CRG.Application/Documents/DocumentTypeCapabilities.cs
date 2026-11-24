using BHS.CRG.Domain.Documents;

namespace BHS.CRG.Application.Documents;

public interface IDocumentTypeCapabilities
{
    bool SupportsAcceptance(DocumentType type);
}
