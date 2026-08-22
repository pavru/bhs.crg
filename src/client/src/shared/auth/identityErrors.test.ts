import { describe, it, expect } from 'vitest';
import { registerErrorText } from './identityErrors';

/** Ответ axios на отказ регистрации. */
const fail = (status: number, data?: unknown) => ({ response: { status, data } });

describe('registerErrorText', () => {
  it('называет требование к паролю, а не английский текст Identity', () => {
    const e = fail(400, [
      { code: 'PasswordRequiresUpper', description: "Passwords must have at least one uppercase ('A'-'Z')." },
    ]);
    const text = registerErrorText(e);
    expect(text).toContain('Пароль');
    expect(text).toContain('8');
    expect(text).not.toContain('Passwords');
  });

  it('разбирает по коду, а не по формулировке', () => {
    // Тот же случай с пустым description: формулировки Identity меняются, коды — нет.
    expect(registerErrorText(fail(400, [{ code: 'PasswordTooShort' }]))).toContain('Пароль');
  });

  it('отличает занятый адрес', () => {
    expect(registerErrorText(fail(400, [{ code: 'DuplicateUserName' }]))).toContain('таким адресом уже есть');
  });

  it('отличает недействительный адрес', () => {
    expect(registerErrorText(fail(400, [{ code: 'InvalidEmail' }]))).toContain('недействительным');
  });

  it('403 — регистрация закрыта, и это НЕ ошибка ввода', () => {
    // Гонка двух вкладок либо второй заход по старой странице: винить пароль здесь нельзя,
    // человек начал бы его менять, хотя система просто уже настроена.
    const text = registerErrorText(fail(403));
    expect(text).toContain('закрыта');
    expect(text).toContain('войдите');
  });

  it('429 — предел частоты, а не отказ данных', () => {
    expect(registerErrorText(fail(429))).toContain('через несколько минут');
  });

  it('незнакомое тело не притворяется разобранным', () => {
    expect(registerErrorText(fail(400, 'что-то своё'))).toBe(
      'Не удалось создать администратора. Проверьте адрес и пароль.');
    expect(registerErrorText(new Error('сеть'))).toContain('Не удалось создать администратора');
  });
});
