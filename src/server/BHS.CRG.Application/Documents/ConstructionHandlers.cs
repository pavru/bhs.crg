using BHS.CRG.Application.Common;
using BHS.CRG.Application.Objects;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Documents;
using MediatR;

namespace BHS.CRG.Application.Documents;

public class ConstructionHandlers(
    IRepository<Construction> constructionRepo,
    IRepository<Section> sectionRepo,
    IScopeCascade cascade) :
    IRequestHandler<CreateConstructionCommand, Construction>,
    IRequestHandler<RenameConstructionCommand, Construction>,
    IRequestHandler<SetConstructionTimeZoneCommand, Construction>,
    IRequestHandler<SetConstructionExternalIdCommand, Construction>,
    IRequestHandler<DeleteConstructionCommand>,
    IRequestHandler<GetConstructionQuery, Construction?>,
    IRequestHandler<ListConstructionsQuery, IReadOnlyList<Construction>>,
    IRequestHandler<GetSectionQuery, Section?>,
    IRequestHandler<CreateSectionCommand, Section>,
    IRequestHandler<RenameSectionCommand, Section>,
    IRequestHandler<DeleteSectionCommand>
{
    public async Task<Construction> Handle(CreateConstructionCommand cmd, CancellationToken ct)
    {
        var c = Construction.Create(cmd.Name, cmd.UserId);
        await constructionRepo.AddAsync(c, ct);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    public async Task<Construction> Handle(RenameConstructionCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        c.Rename(cmd.Name);
        constructionRepo.Update(c);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Часовой пояс стройки (ТЗ CORE-5). Пустое значение СНИМАЕТ свой пояс — стройка снова считает
    /// сутки по поясу компании; проверяет идентификатор вызывающий, здесь он уже разобран.
    /// </summary>
    public async Task<Construction> Handle(SetConstructionTimeZoneCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        c.SetTimeZone(cmd.TimeZoneId);
        constructionRepo.Update(c);
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Внешний идентификатор стройки (ТЗ CORE-5).
    ///
    /// <para>⚠️ Всё, что можно отвергнуть ДО базы, отвергается здесь. Иначе длинный код упирался бы
    /// в ширину колонки (22001), а повтор пары — в уникальный индекс (23505), и оба возвращались бы
    /// пятисоткой: <c>ApiErrorMapping</c> разбирает только наши отказы, а прочее прячет целиком,
    /// чтобы наружу не уехали хост и имя базы. То есть ошибка вызывающего выглядела бы поломкой
    /// сервера (ревью PR #1046 — та же находка, что уже была у создания комплекта в PR #1045).</para>
    /// </summary>
    public async Task<Construction> Handle(SetConstructionExternalIdCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();

        if (cmd.System is { Length: > 128 })
            throw new InvalidRequestException("Название системы-источника длиннее 128 символов.");
        if (cmd.Code is { Length: > 256 })
            throw new InvalidRequestException("Код в системе-источнике длиннее 256 символов.");

        // Половину пары отвергает сам домен — нашим типом отказа, то есть текстом, который дойдёт
        // до вызывающего (см. DomainExceptionPolicyTests).
        c.SetExternalId(cmd.System, cmd.Code);

        if (c.ExternalSystem is not null)
        {
            var taken = await constructionRepo.FindAsync(
                x => x.Id != c.Id && x.ExternalSystem == c.ExternalSystem && x.ExternalCode == c.ExternalCode, ct);
            if (taken.Count > 0)
                throw new ConflictException(
                    $"Код «{c.ExternalCode}» в системе «{c.ExternalSystem}» уже занят стройкой «{taken[0].Name}». " +
                    "Пара «система + код» адресует ровно одну стройку: иначе следующая загрузка извне " +
                    "выбрала бы любую из них.");
        }

        constructionRepo.Update(c);
        // Гонку (пара занята между проверкой и записью) ловит АДРЕС: здесь, в прикладном слое, нет
        // ни EF, ни Npgsql — а различить 23505 нечем иначе. См. ConstructionEndpoints.
        await constructionRepo.SaveChangesAsync(ct);
        return c;
    }

    /// <summary>
    /// Удаление стройки. Разделы и комплекты уносит каскад базы, объекты на полиморфной оси — нет:
    /// их удаляем прикладно, иначе документы и общие данные всего поддерева остаются сиротами
    /// (issue #739). Guard тот же, что у поштучного удаления: держатели ссылок ИЗВНЕ поддерева.
    /// </summary>
    public async Task Handle(DeleteConstructionCommand cmd, CancellationToken ct)
    {
        var c = await constructionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        var plan = await cascade.PlanAsync(CatalogScope.Construction, cmd.Id, ct);
        cascade.EnsureDeletable(plan, "стройку");
        cascade.Remove(plan);
        constructionRepo.Remove(c);
        await constructionRepo.SaveChangesAsync(ct);
    }

    public Task<Construction?> Handle(GetConstructionQuery q, CancellationToken ct)
        => constructionRepo.GetByIdAsync(q.Id, ct);

    public Task<IReadOnlyList<Construction>> Handle(ListConstructionsQuery q, CancellationToken ct)
        => constructionRepo.GetAllAsync(ct);

    public Task<Section?> Handle(GetSectionQuery q, CancellationToken ct)
        => sectionRepo.GetByIdAsync(q.Id, ct);

    public async Task<Section> Handle(CreateSectionCommand cmd, CancellationToken ct)
    {
        _ = await constructionRepo.GetByIdAsync(cmd.ConstructionId, ct)
            ?? throw new NotFoundException("Construction not found");
        var section = Section.Create(cmd.ConstructionId, cmd.Name);
        await sectionRepo.AddAsync(section, ct);
        await sectionRepo.SaveChangesAsync(ct);
        return section;
    }

    public async Task<Section> Handle(RenameSectionCommand cmd, CancellationToken ct)
    {
        var s = await sectionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        s.Rename(cmd.Name);
        sectionRepo.Update(s);
        await sectionRepo.SaveChangesAsync(ct);
        return s;
    }

    /// <inheritdoc cref="Handle(DeleteConstructionCommand, CancellationToken)" />
    public async Task Handle(DeleteSectionCommand cmd, CancellationToken ct)
    {
        var s = await sectionRepo.GetByIdAsync(cmd.Id, ct) ?? throw new NotFoundException();
        var plan = await cascade.PlanAsync(CatalogScope.Section, cmd.Id, ct);
        cascade.EnsureDeletable(plan, "раздел");
        cascade.Remove(plan);
        sectionRepo.Remove(s);
        await sectionRepo.SaveChangesAsync(ct);
    }
}
