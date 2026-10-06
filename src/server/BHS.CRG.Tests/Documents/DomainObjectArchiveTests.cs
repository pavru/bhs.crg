using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Архивная запись документом не становится (issue #1185, ТЗ CORE-34.4). Обратную сторону —
/// документ в архив не уходит — держит служба архива; её тесты в <c>BackupServiceTests.Archive</c>.
/// </summary>
public class DomainObjectArchiveTests
{
    private static DomainObject Record(DateTimeOffset? archivedAt) => DomainObject.Restore(
        Guid.NewGuid(), Guid.NewGuid(), "ООО Ромашка", JsonDocument.Parse("{}"),
        CatalogScope.System, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, archivedAt);

    [Fact]
    public void LiveRecord_CanBecomeDocument()
    {
        var live = Record(null);

        live.EnsureFacet();

        Assert.True(live.IsDocument);
    }

    [Fact]
    public void ArchivedRecord_CannotBecomeDocument()
    {
        var archived = Record(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => archived.EnsureFacet());
        Assert.False(archived.IsDocument);
    }
}
