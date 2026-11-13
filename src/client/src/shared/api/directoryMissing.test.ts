import { describe, expect, it } from 'vitest';
import { directoryMissingOf } from './directoryMissing';

const refusal = (status: number, data: unknown) => ({ response: { status, data } });

describe('directoryMissingOf — отказ «справочник не заведён» [CORE-20.4]', () => {
  it('409 с кодом directory-missing — значение с причиной и словами сервера', () => {
    expect(directoryMissingOf(refusal(409, { code: 'directory-missing', reason: 'unit-missing', error: 'Нет единиц.' })))
      .toEqual({ reason: 'unit-missing', error: 'Нет единиц.' });
  });

  it('без слов сервера — запасные слова, а не пустая строка', () => {
    expect(directoryMissingOf(refusal(409, { code: 'directory-missing', reason: 'not-created' }), 'Не заведён.'))
      .toEqual({ reason: 'not-created', error: 'Не заведён.' });
  });

  it('409 без кода — не этот отказ', () => {
    expect(directoryMissingOf(refusal(409, { error: 'Вид работы тем временем изменили.' }))).toBeNull();
  });

  it('другой статус с тем же кодом — не этот отказ', () => {
    expect(directoryMissingOf(refusal(400, { code: 'directory-missing', reason: 'unit-missing', error: 'х' }))).toBeNull();
    expect(directoryMissingOf(new Error('сеть'))).toBeNull();
    expect(directoryMissingOf(undefined)).toBeNull();
  });
});
