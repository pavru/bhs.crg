import { describe, it, expect } from 'vitest';
import { registerErrorText, autoLoginFailedText } from './identityErrors';

/** Ответ axios на отказ регистрации. */
const fail = (status: number, data?: unknown) => ({ response: { status, data } });

describe('registerErrorText', () => {
  it('показывает точный текст сервера, а не пересказ политики', () => {
    // Identity уже отвечает по-русски (RuIdentityErrorDescriber) и называет НАРУШЕННОЕ правило.
    // Подменять это общей фразой значит заставить человека перечитывать политику целиком.
    const e = fail(400, { error: 'Пароль должен содержать хотя бы одну заглавную букву.' });
    expect(registerErrorText(e)).toBe('Пароль должен содержать хотя бы одну заглавную букву.');
  });

  it('склеенные требования доходят как есть', () => {
    const e = fail(400, { error: 'Пароль должен быть не короче 8 символов.; Пароль должен содержать хотя бы одну цифру.' });
    expect(registerErrorText(e)).toContain('не короче 8 символов');
    expect(registerErrorText(e)).toContain('одну цифру');
  });

  it('403 — регистрация закрыта, и это НЕ ошибка ввода', () => {
    // Гонка двух вкладок либо второй заход по старой странице: винить пароль здесь нельзя —
    // человек начнёт его менять, хотя система просто уже настроена. Свой текст, а не серверный
    // («обратитесь к администратору»): администратор здесь — он сам, ему нужно просто войти.
    const text = registerErrorText(fail(403, { detail: 'Регистрация закрыта. Обратитесь к администратору.' }));
    expect(text).toContain('закрыта');
    expect(text).toContain('войдите');
  });

  it('429 — предел частоты, а не отказ данных', () => {
    // Тела у ответа нет вовсе: без своего текста человек увидел бы общую фразу про адрес и пароль.
    expect(registerErrorText(fail(429))).toContain('через несколько минут');
  });

  it('без внятного тела отвечает по-русски, а не сообщением axios', () => {
    // У axios `message` есть ВСЕГДА («Request failed with status code 400»), и общий apiError
    // вернул бы именно его. На первом экране системы это худший ответ: и непонятно, и не на том
    // языке. Пусто в теле — говорим своё.
    expect(registerErrorText(fail(400, {}))).toContain('Не удалось создать администратора');
    expect(registerErrorText(new Error('Network Error'))).toContain('Не удалось создать администратора');
  });

  it('текст ProblemDetails тоже доходит', () => {
    expect(registerErrorText(fail(400, { detail: 'Что-то пошло не так на сервере.' })))
      .toBe('Что-то пошло не так на сервере.');
  });
});

describe('autoLoginFailedText', () => {
  it('не отправляет регистрироваться заново', () => {
    // Учётная запись уже создана: «не удалось создать» увело бы на повторную попытку, а она
    // ответит «регистрация закрыта» — два сообщения, противоречащих друг другу.
    const text = autoLoginFailedText();
    expect(text).toContain('создан');
    expect(text).toContain('войдите');
    expect(text).not.toContain('Не удалось создать');
  });
});
