using BHS.CRG.Domain.Common;

namespace BHS.CRG.Modules.Plan.Schedules;

internal sealed class Schedule
{
    internal const int NameLength = 250;

    private Schedule() { }

    public Guid Id { get; private init; }
    public Guid ConstructionId { get; private init; }
    public Guid? RevisionId { get; private init; }
    public Guid CreationKey { get; private init; }
    public string Name { get; private init; } = null!;
    public string? DistributionFingerprint { get; private set; }
    public int Unmapped { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }

    internal static string ValidateName(string? name) =>
        name?.Trim() is { Length: > 0 and <= NameLength } allowed ? allowed
            : throw new InvalidRequestException($"Назовите график (до {NameLength} знаков).");

    internal static Schedule Create(Guid construction, Guid? revision, Guid key, string name, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(), ConstructionId = construction, RevisionId = revision, CreationKey = key,
            Name = name, CreatedAt = now, UpdatedAt = now,
        };

    internal void Rebuilt(string fingerprint, int unmapped)
    {
        DistributionFingerprint = fingerprint;
        Unmapped = unmapped;
        Touch();
    }

    internal void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}
