using Microsoft.EntityFrameworkCore;
using BHS.CRG.Modules.Work.Data;

namespace BHS.CRG.Modules.Work.Devices;

internal sealed class DeviceInvitation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid EmployeeId { get; init; }
    public required string CodeHash { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset? RedeemedAt { get; set; }
}

internal sealed class DeviceSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid InvitationId { get; init; }
    public required Guid EmployeeId { get; init; }
    public required DateTimeOffset JoinedAt { get; init; }
    public required DateTimeOffset SessionExpiresAt { get; set; }
    public required string Grant { get; init; }
    public string AccessHash { get; set; } = null!;
    public DateTimeOffset AccessExpiresAt { get; set; }
    public string RefreshHash { get; set; } = null!;
    public string? PreviousRefreshHash { get; set; }
    public string? ProtectedRotationResponse { get; set; }
    public DateTimeOffset? RetryUntil { get; set; }
    public DeviceRevocation? Revocation { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateOnly? RevokedOn { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTimeOffset? DeliveryUntil { get; set; }

    public bool IsRevokedAt(DateTimeOffset now) =>
        RevokedAt.HasValue && (DeliveryUntil is null || now >= DeliveryUntil);
}

internal enum DeviceRevocation { Replacement, Dismissal, Lost }

internal static class DeviceModel
{
    public static void Configure(ModelBuilder model)
    {
        var invitation = model.Entity<DeviceInvitation>().ToTable("device_invitations");
        invitation.Property(i => i.Id).ValueGeneratedNever();
        invitation.Property(i => i.CodeHash).HasMaxLength(64);
        invitation.HasIndex(i => i.CodeHash).IsUnique();

        var session = model.Entity<DeviceSession>().ToTable("device_sessions");
        session.Property(s => s.Id).ValueGeneratedNever();
        session.Property<uint>(WorkDbContext.RowVersion).IsRowVersion();
        session.Property(s => s.Grant).HasMaxLength(40);
        session.Property(s => s.AccessHash).HasMaxLength(64);
        session.Property(s => s.RefreshHash).HasMaxLength(64);
        session.Property(s => s.PreviousRefreshHash).HasMaxLength(64);
        session.Property(s => s.Revocation).HasConversion<string>().HasMaxLength(16);
        session.HasOne<DeviceInvitation>().WithOne().HasForeignKey<DeviceSession>(s => s.InvitationId)
            .OnDelete(DeleteBehavior.Restrict);
        session.HasIndex(s => s.EmployeeId);
        session.HasIndex(s => s.AccessHash).IsUnique();
        session.HasIndex(s => s.RefreshHash).IsUnique();
        session.HasIndex(s => s.PreviousRefreshHash);
    }
}
