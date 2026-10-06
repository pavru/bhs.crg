using BHS.CRG.Application.DataSets;
using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
using BHS.CRG.Application.Objects;
using BHS.CRG.Application.Schema;
using BHS.CRG.Domain.Documents;
using BHS.CRG.Domain.Objects;
using MediatR;

namespace BHS.CRG.Application.Documents;

/// <summary>
/// Проверка связок наборов данных объекта общих данных (issue #99, PR-2). Сверяет СНИМОК ссылок в Data
/// со СВЕЖИМ резолвом источника по каждому составному (@@ref) полю. Статусы:
/// matched (снимок = свежий и запись жива), not-found (значение источника не сматчилось),
/// dangling (запись каталога удалена), drift (источник теперь указывает на ДРУГУЮ запись — снимок id устарел),
/// stale (снимок не {$ref} — легаси «🔗…» — но источник матчится: пересохранить),
/// archived (цель в архиве, issue #1185: связка стоит и работает — чинить нечего),
/// archived-skipped (источник называет архивную запись, которой в поле не было: ссылка НЕ
/// подставлена и поле не заполняется — не «не найдено», чинится возвратом из архива),
/// error (резолв привязки не состоялся вовсе — источник недоступен либо материализация без маппинга).
///
/// <para>Ошибки резолва берём наравне с предупреждениями (issue #715). Раньше отбирались только
/// предупреждения, и всё, что резолвер сообщал уровнем Error, до этого экрана не доходило: и
/// «источник данных недоступен», и материализация с пустым маппингом. То есть поле переставало
/// заполняться, а проверка связок отвечала «всё в порядке» — худший из возможных ответов.</para>
/// </summary>
public record BindingCheckItem(string FieldKey, string FieldTitle, string Status, string? LinkedName, string? Detail);
public record BindingCheckResult(IReadOnlyList<BindingCheckItem> Items);

public record CheckCommonDataBindingsQuery(Guid Id, DataAccess Access) : IRequest<BindingCheckResult>;

public class CheckCommonDataBindingsHandler(
    IRepository<DomainObject> repo,
    IRepository<DocumentType> docTypeRepo,
    IDataSetResolver dataSetResolver) : IRequestHandler<CheckCommonDataBindingsQuery, BindingCheckResult>
{
    public async Task<BindingCheckResult> Handle(CheckCommonDataBindingsQuery q, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(q.Id, ct) ?? throw new NotFoundException();

        var diag = new List<ResolutionDiagnostic>();
        // Тот же набор стоявших ссылок, что у сохранения: проверка обязана показать то, что
        // сохранение сделает, а не более щедрый ответ.
        var fresh = await dataSetResolver.ResolveOwnerBindingsAsync(
            q.Id, entry.CompositeTypeId, entry.ScopeLevel, entry.ScopeId,
            CatalogRefs.IdsIn(entry.Data.RootElement), q.Access, diag, ct);

        var allTypes = (await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        var titles = DocumentTypeSchemaReader.EffectiveFields(entry.CompositeTypeId, allTypes)
            .ToDictionary(f => f.Key, f => f.Title ?? f.Key);
        string Title(string key) => titles.TryGetValue(key, out var t) ? t : key;

        // Цель связки: её название и состояние. null — записи нет.
        var targets = new Dictionary<string, DomainObject?>();
        async Task<DomainObject?> TargetAsync(string entryId)
        {
            if (targets.TryGetValue(entryId, out var cached)) return cached;
            return targets[entryId] = Guid.TryParse(entryId, out var g) ? await repo.GetByIdAsync(g, ct) : null;
        }
        async Task<string?> NameAsync(string entryId) => (await TargetAsync(entryId))?.DisplayName;

        // Стоящая связка: цель удалена, в архиве либо на месте.
        BindingCheckItem Standing(string field, DomainObject? target) => target switch
        {
            null => new BindingCheckItem(field, Title(field), "dangling", null, "Целевая запись каталога удалена."),
            { IsArchived: true } => new BindingCheckItem(field, Title(field), "archived", target.DisplayName,
                "Запись в архиве: связка сохранена и в документах работает. В выборе этой записи нет."),
            _ => new BindingCheckItem(field, Title(field), "matched", target.DisplayName, null),
        };

        var items = new List<BindingCheckItem>();
        var handled = new HashSet<string>();
        var stored = entry.Data.RootElement;

        // 1) Проблемы резолва: not-found — значение источника не нашлось в каталоге; error — резолв
        // привязки не состоялся вовсе (источник недоступен, материализация без маппинга).
        // Архив — отдельно от «не найдено»: запись ЕСТЬ, и искать пропавшую незачем. Стоявшая
        // ссылка на архивную запись сюда не попадает вовсе — её покажет сравнение со снимком ниже.
        foreach (var d in diag)
        {
            if (d.Code == ArchivedRefCodes.Kept || !handled.Add(d.Path)) continue;
            var status = d.Code == ArchivedRefCodes.Skipped ? "archived-skipped"
                : d.Severity == DiagnosticSeverity.Error ? "error" : "not-found";
            items.Add(new BindingCheckItem(d.Path, Title(d.Path), status, null, d.Message));
        }

        // 2) свежие ссылки → сравнить со снимком.
        foreach (var (field, value) in fresh)
        {
            var freshId = RefId(value);
            if (freshId is null || !handled.Add(field)) continue;

            var freshName = await NameAsync(freshId);
            var storedId = stored.TryGetProperty(field, out var sv) ? RefIdJson(sv) : null;

            if (storedId == freshId)
                items.Add(Standing(field, await TargetAsync(freshId)));
            else if (storedId is not null)
                items.Add(new BindingCheckItem(field, Title(field), "drift", await NameAsync(storedId),
                    $"Источник теперь указывает на «{freshName ?? "(удалена)"}» — пересохраните для обновления."));
            else
                items.Add(new BindingCheckItem(field, Title(field), "stale", freshName,
                    "Сохранённое значение устарело (нет структурной ссылки) — пересохраните."));
        }

        // 3) dangling: снимок несёт {$ref}, а свежий резолв его не дал (источник убран / поле вне маппинга).
        foreach (var prop in stored.EnumerateObject())
        {
            var storedId = RefIdJson(prop.Value);
            if (storedId is null || !handled.Add(prop.Name)) continue;
            items.Add(Standing(prop.Name, await TargetAsync(storedId)));
        }

        return new BindingCheckResult(items.OrderBy(i => i.FieldTitle).ToList());
    }

    private static string? RefId(object? v) =>
        v is IDictionary<string, object?> d && d.TryGetValue("$ref", out var r) && r as string == "catalog"
            && d.TryGetValue("entryId", out var e) ? e as string : null;

    private static string? RefIdJson(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("$ref", out var r) && r.GetString() == "catalog"
            && el.TryGetProperty("entryId", out var e) ? e.GetString() : null;
}
