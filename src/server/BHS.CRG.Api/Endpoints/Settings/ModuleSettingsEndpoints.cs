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
        decimal? Min, decimal? Max, int? Scale, string? Unit, string? ChangeWarning, bool Money = false,
        string? On = null, string? Off = null);

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
            ModuleSettingWriter writer, CancellationToken ct) =>
        {
            if (Checked(code, req, registry, out var module, out var values) is { } refused) return refused;

            await writer.SaveAsync(module, values, ct);
            return Results.NoContent();
        });

        // Предупреждение числами (E2, issue #1100, spec v3.3 п. 1.9): что смена сделает с записанным — у
        // настроек, чей модуль умеет это сказать. Тело — то же, что у записи; записи нет. ChangeWarning
        // остаётся текстом объявления: числа — сверх него, а не вместо.
        g.MapPost("/{code}/preview", async (string code, ModuleSettingsRequest req, ModuleRegistry registry,
            ModuleSettingWriter writer, CancellationToken ct) =>
        {
            if (Checked(code, req, registry, out var module, out var values) is { } refused) return refused;

            var impacts = await writer.PreviewAsync(module, values, ct);
            return Results.Ok(new
            {
                impacts = impacts.Select(i => new ModuleSettingImpactView(
                    i.Setting.Key, i.Setting.Title, i.Impact.Changed, i.Impact.Recorded, i.Impact.Text)),
            });
        });
    }

    /// <summary>Что смена настройки сделает с записанными данными.</summary>
    /// <param name="Changed">Сколько документов изменится.</param>
    /// <param name="Recorded">Сколько из них получат запись о смене (у счетов — книгу исправлений).</param>
    /// <param name="Text">То же словами модуля — его показывают.</param>
    public record ModuleSettingImpactView(string Key, string Title, int Changed, int Recorded, string Text);

    /// <summary>
    /// Модуль включён, и каждое значение годно — иначе отказ. Всё или ничего: причина встаёт у СВОЕГО поля,
    /// поэтому отказы едут по ключам.
    /// </summary>
    private static IResult? Checked(
        string code, ModuleSettingsRequest req, ModuleRegistry registry, out IAppModule module,
        out IReadOnlyDictionary<string, string?> values)
    {
        // Без модуля дальше не идут: отказ уходит раньше, чем кто-то прочтёт module.
        module = registry.Find(code)!;
        values = req.Values ?? [];
        if (module is null)
            return Results.NotFound(new { error = $"Модуль «{code}» на этом экземпляре не включён." });

        if (values.Count == 0)
            return Results.BadRequest(new { error = "Не названо ни одной настройки." });

        var declared = module.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (!declared.TryGetValue(key, out var setting))
                fields[key] = "модуль такой настройки не объявляет";
            else if (value is not null && setting.Refuse(value.Trim()) is { } why)
                fields[key] = why;
        }

        return fields.Count > 0
            ? Results.BadRequest(new { error = "Настройки не сохранены: " + string.Join("; ", fields.Values) + ".", fields })
            : null;
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
            Money: number?.Money ?? false,
            // Значения флага — словами самой настройки: экрану нечем назвать их иначе, чем «да / нет».
            On: (setting as FlagSetting)?.On, Off: (setting as FlagSetting)?.Off);
    }
}
