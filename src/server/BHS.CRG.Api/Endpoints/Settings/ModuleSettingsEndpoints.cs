using BHS.CRG.Api.Auth;
using BHS.CRG.Application.Activity;
using BHS.CRG.Application.Settings;
using BHS.CRG.Modules;
using BHS.CRG.Modules.Settings;

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

    public record ModuleSettingView(
        string Key, string Title, string Effect, string Kind,
        string Value, string? Stored, string Default,
        decimal? Min, decimal? Max, int? Scale, string? Unit, string? ChangeWarning);

    public record ModuleSettingsView(string Code, string Title, IReadOnlyList<ModuleSettingView> Settings);

    public static void MapModuleSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/settings/modules")
            .RequireAuthorization(AppPolicies.Permission(CorePermissions.SystemManage));

        g.MapGet("/", async (ModuleRegistry registry, IAppSettingsStore store, CancellationToken ct) =>
        {
            var modules = new List<ModuleSettingsView>();
            // Модуль без настроек в ответ не попадает: пустая секция на экране — вопрос «а где?».
            foreach (var module in registry.Enabled.Where(m => m.Settings.Count > 0))
            {
                var views = new List<ModuleSettingView>();
                foreach (var setting in module.Settings)
                    views.Add(View(setting, await store.GetAsync(setting.Key, ct)));
                modules.Add(new(module.Code, module.Title, views));
            }

            return Results.Ok(new { modules });
        });

        g.MapPut("/{code}", async (string code, ModuleSettingsRequest req, ModuleRegistry registry,
            IAppSettingsStore store, IActivityLog journal, CancellationToken ct) =>
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

            foreach (var (key, value) in values)
            {
                var setting = declared[key];
                var before = await store.GetAsync(key, ct);
                var after = value is null ? null : setting.Normalize(value.Trim());
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;

                await store.SetAsync(key, after, ct);
                // В журнал — действующие значения словами, а не сохранённые строки: «было 1,00 ₽,
                // стало 0,50 ₽» читается и тогда, когда прежнего значения в базе не было вовсе.
                await journal.RecordAsync(ActivityActions.ModuleSettingChanged, key,
                    $"{module.Title}: {setting.Title}",
                    before: setting.Display(Effective(setting, before)),
                    after: setting.Display(Effective(setting, after)), ct: ct);
            }

            return Results.NoContent();
        });
    }

    private static ModuleSettingView View(ModuleSetting setting, string? stored)
    {
        var number = setting as NumberSetting;
        return new(setting.Key, setting.Title, setting.Effect, setting.Kind,
            Value: Effective(setting, stored),
            // Сохранённое отдаётся отдельно от действующего: расходятся они, когда в базе лежит
            // значение, которое эта версия уже не принимает, — и экран обязан это показать.
            Stored: stored, Default: setting.DefaultText,
            number?.Min, number?.Max, number?.Scale, number?.Unit, setting.ChangeWarning);
    }

    private static string Effective(ModuleSetting setting, string? stored) =>
        stored is not null && setting.Refuse(stored) is null ? setting.Normalize(stored) : setting.DefaultText;
}
