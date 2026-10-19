import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

/**
 * Пакеты Radix, которые держат ОБЩЕЕ состояние, стоят в одном экземпляре (issue #1235).
 *
 * Слой, закрываемый по Esc и клику мимо, узнаёт, верхний ли он, по общей стопке слоёв. Стопка живёт
 * в пакете `react-dismissable-layer`, и у каждой его копии она своя. Radix прибивает внутренние
 * зависимости точной версией, поэтому обновление ОДНОГО пакета (так в master попал `react-popover`
 * 1.1.18 рядом с `react-dialog` 1.1.17) ставит вторую копию — и календарь, открытый в диалоге, и
 * сам диалог оба считают себя верхними: Esc закрывал оба, с кодом «всё работает» у каждого.
 *
 * Обновлять эти пакеты можно только вместе, одним набором версий, — поэтому в `package.json` они
 * записаны точными версиями, без «^».
 *
 * От единственной копии зависит и портал слоёв в body изнутри окна (issue #1521, docs/UI.md «Слои над
 * окном»). При двух копиях ловушка фокуса окна не знает о календаре, а `pointer-events: none` с body
 * никто не снимает: клик по дню закрывал календарь без выбора, клавиатура была мертва (#338). Тогда его
 * обошли порталом ВНУТРЬ окна — и окно стало резать календарь по краю.
 *
 * ⚠️ Набор стоит на слое 1.1.13 нарочно. Слой 1.1.14 (с ним идут `react-dialog` 1.1.18 и соседи)
 * вместе с React 19.2 отдаёт обработчикам Esc и клика мимо замыкание ПЕРВОГО рендера: диалог
 * отвечает по состоянию, которого давно нет. Ловит это `Modal.test.tsx` — два теста краснеют.
 */
const SHARED_STATE = ['react-dismissable-layer', 'react-focus-scope', 'react-focus-guards', 'react-portal'];

describe('Radix: общее состояние в одном экземпляре', () => {
  const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
  const own = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));

  it.each(SHARED_STATE)('%s', name => {
    const copies = Object.keys(lock.packages).filter(p => p.endsWith(`node_modules/@radix-ui/${name}`));
    expect(copies).toEqual([`node_modules/@radix-ui/${name}`]);
  });

  it('версии записаны точно', () => {
    const loose = Object.entries(own.dependencies)
      .filter(([name, range]) => name.startsWith('@radix-ui/') && !/^\d/.test(range));
    expect(loose).toEqual([]);
  });
});
