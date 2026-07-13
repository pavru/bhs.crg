using System.Text.Json;
using BHS.CRG.Application.Common;
using BHS.CRG.Application.Generation;
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
/// stale (снимок не {$ref} — легаси «🔗…» — но источник матчится: пересохранить).
/// </summary>
public record BindingCheckItem(string FieldKey, string FieldTitle, string Status, string? LinkedName, string? Detail);
public record BindingCheckResult(IReadOnlyList<BindingCheckItem> Items);

public record CheckCommonDataBindingsQuery(Guid Id) : IRequest<BindingCheckResult>;

public class CheckCommonDataBindingsHandler(
    IRepository<DomainObject> repo,
    IRepository<DocumentType> docTypeRepo,
    IDataSetResolver dataSetResolver) : IRequestHandler<CheckCommonDataBindingsQuery, BindingCheckResult>
{
    public async Task<BindingCheckResult> Handle(CheckCommonDataBindingsQuery q, CancellationToken ct)
    {
        var entry = await repo.GetByIdAsync(q.Id, ct) ?? throw new KeyNotFoundException();

        var diag = new List<ResolutionDiagnostic>();
        var fresh = await dataSetResolver.ResolveOwnerBindingsAsync(
            q.Id, entry.CompositeTypeId, entry.ScopeLevel, entry.ScopeId, diag, ct);

        var allTypes = (await docTypeRepo.GetAllAsync(ct)).ToDictionary(t => t.Id);
        var titles = DocumentTypeSchemaReader.EffectiveFields(entry.CompositeTypeId, allTypes)
            .ToDictionary(f => f.Key, f => f.Title ?? f.Key);
        string Title(string key) => titles.TryGetValue(key, out var t) ? t : key;

        var nameCache = new Dictionary<string, string?>();
        async Task<string?> NameAsync(string entryId)
        {
            if (nameCache.TryGetValue(entryId, out var cached)) return cached;
            var e = Guid.TryParse(entryId, out var g) ? await repo.GetByIdAsync(g, ct) : null;
            return nameCache[entryId] = e?.DisplayName;
        }

        var items = new List<BindingCheckItem>();
        var handled = new HashSet<string>();
        var stored = entry.Data.RootElement;

        // 1) not-found — значение источника не нашлось в каталоге (из диагностики резолва).
        foreach (var d in diag.Where(d => d.Severity == DiagnosticSeverity.Warning))
            if (handled.Add(d.Path))
                items.Add(new BindingCheckItem(d.Path, Title(d.Path), "not-found", null, d.Message));

        // 2) свежие ссылки → сравнить со снимком.
        foreach (var (field, value) in fresh)
        {
            var freshId = RefId(value);
            if (freshId is null || !handled.Add(field)) continue;

            var freshName = await NameAsync(freshId);
            var storedId = stored.TryGetProperty(field, out var sv) ? RefIdJson(sv) : null;

            if (storedId == freshId)
                items.Add(freshName is null
                    ? new BindingCheckItem(field, Title(field), "dangling", null, "Целевая запись каталога удалена.")
                    : new BindingCheckItem(field, Title(field), "matched", freshName, null));
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
            var name = await NameAsync(storedId);
            items.Add(name is null
                ? new BindingCheckItem(prop.Name, Title(prop.Name), "dangling", null, "Целевая запись каталога удалена.")
                : new BindingCheckItem(prop.Name, Title(prop.Name), "matched", name, null));
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
