import { describe, expect, it } from 'vitest';
import { contractPermissionNote } from './roles';

/**
 * Подпись права контракта (plan-21, ТЗ AUTH-5, AUTH-16.7) — по признаку с сервера, а не по коду:
 * право контракта начинается с кода своего модуля, как любое его право, и по коду не узнаётся.
 */
describe('contractPermissionNote', () => {
  it('называет модуль, который право контракта не открывает', () => {
    expect(contractPermissionNote({ contract: true }, 'Планирование'))
      .toBe('право контракта: не открывает модуль „Планирование“, данные отдаются другим модулям');
  });

  it('у обычного права подписи нет', () => {
    expect(contractPermissionNote({ contract: false }, 'Планирование')).toBeNull();
  });
});
