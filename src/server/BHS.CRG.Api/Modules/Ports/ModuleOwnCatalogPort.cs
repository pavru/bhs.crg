using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using BHS.CRG.Modules.Ports;
using MediatR;

namespace BHS.CRG.Api.Modules.Ports;

/// <summary>
/// Запись своих справочников модуля (см. <see cref="IModuleOwnCatalog" />, задача F3, issue #1087).
///
/// <para>Создание и удаление идут теми же командами, что и у двери «Общих данных»: охрана записи, отказ
/// удалить запись, на которую ссылаются документы, журнал правок — одни на всех. Переименование — прямо
/// репозиторием: команда правки перечитывает привязанные наборы правами пользователя, а у записи
/// справочника модуля меняется одно название, и наборов она не читает.</para>
/// </summary>
public sealed class ModuleOwnCatalogPort(
    IMediator mediator, IRepository<DocumentType> types, IRepository<DomainObject> objects) : IModuleOwnCatalog
{
    public async Task<ModuleCatalogRef?> CreateAsync(string typeCode, string displayName, CancellationToken ct = default)
    {
        if (await OwnTypeAsync(typeCode, ct) is not { } type) return null;

        using var data = JsonDocument.Parse("{}");
        var entry = await mediator.Send(new CreateCommonDataEntryCommand(
            displayName, type.Id, data, CatalogScope.System, null), ct);
        return new ModuleCatalogRef(entry.Id, type.Code, entry.DisplayName, entry.IsArchived);
    }

    public async Task<ModuleCatalogRef?> RenameAsync(
        string typeCode, Guid id, string displayName, CancellationToken ct = default)
    {
        if (await EntryAsync(typeCode, id, ct) is not { } found) return null;

        var (type, entry) = found;
        entry.Rename(displayName);
        await objects.SaveChangesAsync(ct);
        return new ModuleCatalogRef(entry.Id, type.Code, entry.DisplayName, entry.IsArchived);
    }

    public async Task<bool> DeleteAsync(string typeCode, Guid id, CancellationToken ct = default)
    {
        if (await EntryAsync(typeCode, id, ct) is null) return false;

        await mediator.Send(new DeleteCommonDataEntryCommand(id), ct);
        return true;
    }

    /// <summary>
    /// Запись ЭТОГО типа. ⚠️ Тип записи сверяется на каждом адресе с идентификатором, как у двери
    /// сотрудников: без этого узкое право модуля правило бы любую запись общих данных — организацию,
    /// единицу измерения, — то есть означало бы <c>core.catalog.edit</c>.
    /// </summary>
    private async Task<(DocumentType Type, DomainObject Entry)?> EntryAsync(string typeCode, Guid id, CancellationToken ct)
    {
        if (await OwnTypeAsync(typeCode, ct) is not { } type) return null;
        var entry = await objects.GetByIdAsync(id, ct);
        return entry is null || entry.IsDocument || entry.CompositeTypeId != type.Id ? null : (type, entry);
    }

    /// <summary>
    /// Тип с этим кодом; <c>null</c> — его нет (проекция при старте его пропустила, причина — в журнале
    /// запуска). Отказ с объяснением говорит модуль: он знает, что это значит на его экране.
    ///
    /// <para>Тип ядра или таблицы модуля — дефект вызывающего кода, а не отказ человеку: такие коды модуль
    /// называет в своём коде, а не получает из запроса.</para>
    /// </summary>
    private async Task<DocumentType?> OwnTypeAsync(string typeCode, CancellationToken ct)
    {
        var found = await types.FindAsync(t => t.Code == typeCode, ct);
        if (found.Count == 0) return null;

        var type = found[0];
        if (TypeOwner.IsCore(type.Module) || type.Storage != TypeStorage.SharedObject)
            throw new InvalidOperationException(
                $"Тип «{typeCode}» — не справочник модуля в общей таблице (владелец «{type.Module}», носитель " +
                $"«{type.Storage}»). Записи ядра и таблиц модулей этим портом не пишутся.");
        return type;
    }
}
