using Microsoft.AspNetCore.Identity;

namespace BHS.CRG.Api.Auth;

/// <summary>
/// Русские сообщения об ошибках ASP.NET Identity (issue #165). Подключается через
/// <c>.AddErrorDescriber&lt;RuIdentityErrorDescriber&gt;()</c>. Тексты доезжают до фронта,
/// т.к. endpoint'ы отдают <c>{ error: DescribeErrors(...) }</c>.
/// </summary>
public class RuIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() =>
        E(nameof(DefaultError), "Произошла неизвестная ошибка.");

    public override IdentityError ConcurrencyFailure() =>
        E(nameof(ConcurrencyFailure), "Данные были изменены другим процессом. Обновите и повторите.");

    // ── Пароль ──────────────────────────────────────────────────────────────
    public override IdentityError PasswordMismatch() =>
        E(nameof(PasswordMismatch), "Неверный пароль.");

    public override IdentityError PasswordTooShort(int length) =>
        E(nameof(PasswordTooShort), $"Пароль должен быть не короче {length} символов.");

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) =>
        E(nameof(PasswordRequiresUniqueChars), $"Пароль должен содержать не менее {uniqueChars} различных символов.");

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        E(nameof(PasswordRequiresNonAlphanumeric), "Пароль должен содержать хотя бы один спецсимвол.");

    public override IdentityError PasswordRequiresDigit() =>
        E(nameof(PasswordRequiresDigit), "Пароль должен содержать хотя бы одну цифру.");

    public override IdentityError PasswordRequiresLower() =>
        E(nameof(PasswordRequiresLower), "Пароль должен содержать хотя бы одну строчную букву.");

    public override IdentityError PasswordRequiresUpper() =>
        E(nameof(PasswordRequiresUpper), "Пароль должен содержать хотя бы одну заглавную букву.");

    // ── Email / имя пользователя ───────────────────────────────────────────
    public override IdentityError InvalidEmail(string? email) =>
        E(nameof(InvalidEmail), $"Некорректный адрес «{email}».");

    public override IdentityError DuplicateEmail(string email) =>
        E(nameof(DuplicateEmail), $"Адрес «{email}» уже используется.");

    public override IdentityError InvalidUserName(string? userName) =>
        E(nameof(InvalidUserName), $"Недопустимое имя пользователя «{userName}».");

    public override IdentityError DuplicateUserName(string userName) =>
        E(nameof(DuplicateUserName), $"Пользователь «{userName}» уже существует.");

    // ── Токены / прочее ────────────────────────────────────────────────────
    public override IdentityError InvalidToken() =>
        E(nameof(InvalidToken), "Ссылка недействительна или устарела.");

    public override IdentityError UserAlreadyHasPassword() =>
        E(nameof(UserAlreadyHasPassword), "У пользователя уже задан пароль.");

    public override IdentityError UserAlreadyInRole(string role) =>
        E(nameof(UserAlreadyInRole), $"Роль «{role}» уже назначена.");

    public override IdentityError UserNotInRole(string role) =>
        E(nameof(UserNotInRole), $"Роль «{role}» не назначена.");

    public override IdentityError RecoveryCodeRedemptionFailed() =>
        E(nameof(RecoveryCodeRedemptionFailed), "Код восстановления недействителен.");

    private static IdentityError E(string code, string description) => new() { Code = code, Description = description };
}
