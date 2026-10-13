using BHS.CRG.Application.Schema;
using BHS.CRG.Modules;

namespace BHS.CRG.Api.Modules;

/// <summary>
/// Потребности включённых модулей в справочниках ядра (ТЗ TYPE-23, issue #1101): какие роли и какие
/// тэги полей читает их код.
///
/// <para>Одиночка, собирается и ПРОВЕРЯЕТСЯ при старте (<c>StartupTasks</c>): негодное объявление —
/// роли нет в реестре, тэг не относится к роли, пустой текст следствия — останавливает запуск, а не
/// приходит отказом тому, кто открыл форму счёта.</para>
///
/// <para>Он же — граница порта: <see cref="EnsureDeclared(string)" /> отказывает роли, которой ни
/// один включённый модуль не объявил. Так экран сопоставления не может показать меньше, чем код
/// читает: прочитать необъявленное нечем.</para>
/// </summary>
public sealed class ModuleNeeds
{
    private readonly Dictionary<string, HashSet<string>> _tagsByRole = new(StringComparer.Ordinal);

    public ModuleNeeds(ModuleRegistry modules, TagCatalog tags)
    {
        var declared = new List<(IAppModule, ModuleRoleNeed)>();
        foreach (var module in modules.Enabled)
        {
            foreach (var need in module.Needs)
            {
                Validate(module, need, tags);
                declared.Add((module, need));
                if (!_tagsByRole.TryGetValue(need.Role, out var known)) _tagsByRole[need.Role] = known = [];
                known.UnionWith(need.Fields.Select(f => f.Tag));
            }

            // Заведение записей ядра адресует тем же, чем чтение: справочник, в который модуль
            // пишет, но который не объявил нужным, на экране сопоставления не появился бы вовсе.
            foreach (var intake in module.IntakeTypes)
                foreach (var tag in new[] { intake.NameTag, intake.UniqueTag })
                    if (!module.Needs.Any(n => n.Role == intake.Role && n.Fields.Any(f => f.Tag == tag)))
                        throw new InvalidOperationException(
                            $"Модуль «{module.Code}» объявляет заведение записей в справочнике «{intake.Role}» " +
                            $"по тэгу «{tag}», а в потребностях (Needs) этой пары нет. Объявите её: иначе " +
                            "поле, в которое модуль пишет, администратору негде было бы сопоставить.");

            // И цель поля, названная ролью, — тоже чтение справочника (ревью PR #1317): «Поставщик»
            // счёта целится в справочник организаций. Проекция сверяет роль только с реестром тэгов,
            // и без этой строки поле целилось бы в справочник, которого нет в потребностях.
            foreach (var type in module.RecordTypes)
                foreach (var field in type.Fields)
                    if (field.TargetRole is { Length: > 0 } role && module.Needs.All(n => n.Role != role))
                        throw new InvalidOperationException(
                            $"Модуль «{module.Code}» объявляет полю «{field.Key}» типа «{type.Code}» цель — " +
                            $"справочник с ролью «{role}», а в потребностях (Needs) этой роли нет. Объявите " +
                            "её: иначе справочник, в который целится поле, администратор на экране " +
                            "сопоставления не увидел бы.");
        }

        All = declared;
    }

    /// <summary>Все потребности включённых модулей — с модулем, который объявил каждую.</summary>
    public IReadOnlyList<(IAppModule Module, ModuleRoleNeed Need)> All { get; }

    /// <summary>Роль объявлена хотя бы одним включённым модулем; иначе — дефект вызывающего кода.</summary>
    public void EnsureDeclared(string role)
    {
        if (!_tagsByRole.ContainsKey(role))
            throw new InvalidOperationException(
                $"Справочник с ролью «{role}» ни один включённый модуль не объявил в потребностях (Needs) — " +
                "портом он не читается. Объявление — граница: что код читает, то администратор и видит " +
                "на экране сопоставления.");
    }

    /// <summary>То же для поля: пара «роль + тэг» обязана быть объявлена.</summary>
    public void EnsureDeclared(string role, string fieldTag)
    {
        EnsureDeclared(role);
        if (!_tagsByRole[role].Contains(fieldTag))
            throw new InvalidOperationException(
                $"Поле с тэгом «{fieldTag}» справочника «{role}» ни один включённый модуль не объявил в " +
                "потребностях (Needs) — портом оно не читается.");
    }

    /// <summary>
    /// Что из объявленного сейчас НЕ обеспечено — строками для журнала запуска.
    ///
    /// <para>Зачем при старте: обновление, перенёсшее роли не до конца (тип с иным кодом, поле у
    /// подтипа помечено иначе), поднимается зелёным и молчит — об остановленном сопоставлении
    /// поставщика узнают, открыв счёт. Журнал запуска читает тот, кто обновлял (ревью PR #1317).</para>
    /// </summary>
    public IReadOnlyList<string> Unmet(TagCatalog tags, IReadOnlyList<Domain.Documents.DocumentType> all)
    {
        var lines = new List<string>();
        foreach (var (module, need) in All)
        {
            var role = RoleResolution.Role(need.Role, all);
            if (role.Describe(tags.Find(need.Role)!) is { } absent)
            {
                lines.Add($"Модуль «{module.Code}»: {absent}. {need.WithoutIt}");
                continue;
            }

            foreach (var field in need.Fields)
            {
                var tag = tags.Find(field.Tag)!;
                if (RoleResolution.Field(role, tag, all).Describe(tag) is { } fault)
                    lines.Add($"Модуль «{module.Code}»: {fault}. {field.WithoutIt}");
            }
        }

        return lines;
    }

    private static void Validate(IAppModule module, ModuleRoleNeed need, TagCatalog tags)
    {
        string Who() => $"Модуль «{module.Code}» объявляет потребность в справочнике «{need.Role}»";

        if (!tags.IsRole(need.Role))
            throw new InvalidOperationException(
                $"{Who()}, а такой роли в реестре тэгов нет. Роль — тэг типа с признаком справочника.");
        if (string.IsNullOrWhiteSpace(need.Purpose) || string.IsNullOrWhiteSpace(need.WithoutIt))
            throw new InvalidOperationException(
                $"{Who()} без слов о том, зачем он нужен и что без него не работает. Строка «не назначено» " +
                "без следствия не говорит администратору, срочно ли это.");

        foreach (var field in need.Fields)
        {
            if (tags.Find(field.Tag) is not { Scope: TagScope.Field } tag || tag.Role != need.Role)
                throw new InvalidOperationException(
                    $"{Who()} и называет в нём поле тэгом «{field.Tag}», а это не тэг поля этой роли. " +
                    "По роли тэга ядро находит поле в справочнике — чужой тэг оно там искать не станет.");
            if (string.IsNullOrWhiteSpace(field.WithoutIt))
                throw new InvalidOperationException(
                    $"{Who()} и называет в нём поле «{field.Tag}» без слов о том, что без него не работает.");
        }

        var twice = need.Fields.GroupBy(f => f.Tag, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (twice is not null)
            throw new InvalidOperationException($"{Who()} и называет в нём тэг «{twice.Key}» дважды.");
    }
}
