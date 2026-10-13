import { describe, expect, it } from 'vitest';
import { withIcons, type Branding } from './branding';

// Пока идёт обновление, клиент новой версии может получить ответ от сервера прежней — без поля
// `icons`. Читают его и шапка (ссылка значка), и экран настройки: без подстановки оба упали бы.
describe('ответ об оформлении от сервера прежней версии', () => {
  const old = { productName: 'Пример', isCustom: true, hasLogo: true, logoVersion: '1' } as Branding;

  it('получает «стандартные значки, метки нет»', () => {
    expect(withIcons(old).icons).toEqual({ state: 'standard', version: '', problem: null, aspect: null });
  });

  it('ответ с полем остаётся тем же объектом', () => {
    const fresh: Branding = { ...old, icons: { state: 'custom', version: 'v', problem: null, aspect: 1 } };

    expect(withIcons(fresh)).toBe(fresh);
  });
});
