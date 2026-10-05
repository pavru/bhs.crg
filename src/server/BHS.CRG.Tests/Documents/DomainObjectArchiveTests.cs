using System.Text.Json;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;

namespace BHS.CRG.Tests.Documents;

/// <summary>
/// Архив и документ несовместимы — в обе стороны (issue #1185, ТЗ CORE-34.4): документ в архив не
/// уходит, архивная запись документом не становится. Вторая дверь нужна, чтобы первая не обходилась
/// в два шага.
/// </summary>
public class DomainObjectArchiveTests
{
    private static DomainObject Record(DateTimeOffset? archivedAt) => DomainObject.Restore(
        Guid.NewGuid(), Guid.NewGuid(), "ООО Ромашка", JsonDocument.Parse("{}"),
        CatalogScope.System, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, archivedAt);

    [Fact]
    public void Record_IsArchivable()
    {
        Record(null).EnsureArchivable();
        // Повторный архив — не ошибка сущности: «менять нечего» отвечает служба.
        Record(DateTimeOffset.UtcNow).EnsureArchivable();
    }

    [Fact]
    public void Document_IsNotArchivable()
    {
        var document = DomainObject.RestoreDocument(
            Guid.NewGuid(), Guid.NewGuid(), "Акт", JsonDocument.Parse("{}"), Guid.NewGuid(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null,
            DocumentStatus.Draft, 0, null, null, null, JsonDocument.Parse("{}"));

        Assert.Throws<InvalidRequestException>(document.EnsureArchivable);
        Assert.False(document.IsArchived);
    }

    [Fact]
    public void ArchivedRecord_CannotBecomeDocument()
    {
        var archived = Record(DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => archived.EnsureFacet());
        Assert.False(archived.IsDocument);
    }
}
