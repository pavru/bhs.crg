using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Generation;

public record GenerateDocumentCommand(Guid InstanceId, OutputFormat Format, string? GeneratedBy = null) : IRequest<GeneratedFile>;
