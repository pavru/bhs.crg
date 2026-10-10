using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Documents;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Catalog;
using BHS.CRG.Domain.Common;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Infrastructure.Persistence;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Infrastructure.Documents;

/// <summary>
/// Создание позиции номенклатуры коротким окном и поиск похожих (задача C3, issue #1079).
///
/// <para>Своих чтений базы здесь нет: значения полей ключа отдаёт запрос ядра
/// (<see cref="CommonDataFieldValuesQuery" />), записи на выбор — поиск выбора. Своё здесь одно —
/// замок: сверка «такой ещё нет» и создание идут в одной транзакции.</para>
///
/// <para>⚠️ <b>Сверяются позиции ВСЕГО семейства «Номенклатуры»</b>, а не только выбранного вида:
/// «Кабель», заведённый рядом с такой же «Номенклатурой», — тот же дубль. Полями ключа служат поля
/// выбранного вида; у подтипа с собственными полями ключа лежащие записи сравниваются по ним же.</para>
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
                    refusals.Add($"справочник для обязательного поля «{field.Title}» пуст");
                fields.Add(field with { Options = known ?? [] });
            }
            kinds.Add(kind with { Fields = fields, Refusals = refusals });
        }
        return kinds;
    }

    public async Task<SimilarAnswer> SimilarAsync(
        Guid typeId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        var (all, family) = await FamilyAsync(ct);
        var type = family.FirstOrDefault(t => t.Id == typeId) ?? throw NotAKind();
        return await SimilarAsync(NomenclatureIntakeLayout.Describe(type, all), family, values, ct);
    }

    public async Task<NomenclatureIntakeOutcome> CreateAsync(NomenclatureIntakeRequest request, CancellationToken ct = default)
    {
        var kind = (await DescribeAsync(ct)).FirstOrDefault(k => k.TypeId == request.TypeId) ?? throw NotAKind();

        // Ссылка обязана стоять среди записей НА ВЫБОР: архивная либо чужого вида запись, присланная
        // мимо окна, иначе легла бы в данные как выбранная человеком.
        var picked = new Dictionary<string, IntakeOption>(StringComparer.Ordinal);
        foreach (var (key, id) in request.Refs)
        {
            var field = kind.Fields.FirstOrDefault(f => f.Key == key && f.TargetTypeId is not null);
            picked[key] = field is null
                ? new IntakeOption(id, null) // лишний ключ назовёт раскладка — своим отказом
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

            var (_, family) = await FamilyAsync(ct);
            if ((await SimilarAsync(kind, family, request.Values, ct)).Exact is { } twin)
                return new(null, twin);

            // CreateAnyway: «есть в архиве» уже сверено выше, и строже, чем это делает создание, —
            // с пустыми полями ключа. Второй отказ того же рода был бы недостижим.
            var created = await mediator.Send(new CreateCommonDataEntryCommand(
                name, request.TypeId, data, CatalogScope.System, null, CreateAnyway: true), ct);
            await journal.RecordAsync(
                ActivityActions.NomenclatureCreated, created.Id.ToString(), $"{name} ({kind.Name})", ct: ct);
            if (own is not null) await own.CommitAsync(ct);
            return new(created, null);
        }
    }

    private async Task<SimilarAnswer> SimilarAsync(
        IntakeKind kind, IReadOnlyList<DocumentType> family, IReadOnlyDictionary<string, string?> values,
        CancellationToken ct)
    {
        var identity = kind.Fields.Where(f => f.Identity).ToList();
        if (identity.Count == 0)
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

        return NomenclatureSimilarity.Find(
            [.. identity.Select(f => (f.Title, values.TryGetValue(f.Key, out var typed) ? typed : null))],
            // Нечитаемая запись в сверку не идёт: о ней нельзя сказать ни «совпала», ни «не совпала».
            [.. records.Values.Where(r => !unreadable.Contains(r.Ref.Id))
                .Select(r => new SimilarRecord(r.Ref.Id, r.Ref.CompositeTypeId, r.Ref.DisplayName, r.Ref.Archived, r.Key))],
            unreadable.Count);
    }

    /// <summary>Записи на выбор; <c>null</c> — их больше, чем окно может показать списком.</summary>
    private async Task<IReadOnlyList<IntakeOption>?> OptionsAsync(
        Guid target, IReadOnlyDictionary<Guid, DocumentType> all, CancellationToken ct)
    {
        var typeIds = all.Keys.Where(id => DocumentTypeSchemaReader.IsSameOrDescendant(id, target, all)).ToList();
        var found = await mediator.Send(
            new SearchCommonDataForChoiceQuery(typeIds, null, NomenclatureIntakeLayout.OptionsLimit + 1), ct);
        return found.Items.Count > NomenclatureIntakeLayout.OptionsLimit
            ? null
            : [.. found.Items.Select(r => new IntakeOption(r.Id, r.DisplayName))];
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
