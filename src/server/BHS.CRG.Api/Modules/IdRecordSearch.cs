using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Recent;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Templates;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules.Access;
using BHS.CRG.Modules.Exchange;
using BHS.CRG.Modules.Search;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Группы «Поиска записей» модуля <c>id</c> — «Комплекты» и «Шаблоны» (shell-09; ТЗ ID-13, CORE-25.3,
/// CORE-34): модуль — владелец типов комплекта и шаблона и отдаёт их карточки «Недавним» Главной в правах
/// спрашивающего. Регистрируется модулем (<see cref="IdModule.RegisterServices" />), а не ядром: выключение
/// <c>id</c> (ТЗ ID-10) само убирает его группы (Р-1).
///
/// <para><b>Обе группы «только карточки»</b> (Р-2, решение владельца 13.11.2026 — Б): палитра их не ищет,
/// поиск ими — отдельный change. Поэтому <see cref="SearchAsync" /> ядром не зовётся вовсе.</para>
///
/// <para><b>Права — двумя поясами</b> (Р-3). Ключ группы — право чтения её записей,
/// <c>id.document.read</c> у обеих (<c>GET /api/templates</c> читается им же). Право ЭКРАНА записи —
/// другое, и его поставщик проверяет сам: без него группа отвечает «скрыто» и пропадает целиком, а не
/// копит у человека строку «не показано».</para>
/// </summary>
public sealed class IdRecordSearch(AppDbContext db) : IRecordSearch
{
    public const string SetsGroup = "sets";
    public const string TemplatesGroup = "templates";

    /// <summary>Ключ обеих групп — право чтения комплектов и шаблонов.</summary>
    private const string DocumentRead = "id.document.read";

    /// <summary>
    /// Право экрана группы — то же, каким закрыт пункт навигации экрана в клиенте
    /// (<c>src/client/src/shared/ui/navConfig.ts</c>): комплект открывается под «Стройками»
    /// (<c>/document-sets</c>), шаблон — пунктом «Шаблоны» (<c>/templates</c>). Расхождение с клиентом ловит
    /// перепись <c>recordScreenRights.test.ts</c>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ScreenRights = new Dictionary<string, string>
    {
        [SetsGroup] = CorePermissions.ConstructionsRead,
        [TemplatesGroup] = "id.config.edit",
    };

    public static readonly IReadOnlyList<RecordSearchGroup> Declared =
    [
        new(SetsGroup, "Комплекты", IdRecordTypes.DocumentSet, DocumentRead, RecordSearchArchive.None,
            Searchable: false),
        new(TemplatesGroup, "Шаблоны", IdRecordTypes.Template, DocumentRead, RecordSearchArchive.None,
            Searchable: false),
    ];

    public IReadOnlyList<RecordSearchGroup> Groups => Declared;

    /// <summary>
    /// Группы «только карточки» поиском не спрашиваются (<c>RecordSearchService</c>); ответ на случай
    /// вызова мимо службы — пустой, а не исключение: ничего не найдено честно.
    /// </summary>
    public Task<ContractAnswer<RecordSearchHits>> SearchAsync(
        string group, RecordSearchQuery query, ModuleReader reader, CancellationToken ct)
    {
        Own(group);
        return Task.FromResult(Refusal<RecordSearchHits>(group, reader)
            ?? ContractAnswer.Shown(new RecordSearchHits([], false)));
    }

    public async Task<ContractAnswer<IReadOnlyList<RecordSearchHit>>> CardsAsync(
        string group, IReadOnlyCollection<Guid> ids, ModuleReader reader, CancellationToken ct)
    {
        Own(group);
        if (Refusal<IReadOnlyList<RecordSearchHit>>(group, reader) is { } refused) return refused;
        return ContractAnswer.Shown(group == SetsGroup ? await SetsAsync(ids, ct) : await TemplatesAsync(ids, ct));
    }

    /// <summary>Сначала ключ группы, затем право экрана: без любого — «скрыто».</summary>
    private static ContractAnswer<T>? Refusal<T>(string group, ModuleReader reader) =>
        ContractAnswer.Refusal<T>(reader, DocumentRead) ?? ContractAnswer.Refusal<T>(reader, ScreenRights[group]);

    /// <summary>Комплекты одним запросом с разделом и стройкой: экран комплекта стоит под стройкой.</summary>
    private async Task<IReadOnlyList<RecordSearchHit>> SetsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var rows = await db.DocumentSets.AsNoTracking().Where(d => ids.Contains(d.Id))
            .Join(db.Sections, d => d.SectionId, s => s.Id, (d, s) => new { d.Id, d.Name, Section = s.Name, s.ConstructionId })
            .Join(db.Constructions, x => x.ConstructionId, c => c.Id,
                (x, c) => new { x.Id, x.Name, x.Section, ConstructionId = c.Id, Construction = c.Name })
            .ToListAsync(ct);
        return [.. rows.Select(r => new RecordSearchHit(r.Id, r.Name, Detail: r.Section, Place: r.Construction,
            Opens: new RecordSearchOpens(r.ConstructionId)))];
    }

    /// <summary>
    /// Карточка шаблона по любой отмеченной версии (Р-5 — Б): шаблон — все версии с одним
    /// <c>(DocumentTypeId, Name)</c>, ведёт карточка на текущую (<see cref="TemplateCurrentVersion" />).
    /// <c>Id</c> карточки — тот, о котором спросили: по нему «Недавние» находят отметку. Тип документа,
    /// который страница шаблонов не выбирает (не документ или абстрактный), карточки не даёт: ссылка открыла
    /// бы пустую страницу.
    /// </summary>
    private async Task<IReadOnlyList<RecordSearchHit>> TemplatesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var asked = await db.Templates.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Join(db.DocumentTypes, t => t.DocumentTypeId, d => d.Id,
                (t, d) => new { t.Id, t.Name, t.DocumentTypeId, Type = d.Name, d.Kind, d.IsAbstract })
            .Where(x => x.Kind == DocumentTypeKind.Document && !x.IsAbstract)
            .ToListAsync(ct);
        if (asked.Count == 0) return [];

        // Версии всех спрошенных шаблонов одним запросом, без содержимого. Отбор по типам и по именам — в
        // базе; пара (тип, имя) — в памяти: одноимённый шаблон другого спрошенного типа отсекается здесь.
        List<Guid> types = [.. asked.Select(a => a.DocumentTypeId).Distinct()];
        List<string> names = [.. asked.Select(a => a.Name).Distinct()];
        var versions = (await db.Templates.AsNoTracking()
                .Where(t => types.Contains(t.DocumentTypeId) && names.Contains(t.Name))
                .Select(t => new { t.DocumentTypeId, t.Name, t.Id, t.IsActive, t.IsDefault, t.Version, t.UpdatedAt })
                .ToListAsync(ct))
            .GroupBy(v => (v.DocumentTypeId, v.Name))
            .ToDictionary(g => g.Key, g => TemplateCurrentVersion.Of(
                g.Select(v => new TemplateVersionFacts(v.Id, v.IsActive, v.IsDefault, v.Version, v.UpdatedAt)))!);

        return [.. asked.Select(a =>
        {
            var current = versions[(a.DocumentTypeId, a.Name)];
            // Тип документа — место шаблона: его Главная показывает в строке, как стройку у комплекта.
            return new RecordSearchHit(a.Id, a.Name, Detail: $"v{current.Version}", Place: a.Type,
                Opens: new RecordSearchOpens(a.DocumentTypeId, current.Id));
        })];
    }

    private static void Own(string group)
    {
        if (group != SetsGroup && group != TemplatesGroup)
            throw new ArgumentException($"У модуля «id» нет группы поиска «{group}».", nameof(group));
    }
}
