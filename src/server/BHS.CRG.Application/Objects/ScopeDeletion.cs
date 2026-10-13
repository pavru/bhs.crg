using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Objects;

/// <summary>
/// Принудительное удаление уровня — стройки, раздела или комплекта, — на содержимое которого
/// ссылаются только данные выключенных или снятых модулей (issue #1243).
///
/// <para>Подтверждается ОБЩИМ числом ссылок на всё, что уходит с уровнем (решение владельца
/// 13.10.2026, то же, что у пакета #1245): держать могут разные записи поддерева, и число «по
/// записям» человеку сверять было бы не с чем.</para>
/// </summary>
/// <param name="Level">Какой уровень удаляют. <c>System</c> не удаляется — такой команды нет.</param>
/// <param name="References">Число теряемых ссылок, как его увидел человек.</param>
public sealed record PurgeHeldScopeCommand(CatalogScope Level, Guid Id, int References) : IRequest<PurgedScope>;

/// <summary>Что удалено и что при этом потеряно.</summary>
public sealed record PurgedScope(string Name, int References, int Untraceable);

/// <summary>
/// Удаление уровня — ОДНО тело на три уровня и на оба пути, обычный и принудительный (issue #1243).
///
/// <para>Раньше удаление жило тремя копиями — у стройки, у раздела и у комплекта. Принудительный
/// путь удвоил бы каждую, а отличается он от обычного одним: вопросом к держателям в модулях.
/// Поэтому вопрос передаётся действием, как у записи (<c>CommonDataHandlers.DeleteAsync</c>): отказ
/// ядра, дописанный в каскад позже, встаёт на оба пути сам, и обойти его нечем.</para>
///
/// <para>Разделы и комплекты уносит каскад базы, объекты на полиморфной оси — нет: их удаляем
/// прикладно (<see cref="IScopeCascade" />), иначе документы и общие данные поддерева остаются
/// сиротами (issue #739).</para>
/// </summary>
public sealed class ScopeDeletionHandlers(
    IRepository<Construction> constructionRepo,
    IRepository<Section> sectionRepo,
    IRepository<DocumentSet> setRepo,
    IScopeCascade cascade,
    IActivityLog journal) :
    IRequestHandler<DeleteConstructionCommand>,
    IRequestHandler<DeleteSectionCommand>,
    IRequestHandler<DeleteDocumentSetCommand>,
    IRequestHandler<PurgeHeldScopeCommand, PurgedScope>
{
    public Task Handle(DeleteConstructionCommand cmd, CancellationToken ct) =>
        DeleteAsync(CatalogScope.Construction, cmd.Id, Plain, ct);

    public Task Handle(DeleteSectionCommand cmd, CancellationToken ct) =>
        DeleteAsync(CatalogScope.Section, cmd.Id, Plain, ct);

    public Task Handle(DeleteDocumentSetCommand cmd, CancellationToken ct) =>
        DeleteAsync(CatalogScope.Set, cmd.Id, Plain, ct);

    /// <summary>Обычное удаление: любые держатели в модулях — отказ, терять нечего.</summary>
    private bool Plain(ScopeCascadePlan plan, string what)
    {
        cascade.EnsureDeletable(plan, what);
        return true;
    }

    public async Task<PurgedScope> Handle(PurgeHeldScopeCommand cmd, CancellationToken ct)
    {
        var (name, plan, release) = await DeleteAsync(
            cmd.Level, cmd.Id, (found, what) => cascade.EnsureReleasable(found, what, cmd.References), ct);

        // Журнал — после удаления и без токена отмены запроса (см. IActivityLog): уровень уже
        // удалён, и оборванный запрос не должен оставить потерянные ссылки без следа. Адрес колонки
        // пишется там, где потерю потом не покажет никто. Состав унесённого — тоже сюда: иначе
        // масштаб удаления из журнала не восстановить, а больше ему записаться негде.
        await journal.RecordAsync(
            ActivityActions.ScopePurged, cmd.Id.ToString(), $"{Words(cmd.Level).Title}: {name}",
            before: string.Join("; ", release.Holders.Select(h =>
                $"{h.Owner}: {h.What} — {h.Rows}" + (h.Traceable ? "" : $" ({h.Address})"))),
            after: $"потеряно ссылок: {release.References}; удалено вместе: {plan.Leaving.Describe()}",
            ct: CancellationToken.None);

        return new PurgedScope(name, release.References, release.Untraceable);
    }

    /// <param name="gate">
    /// Вопрос к плану — единственное, чем пути различаются: обычному любые держатели в модулях —
    /// отказ, принудительный пропускает выключенные, сверив число. Действием, а не признаком.
    /// </param>
    private async Task<(string Name, ScopeCascadePlan Plan, T Passed)> DeleteAsync<T>(
        CatalogScope level, Guid id, Func<ScopeCascadePlan, string, T> gate, CancellationToken ct)
    {
        var (name, remove) = level switch
        {
            CatalogScope.Construction when await constructionRepo.GetByIdAsync(id, ct) is { } c =>
                (c.Name, (Action)(() => constructionRepo.Remove(c))),
            CatalogScope.Section when await sectionRepo.GetByIdAsync(id, ct) is { } s =>
                (s.Name, () => sectionRepo.Remove(s)),
            CatalogScope.Set when await setRepo.GetByIdAsync(id, ct) is { } set =>
                (set.Name, () => setRepo.Remove(set)),
            _ => throw new NotFoundException(),
        };

        var plan = await cascade.PlanAsync(level, id, ct);
        var passed = gate(plan, Words(level).Accusative);

        cascade.Remove(plan);
        remove();
        // Хранилища делят один контекст: сохранение уносит и содержимое, и сам уровень одной
        // транзакцией — отказ на второй половине не оставит стройку без документов.
        await setRepo.SaveChangesAsync(ct);
        return (name, plan, passed);
    }

    private static (string Title, string Accusative) Words(CatalogScope level) => level switch
    {
        CatalogScope.Construction => ("Стройка", "стройку"),
        CatalogScope.Section => ("Раздел", "раздел"),
        _ => ("Комплект", "комплект"),
    };
}
