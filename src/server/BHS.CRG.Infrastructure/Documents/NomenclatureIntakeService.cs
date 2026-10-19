using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Schema;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Documents;

/// <summary>
/// Создание позиции номенклатуры коротким окном и поиск похожих (задача C3, issue #1079).
///
/// <para>Лежащие позиции читает общий загрузчик сверки (<see cref="ISimilarityRecordsLoader" />, issue
/// #1514) — тот же, что у подбора для строки бумаги. Своё чтение одно, лёгкой строкой: записи на выбор
/// в поле-ссылке. И замок: сверка «такой ещё нет» и создание идут в одной транзакции.</para>
///
/// <para>⚠️ <b>Сверяются позиции ВСЕГО семейства «Номенклатуры»</b>, а не только выбранного вида:
/// «Кабель», заведённый рядом с такой же «Номенклатурой», — похожая позиция. Полями ключа служат
/// поля выбранного вида. Запись подтипа, у которой заполнены СВОИ поля ключа, «той же» не считается
/// (<see cref="SimilarRecord.OwnKey" />) — только похожей.</para>
///
/// <para>Спрашивает окно после паузы в наборе; цена сверки названа у загрузчика.</para>
/// </summary>
public sealed class NomenclatureIntakeService(
    AppDbContext db, ISender mediator, IRepository<DocumentType> types, IActivityLog journal, TagCatalog tags,
    ISimilarityRecordsLoader records)
    : INomenclatureIntake
{
    public async Task<IReadOnlyList<IntakeKind>> DescribeAsync(CancellationToken ct = default)
    {
        var (all, family) = await FamilyAsync(ct);
        var options = new Dictionary<Guid, IReadOnlyList<IntakeOption>?>();
        // Тэги — один расчёт на все виды: справочник один, и поле роли у всей семьи одно.
        var tagged = Tagged(all);

        var kinds = new List<IntakeKind>();
        // Корень семейства первым, подтипы — по названию: порядок списка «Вид» в окне.
        foreach (var type in family.OrderBy(t => t.ParentId is not null && family.Any(p => p.Id == t.ParentId))
                     .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            kinds.Add(await DescribeAsync(type, all, options, tagged, ct));
        return kinds;
    }

    public async Task<SimilarAnswer> SimilarAsync(
        Guid typeId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        var (all, family) = await FamilyAsync(ct);
        var type = family.FirstOrDefault(t => t.Id == typeId) ?? throw NotAKind();
        // Записи на выбор сверке не нужны — описание берётся без них.
        return await SimilarAsync(NomenclatureIntakeLayout.Describe(type, all), all, family, values, ct);
    }

    public async Task<NomenclatureIntakeOutcome> CreateAsync(NomenclatureIntakeRequest request, CancellationToken ct = default)
    {
        var (all, family) = await FamilyAsync(ct);
        var type = family.FirstOrDefault(t => t.Id == request.TypeId) ?? throw NotAKind();
        // Описывается только запрошенный вид: остальные виды и их справочники созданию ни к чему.
        // Тэги созданию не нужны: они только для подстановки в окне.
        var kind = await DescribeAsync(type, all, [], null, ct);

        // Ссылка обязана стоять среди записей НА ВЫБОР: архивная либо чужого вида запись, присланная
        // мимо окна, иначе легла бы в данные как выбранная человеком.
        var picked = new Dictionary<string, IntakeOption>(StringComparer.Ordinal);
        foreach (var (key, id) in request.Refs)
        {
            var field = kind.Fields.FirstOrDefault(f => f.Key == key && f.TargetTypeId is not null);
            picked[key] = field is null
                ? new IntakeOption(id, null) // лишний ключ и ключ не того рода назовёт раскладка — отказом
                : field.Options.FirstOrDefault(o => o.Id == id)
                  ?? throw new InvalidRequestException(
                      $"В поле «{field.Title}» выбрана запись, которой нет среди действующих записей справочника.");
        }

        var (data, name) = NomenclatureIntakeLayout.Build(kind, request.Values, picked);
        using (data)
        {
            await using var own = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct)
                : null;
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryLockKeys.NomenclatureIntake})", ct);

            if ((await SimilarAsync(kind, all, family, request.Values, ct)).Exact is { } twin)
                return new(null, null, twin);

            // CreateAnyway: «есть в архиве» уже сверено выше, и строже, чем это делает создание, —
            // с пустыми полями ключа. Полон ли ключ, решило описание: вид, у которого поле ключа
            // человеку не заполнить, сюда не доходит.
            var created = await mediator.Send(new CreateCommonDataEntryCommand(
                name, request.TypeId, data, CatalogScope.System, null, CreateAnyway: true), ct);
            await journal.RecordAsync(
                ActivityActions.NomenclatureCreated, created.Id.ToString(), $"{name} ({kind.Name})", ct: ct);
            if (own is not null) await own.CommitAsync(ct);
            return new(created, kind.Name, null);
        }
    }

    /// <summary>Описание вида вместе с записями на выбор; справочники одного запроса читаются один раз.</summary>
    private async Task<IntakeKind> DescribeAsync(
        DocumentType type, IReadOnlyDictionary<Guid, DocumentType> all,
        Dictionary<Guid, IReadOnlyList<IntakeOption>?> options, IReadOnlyDictionary<string, string>? tagged,
        CancellationToken ct)
    {
        var kind = NomenclatureIntakeLayout.Describe(type, all, tagged);
        var fields = new List<IntakeField>();
        var refusals = kind.Refusals.ToList();
        foreach (var field in kind.Fields)
        {
            if (field.TargetTypeId is not { } target) { fields.Add(field); continue; }
            if (!options.TryGetValue(target, out var known))
                options[target] = known = await OptionsAsync(target, all, ct);
            if (known is null)
                refusals.Add($"поле «{field.Title}» выбирается из справочника длиннее " +
                    $"{NomenclatureIntakeLayout.OptionsLimit} записей — в этом окне его не заполнить");
            else if (known.Count == 0 && field.Required)
                refusals.Add($"в справочнике для обязательного поля «{field.Title}» нет записей уровня всей системы");
            fields.Add(field with { Options = known ?? [] });
        }
        return kind with { Fields = fields, Refusals = refusals };
    }

    /// <summary>
    /// Поля справочника, сопоставленные тэгами артикула и единицы (issue #1512), — тем же расчётом,
    /// что у порта модулей: окно, узнавшее поле иначе, чем карточка, подставило бы артикул в одно поле,
    /// а сравнивало по другому.
    ///
    /// <para>Оба тэга на ОДНОМ поле — противоречие: поле не бывает и артикулом, и единицей. Тогда не
    /// сопоставлен ни один, и окно ведёт себя как на неразмеченном справочнике, а не выбирает тэг молча.</para>
    /// </summary>
    private Dictionary<string, string> Tagged(IReadOnlyDictionary<Guid, DocumentType> all)
    {
        var list = all.Values.ToList();
        var role = RoleResolution.Role(FunctionalTag.RoleNomenclature, list);
        var found = ((string[])[FunctionalTag.NomenclatureArticle, FunctionalTag.NomenclatureUnit])
            .Select(code => (Code: code, Key: tags.Find(code) is { } tag ? RoleResolution.Field(role, tag, list).Key : null))
            .Where(x => x.Key is not null)
            .ToList();
        return found.GroupBy(x => x.Key!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Code, StringComparer.Ordinal);
    }

    private async Task<SimilarAnswer> SimilarAsync(
        IntakeKind kind, IReadOnlyDictionary<Guid, DocumentType> all, IReadOnlyList<DocumentType> family,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var identity = kind.Fields.Where(f => f.Identity).ToList();
        if (identity.Count == 0 || kind.Refusals.Count > 0)
            throw new InvalidRequestException(
                $"Позиции вида «{kind.Name}» сверить с лежащими нечем: {string.Join("; ", kind.Refusals)}.");

        // Тем же загрузчиком, что подбор для строки бумаги (issue #1514): окно не скажет «похожих нет» о
        // позиции, которую подбор находит.
        var lying = await records.LoadAsync(all, family, [.. identity.Select(f => f.Key)], ct);
        return NomenclatureSimilarity.Find(
            [.. identity.Select(f => (f.Title, values.TryGetValue(f.Key, out var typed) ? typed : null))],
            lying.Records, lying.Unreadable);
    }

    /// <summary>
    /// Записи на выбор; <c>null</c> — их больше, чем окно может показать списком.
    ///
    /// <para>⚠️ Только уровня ВСЕЙ СИСТЕМЫ и только действующие. Позиция заводится на уровне системы,
    /// и ссылка из неё на запись одной стройки в документах другой стройки не разрешилась бы: единица
    /// в документе оказалась бы пустой, а в справочнике позиция выглядела бы заполненной.</para>
    /// </summary>
    private async Task<IReadOnlyList<IntakeOption>?> OptionsAsync(
        Guid target, IReadOnlyDictionary<Guid, DocumentType> all, CancellationToken ct)
    {
        var typeIds = all.Keys.Where(id => DocumentTypeSchemaReader.IsSameOrDescendant(id, target, all)).ToList();
        var found = await db.DomainObjects.AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId) && o.ScopeLevel == CatalogScope.System && o.ArchivedAt == null)
            .OrderBy(o => o.DisplayName).ThenBy(o => o.Id)
            .Take(NomenclatureIntakeLayout.OptionsLimit + 1)
            .Select(o => new IntakeOption(o.Id, o.DisplayName))
            .ToListAsync(ct);
        return found.Count > NomenclatureIntakeLayout.OptionsLimit ? null : found;
    }

    /// <summary>Все типы и семейство «Номенклатуры». Типа нет — отказ с причиной, а не пустой список.</summary>
    private async Task<(IReadOnlyDictionary<Guid, DocumentType> All, IReadOnlyList<DocumentType> Family)> FamilyAsync(
        CancellationToken ct)
    {
        var all = (await types.GetAllAsync(ct)).ToDictionary(t => t.Id);
        // По РОЛИ, а не по коду типа (issue #1101) — тем же расчётом, каким номенклатуру находит порт
        // модулей. Иначе у одного справочника было бы два адреса, и разошлись бы они в день, когда
        // роль назначат типу с другим кодом: строка счёта выбирала бы позиции одного типа, а окно
        // заведения клало бы новую в другой.
        var root = RoleResolution.Role(FunctionalTag.RoleNomenclature, [.. all.Values]).Root
            ?? throw new ConflictException(
                "Справочник номенклатуры не назначен, поэтому заводить позицию некуда: роль «Справочник " +
                "номенклатуры» не несёт ни один тип либо несут несколько. Назначает её администратор — " +
                "тэгом типа в редакторе типов.");
        return (all, [.. all.Values.Where(t => DocumentTypeSchemaReader.IsSameOrDescendant(t.Id, root.Id, all))]);
    }

    private static NotFoundException NotAKind() =>
        new("Такого вида позиции номенклатуры нет: тип не «Номенклатура» и не её подтип.");
}
