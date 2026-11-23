namespace BHS.CRG.Modules.Printing;

/// <summary>Форма собственного типа модуля. Поставщик контекста регистрируется под ключом <see cref="TypeCode"/>.</summary>
public sealed record ModulePrintForm(
    string TypeCode, string Title, string ReadPermission, ModulePrintTemplate? DefaultTemplate = null,
    ModulePrintRequisites? Requisites = null);

/// <summary>Шаблон, который получает тип формы при первом запуске без настроенных шаблонов.</summary>
public sealed record ModulePrintTemplate(string Name, string Content);

/// <summary>Пути в печатном JSON для команды «Реквизит» редактора шаблонов.</summary>
public sealed record ModulePrintRequisites(IReadOnlyList<ModulePrintField> Fields,
    string? CustomFieldsKey = null, IReadOnlyList<string>? ExcludedCustomKeys = null);

public sealed record ModulePrintField(IReadOnlyList<string> Path, string Title, string Type);
