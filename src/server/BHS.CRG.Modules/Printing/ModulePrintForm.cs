namespace BHS.CRG.Modules.Printing;

/// <summary>Форма собственного типа модуля. Поставщик контекста регистрируется под ключом <see cref="TypeCode"/>.</summary>
public sealed record ModulePrintForm(string TypeCode, string Title, string ReadPermission);
