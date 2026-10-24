namespace BHS.CRG.Modules.Plan.Data;

/// <summary>Текст из файла сметы под предел колонки — одним местом на все записи модуля.</summary>
internal static class PlanText
{
    /// <summary>Обрезать до предела колонки; <c>null</c> остаётся <c>null</c>.</summary>
    public static string? Clip(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..length];
}
