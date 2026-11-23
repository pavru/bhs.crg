using BHS.CRG.Domain.Common;

namespace BHS.CRG.Application.DataSets;

public sealed record GostRecognitionAdmission(Guid ExpectedRevision, bool AllowManualOverwrite);

public sealed class ManualGroupingConflict(Guid fileId, string fileName, string message)
    : ConflictException(message)
{
    public Guid FileId { get; } = fileId;
    public string FileName { get; } = fileName;
}

public sealed class DataSetRevisionConflict(Exception? inner = null)
    : ConflictException("Набор данных изменился. Откройте его заново и повторите операцию.", inner);
