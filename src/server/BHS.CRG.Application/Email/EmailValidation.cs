using System.Text.RegularExpressions;

namespace BHS.CRG.Application.Email;

public static partial class EmailValidation
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Pattern();

    /// <summary>Базовая проверка валидности адреса (непусто + одиночная @ + домен с точкой).</summary>
    public static bool IsValid(string? email) => !string.IsNullOrWhiteSpace(email) && Pattern().IsMatch(email);
}
