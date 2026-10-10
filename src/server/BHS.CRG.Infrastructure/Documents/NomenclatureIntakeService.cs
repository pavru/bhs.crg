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
/// <para>Значения полей ключа отдаёт запрос ядра (<see cref="CommonDataFieldValuesQuery" />): он
/// разрешает наследование от основы. Своих чтений два, оба лёгкой строкой: записи на выбор в
/// поле-ссылке и альтернативные имена позиций. И замок: сверка «такой ещё нет» и создание идут в
/// одной транзакции.</para>
///
/// <para>⚠️ <b>Сверяются позиции ВСЕГО семейства «Номенклатуры»</b>, а не только выбранного вида:
/// «Кабель», заведённый рядом с такой же «Номенклатурой», — похожая позиция. Полями ключа служат
/// поля выбранного вида. Запись подтипа, у которой заполнены СВОИ поля ключа, «той же» не считается
/// (<see cref="SimilarRecord.OwnKey" />) — только похожей.</para>
///
/// <para>Цена сверки — по запросу на поле ключа, каждый читает все позиции семейства лёгкой строкой.
/// Позиций — тысячи, а спрашивает окно после паузы в наборе. Вырастет справочник на порядок —
/// сверку придётся переносить в базу.</para>
/// </summary>
public sealed class NomenclatureIntakeService(
    AppDbContext db, ISender mediator, IRepository<DocumentType> types, IActivityLog journal)
    : INomenclatureIntake
{
    public async Task<IReadOnlyList<IntakeKind>> DescribeAsync(CancellationToken ct = default)
    {
        var (all, family) = await FamilyAsync(ct);
        var options = new Dictionary<Guid, IReadOnlyList<IntakeOption>?>();

        var kinds = new List<IntakeKind>();
        // Корень семейства первым, подтипы — по названию: порядок списка «Вид» в окне.
        foreach (var type in family.OrderBy(t => t.ParentId is not null && family.Any(p => p.Id == t.ParentId))
                     .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase))
            kinds.Add(await DescribeAsync(type, all, options, ct));
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
        var kind = await DescribeAsync(type, all, [], ct);

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
        Dictionary<Guid, IReadOnlyList<IntakeOption>?> options, CancellationToken ct)
    {
        var kind = NomenclatureIntakeLayout.Describe(type, all);
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

    private async Task<SimilarAnswer> SimilarAsync(
        IntakeKind kind, IReadOnlyDictionary<Guid, DocumentType> all, IReadOnlyList<DocumentType> family,
        IReadOnlyDictionary<string, string?> values, CancellationToken ct)
    {
        var identity = kind.Fields.Where(f => f.Identity).ToList();
        if (identity.Count == 0 || kind.Refusals.Count > 0)
            throw new InvalidRequestException(
                $"Позиции вида «{kind.Name}» сверить с лежащими нечем: {string.Join("; ", kind.Refusals)}.");

        var typeIds = family.Select(t => t.Id).ToList();
        var records = new Dictionary<Guid, (CommonDataRef Ref, string?[] Key)>();
        var unreadable = new HashSet<Guid>();
        for (var i = 0; i < identity.Count; i++)
        {
            // Показ, а не выбор: архивный двойник — тоже двойник, и окно о нём говорит.
            foreach (var value in await mediator.Send(
                         new CommonDataFieldValuesQuery(typeIds, identity[i].Key, RecordsFor.Display), ct))
            {
                if (!records.TryGetValue(value.Record.Id, out var known))
                    records[value.Record.Id] = known = (value.Record, new string?[identity.Count]);
                known.Key[i] = value.Value;
                if (value.Unreadable) unreadable.Add(value.Record.Id);
            }
        }

        var ownKey = await OwnKeyAsync(identity, all, family, unreadable, ct);
        var aliases = await AliasesAsync(typeIds, ct);

        return NomenclatureSimilarity.Find(
            [.. identity.Select(f => (f.Title, values.TryGetValue(f.Key, out var typed) ? typed : null))],
            // Нечитаемая запись в сверку не идёт: о ней нельзя сказать ни «совпала», ни «не совпала».
            [.. records.Values.Where(r => !unreadable.Contains(r.Ref.Id))
                .Select(r => new SimilarRecord(
                    r.Ref.Id, r.Ref.CompositeTypeId, all.GetValueOrDefault(r.Ref.CompositeTypeId)?.Name ?? "",
                    r.Ref.DisplayName, r.Ref.Archived, r.Key,
                    aliases.GetValueOrDefault(r.Ref.Id) ?? [], ownKey.Contains(r.Ref.Id)))],
            unreadable.Count);
    }

    /// <summary>
    /// Записи, у которых заполнены поля ключа их СОБСТВЕННОГО подтипа, не входящие в ключ выбранного
    /// вида. Обычно таких полей нет вовсе, и запросов здесь ноль.
    /// </summary>
    private async Task<HashSet<Guid>> OwnKeyAsync(
        IReadOnlyList<IntakeField> identity, IReadOnlyDictionary<Guid, DocumentType> all,
        IReadOnlyList<DocumentType> family, HashSet<Guid> unreadable, CancellationToken ct)
    {
        var shared = identity.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        var everything = all.Values.ToList();
        var typesByKey = family
            .SelectMany(t => SchemaTags.OrderedKeysWithTag(t, everything, FunctionalTag.Identity)
                .Where(k => !shared.Contains(k)).Select(k => (Key: k, Type: t.Id)))
            .GroupBy(x => x.Key, x => x.Type);

        var own = new HashSet<Guid>();
        foreach (var group in typesByKey)
            foreach (var value in await mediator.Send(
                         new CommonDataFieldValuesQuery([.. group.Distinct()], group.Key, RecordsFor.Display), ct))
            {
                if (value.Unreadable) unreadable.Add(value.Record.Id);
                else if (!string.IsNullOrWhiteSpace(value.Value)) own.Add(value.Record.Id);
            }
        return own;
    }

    /// <summary>Альтернативные имена позиций семейства — только тех, у кого они есть.</summary>
    private async Task<Dictionary<Guid, List<string>>> AliasesAsync(IReadOnlyCollection<Guid> typeIds, CancellationToken ct) =>
        await db.DomainObjects.AsNoTracking()
            .Where(o => o.Facet == null && typeIds.Contains(o.CompositeTypeId) && o.Aliases.Any())
            .Select(o => new { o.Id, o.Aliases })
            .ToDictionaryAsync(o => o.Id, o => o.Aliases, ct);

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
        var root = all.Values.FirstOrDefault(t => t.Code == CoreRecordTypes.NomenclatureCode)
            ?? throw new ConflictException(
                $"Тип «{CoreRecordTypes.NomenclatureCode}» в системе не заведён, поэтому заводить позицию " +
                "некуда. Справочник появляется вместе с материалами: миграция ядра поднимает «Номенклатуру» " +
                "над «Материалом» там, где материалы есть.");
        return (all, [.. all.Values.Where(t => DocumentTypeSchemaReader.IsSameOrDescendant(t.Id, root.Id, all))]);
    }

    private static NotFoundException NotAKind() =>
        new("Такого вида позиции номенклатуры нет: тип не «Номенклатура» и не её подтип.");
}
