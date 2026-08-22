import { PASSWORD_HINT } from './passwordPolicy';

/**
 * Человеческий текст по неудачной регистрации первого администратора (issue #826).
 *
 * Нужен потому, что тело отказа — это `IdentityError[]` (`Results.BadRequest(result.Errors)` в
 * AuthEndpoints), где `description` написан ASP.NET Identity **по-английски**: «Passwords must
 * have at least one uppercase ('A'-'Z')». Показать его как есть — значит на первом же экране
 * системы встретить администратора чужим языком и чужой терминологией; показать общее «ошибка» —
 * скрыть единственное, что ему нужно знать: какому требованию не отвечает пароль.
 *
 * Разбираем по `code`, а не по тексту: коды у Identity стабильны, а формулировки меняются от
 * версии к версии и от локализации к локализации.
 */
export function registerErrorText(e: unknown): string {
  const res = (e as { response?: { status?: number; data?: unknown } })?.response;

  // Порядок важен: 403 и 429 приходят БЕЗ кодов Identity, и разбирать в них нечего.
  if (res?.status === 403)
    return 'Регистрация уже закрыта — администратор в системе есть. Обновите страницу и войдите.';
  if (res?.status === 429)
    return 'Слишком много попыток. Попробуйте через несколько минут.';

  const codes = identityCodes(res?.data);
  if (codes.some(c => c.startsWith('Password')))
    return `Пароль не отвечает требованиям. ${PASSWORD_HINT}`;
  if (codes.some(c => c.startsWith('Duplicate')))
    return 'Учётная запись с таким адресом уже есть.';
  if (codes.some(c => c === 'InvalidEmail' || c === 'InvalidUserName'))
    return 'Адрес электронной почты выглядит недействительным.';

  return 'Не удалось создать администратора. Проверьте адрес и пароль.';
}

/** Коды из тела отказа Identity. Тело — массив; всё прочее (строка, ProblemDetails) даёт пусто. */
function identityCodes(data: unknown): string[] {
  if (!Array.isArray(data)) return [];
  return data
    .map(x => (x as { code?: string })?.code)
    .filter((c): c is string => typeof c === 'string');
}
