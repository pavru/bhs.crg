using System.Text.Json;
using BHS.CRG.Application.DataSets;
using BHS.CRG.Domain.Common;

namespace BHS.CRG.Infrastructure.Jobs;

internal sealed record GostRecognitionJobPayload(int Version, Guid ExpectedRevision, bool? AllowManualOverwrite)
{
    private const int CurrentVersion = 1;
    private const string RetryMessage =
        "Задание распознавания не содержит актуальных данных запуска. Запустите распознавание заново из интерфейса набора.";

    public static string Serialize(GostRecognitionAdmission admission) =>
        JsonSerializer.Serialize(
            new GostRecognitionJobPayload(CurrentVersion, admission.ExpectedRevision, admission.AllowManualOverwrite),
            JsonSerializerOptions.Web);

    public static GostRecognitionAdmission Parse(string? json)
    {
        try
        {
            if (json is not null &&
                JsonSerializer.Deserialize<GostRecognitionJobPayload>(json, JsonSerializerOptions.Web) is
                    { Version: CurrentVersion, AllowManualOverwrite: { } allow } payload &&
                payload.ExpectedRevision != Guid.Empty)
                return new GostRecognitionAdmission(payload.ExpectedRevision, allow);
        }
        catch (JsonException ex)
        {
            throw new ConflictException(RetryMessage, ex);
        }
        throw new ConflictException(RetryMessage);
    }
}
