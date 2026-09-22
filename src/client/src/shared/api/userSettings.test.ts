import { describe, expect, it } from 'vitest';
import { resolvePreference } from './userSettings';

/**
 * Настройки хранит сервер, браузер держит зеркало (issue #953). Весь стык между ними — одна
 * функция, и проверяется именно она: три её случая различить на глаз нельзя, а перепутать легко.
 */
describe('resolvePreference', () => {
  it('до ответа сервера показывает зеркало — иначе первый кадр мигнёт чужой темой', () => {
    expect(resolvePreference(undefined, 'theme', 'dark', 'system')).toBe('dark');
  });

  it('до ответа сервера и без зеркала — умолчание', () => {
    expect(resolvePreference(undefined, 'theme', null, 'system')).toBe('system');
  });

  it('ответ сервера главнее зеркала: за этим задача и делалась', () => {
    // На этой машине когда-то выбрали светлую, а в учётной записи записана тёмная — значит тёмная.
    expect(resolvePreference({ theme: 'dark' }, 'theme', 'light', 'system')).toBe('dark');
  });

  /**
   * Тот случай, ради которого функция вынесена отдельно. «Сервер ответил, а ключа нет» означает
   * «человек ничего не выбирал», и подставить сюда зеркало — значит отдать новому пользователю
   * настройки прежнего хозяина машины: он их не выбирал и не поймёт, откуда они.
   */
  it('сервер ответил без ключа — умолчание, а НЕ зеркало предыдущего пользователя', () => {
    expect(resolvePreference({}, 'theme', 'dark', 'system')).toBe('system');
  });

  it('чужие ключи в ответе не мешают', () => {
    expect(resolvePreference({ locale: 'de-DE' }, 'theme', null, 'system')).toBe('system');
    expect(resolvePreference({ locale: 'de-DE' }, 'locale', null, 'system')).toBe('de-DE');
  });
});
