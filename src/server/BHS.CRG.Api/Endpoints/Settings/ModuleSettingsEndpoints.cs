using BHS.CRG.Api.Auth;
using BHS.CRG.Api.Modules;
using BHS.CRG.Api.Modules.Ports;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Schema;
using BHS.CRG.Application.Settings;
using BHS.CRG.Infrastructure.Persistence;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Settings;
using Microsoft.EntityFrameworkCore;

namespace BHS.CRG.Api.Endpoints.Settings;

/// <summary>
/// Настройки модулей глазами администратора (задача M1, issue #1070).
///
/// <para>Адреса — ядра, а не модуля: экран один на все модули, и объявление несёт всё, что ему нужно
/// (подпись, следствие, вид, границы). Право — <c>core.system.manage</c>: настройка меняет поведение
/// экземпляра, а не данные модуля, и своего права модуль под неё не заводит.</para>
///
/// <para>Показываются настройки ВКЛЮЧЁННЫХ модулей: у выключенного значение лежит в базе и ждёт
/// включения, но править то, что ни на что не действует, незачем.</para>
/// </summary>
public static class ModuleSettingsEndpoints
{
    /// <summary>Новые значения по ключам; <c>null</c> — снять настройку, вернуться к умолчанию.</summary>
    public record ModuleSettingsRequest(Dictionary<string, string?>? Values);

    /// <param name="Stored">Сохранённое значение в хранимом виде; <c>null</c> — настройку не меняли
    /// либо в базе лежит негодное (тогда <paramref name="Stale" /> называет его как есть).</param>
    /// <param name="Stale">Что лежит в базе и не принимается этой версией; <c>null</c> — такого нет.
    /// Признак считает сервер проверкой объявления: сравнивать строки экрану нельзя — годное «1» и
    /// действующее «1.00» различаются записью, а не значением (ревью PR #1249).</param>
    public record ModuleSettingView(
        string Key, string Title, string Effect, string Kind,
        string Value, string? Stored, string? Stale, string Default,
        decimal? Min, decimal? Max, int? Scale, string? Unit, string? ChangeWarning, bool Money = false);

    /// <summary>
    /// Строка-сигнал о сопоставлении полей (ТЗ TYPE-23, issue #1101): сколько обязательного модуль
    /// ждёт от справочников ядра, сколько из этого не назначено и что из-за этого не работает.
    ///
    /// <para>Только сводка: сам экран сопоставления закрыт правом на типы, а этот адрес — правом на
    /// настройку системы. Тот, кто видит настройки, обязан узнать, что модуль работает не весь, —
    /// даже если назначить поле не может.</para>
    /// </summary>
    public record ModuleMappingSignal(int Total, int Unmet, IReadOnlyList<string> NotWorking);

    /// <param name="Mapping"><c>null</c> — модуль из справочников ядра ничего не читает.</param>
    public record ModuleSettingsView(
        string Code, string Title, IReadOnlyList<ModuleSettingView> Settings, ModuleMappingSignal? Mapping);

    public static void MapModuleSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/modules")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        g.MapGet("/", async (ModuleRegistry registry, IAppSettingsStore store, TagCatalog tags,
            AppDbContext db, CancellationToken ct) =>
        {
            // Модуль без настроек И без потребностей в ответ не попадает: пустая секция на экране —
            // вопрос «а где?». Модуль с одними потребностями попадает — ради строки-сигнала.
            var modules = registry.Enabled.Where(m => m.Settings.Count > 0 || m.Needs.Count > 0).ToList();
            // Одним чтением на все модули: адрес общий, и запрос на настройку рос бы вместе с ними.
            var stored = await store.GetManyAsync([.. modules.SelectMany(m => m.Settings).Select(s => s.Key)], ct);
            var types = modules.Any(m => m.Needs.Count > 0)
                ? await db.DocumentTypes.AsNoTracking().ToListAsync(ct)
                : [];

            return Results.Ok(new
            {
                modules = modules.Select(m => new ModuleSettingsView(m.Code, m.Title,
                    [.. m.Settings.Select(s => View(s, stored.GetValueOrDefault(s.Key)))],
                    Signal(m, tags, types))),
            });
        });

        g.MapPut("/{code}", async (string code, ModuleSettingsRequest req, ModuleRegistry registry,
            IAppSettingsStore store, IActivityLog journal, AppDbContext db, CancellationToken ct) =>
        {
            if (registry.Find(code) is not { } module)
                return Results.NotFound(new { error = $"Модуль «{code}» на этом экземпляре не включён." });

            if (req.Values is not { Count: > 0 } values)
                return Results.BadRequest(new { error = "Не названо ни одной настройки." });

            // Всё или ничего: сначала проверяем каждое значение, пишем — только если годны все.
            // Причина встаёт у СВОЕГО поля, поэтому отказы едут по ключам.
            var declared = module.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                if (!declared.TryGetValue(key, out var setting))
                    fields[key] = "модуль такой настройки не объявляет";
                else if (value is not null && setting.Refuse(value.Trim()) is { } why)
                    fields[key] = why;
            }

            if (fields.Count > 0)
                return Results.BadRequest(new { error = "Настройки не сохранены: " + string.Join("; ", fields.Values) + ".", fields });

            // ⚠️ Значения и записи журнала — ОДНОЙ транзакцией (ревью PR #1249). Хранилище и журнал
            // сохраняют каждый сам, и без неё значение, записанное до сбоя журнала, уже действовало бы
            // на все счета — без следа и при ответе «не сохранилось». Журнал здесь единственный
            // ответ на вопрос «с какого дня изменились суммы».
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var before = await store.GetManyAsync([.. values.Keys], ct);

            foreach (var (key, value) in values)
            {
                var setting = declared[key];
                var was = before.GetValueOrDefault(key);
                var now = value is null ? null : setting.Normalize(value.Trim());

                var change = ModuleSettingValues.Change(module.Title, setting, was, now);
                // Значение не меняется. Строку правим, только когда в базе лежит не то, что должно:
                // негодное или записанное не в хранимом виде. Несохранённую настройку тем же
                // значением, что умолчание, не заводим — иначе экран предложил бы «вернуть умолчание»
                // там, где оно и действует.
                if (change is null && (was is null || string.Equals(was, now, StringComparison.Ordinal))) continue;

                await store.SetAsync(key, now, ct);
                // В журнал — только настоящая смена: «было 1,00, стало 1,00» — шум, за которым
                // теряется запись, ради которой журнал открывали.
                if (change is not null)
                    await journal.RecordAsync(ActivityActions.ModuleSettingChanged, key,
                        change.Label, before: change.Before, after: change.After, ct: ct);
            }

            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    private static ModuleMappingSignal? Signal(
        IAppModule module, TagCatalog tags, IReadOnlyList<Domain.Documents.DocumentType> types)
    {
        if (module.Needs.Count == 0) return null;
        var coverage = ModuleCoverage.Of(module, tags, types);
        return new(coverage.Total, coverage.Unmet, coverage.NotWorking);
    }

    private static ModuleSettingView View(ModuleSetting setting, string? stored)
    {
        var number = setting as NumberSetting;
        var stale = ModuleSettingValues.Stale(setting, stored);
        return new(setting.Key, setting.Title, setting.Effect, setting.Kind,
            Value: ModuleSettingValues.Effective(setting, stored),
            Stored: stored is null || stale ? null : setting.Normalize(stored),
            Stale: stale ? stored : null,
            Default: setting.DefaultText,
            number?.Min, number?.Max, number?.Scale, number?.Unit, setting.ChangeWarning,
            Money: number?.Money ?? false);
    }
}
