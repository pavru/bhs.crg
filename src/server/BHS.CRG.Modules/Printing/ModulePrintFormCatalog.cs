using Microsoft.Extensions.DependencyInjection;

namespace BHS.CRG.Modules.Printing;

/// <summary>Объявления форм всей поставки, включая формы выключенных модулей.</summary>
public sealed class ModulePrintFormCatalog
{
    public sealed record Entry(IAppModule Module, ModulePrintForm Form);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<Entry> All { get; }
    public Entry? Find(string typeCode) => _entries.GetValueOrDefault(typeCode);

    public ModulePrintFormCatalog(IEnumerable<IAppModule> modules)
    {
        foreach (var module in modules)
        foreach (var form in module.PrintForms)
        {
            var entry = new Entry(module, form);
            if (!module.RecordTypes.Any(t => t.Code == form.TypeCode))
                throw Fault(entry, "тип формы должен быть объявлен в RecordTypes этого модуля");
            if (!module.Permissions.Any(p => string.Equals(p.Code, form.ReadPermission, StringComparison.OrdinalIgnoreCase)))
                throw Fault(entry, $"право «{form.ReadPermission}» должно быть объявлено этим модулем");
            if (!_entries.TryAdd(form.TypeCode, entry))
                throw Fault(entry, $"тип уже объявлен формой модуля «{_entries[form.TypeCode].Module.Code}»");
        }

        All = [.. _entries.Values];
    }

    internal static void EnsureProviders(IAppModule module, IServiceCollection services, HashSet<ServiceDescriptor> before)
    {
        var added = services.Where(d => IsContext(d) && !before.Contains(d)).ToList();
        foreach (var registration in added)
        {
            if (!module.PrintForms.Any(form => Equals(registration.ServiceKey, form.TypeCode)))
                throw new InvalidOperationException($"Модуль «{module.Code}» зарегистрировал поставщик контекста " +
                    $"с ключом «{registration.ServiceKey}» без собственной печатной формы.");
        }

        foreach (var form in module.PrintForms)
        {
            var providers = services.Where(d => IsContext(d) && Equals(d.ServiceKey, form.TypeCode)).ToList();
            if (providers.Count != 1 || before.Contains(providers[0]))
                throw Fault(new Entry(module, form), "модуль должен зарегистрировать ровно один поставщик контекста");
        }
    }

    internal void EnsureEnabledProviders(ModuleRegistry registry, IServiceCollection services)
    {
        foreach (var entry in All.Where(e => registry.IsEnabled(e.Module.Code)))
            if (services.Count(d => IsContext(d) && Equals(d.ServiceKey, entry.Form.TypeCode)) != 1)
                throw Fault(entry, "ожидался ровно один поставщик контекста после регистрации модулей");
    }

    private static bool IsContext(ServiceDescriptor descriptor) =>
        descriptor.IsKeyedService && descriptor.ServiceType == typeof(IModulePrintContext);

    private static InvalidOperationException Fault(Entry entry, string reason) =>
        new($"Печатная форма «{entry.Form.Title}» ({entry.Form.TypeCode}) модуля «{entry.Module.Code}»: {reason}.");
}
