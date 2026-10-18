import { describe, it, expect } from 'vitest';
import type { AccessInfo } from '@/shared/api/access';
import { clientSources } from '@/shared/testing/clientSources';
import { PALETTE_ACTIONS, availableActions, intentHref } from './paletteActions';

function access(permissions: string[], modules: string[] = []): AccessInfo {
  return { permissions, modules: modules.map(code => ({ code, title: code, available: true })) };
}

/** Действия командной палитры (issue #1433): по правам — и у каждого есть экран, который его исполнит. */
describe('действия палитры', () => {
  it('действие требует и экран, и право своей кнопки', () => {
    const costs = ['costs'];
    // Право кнопки без права чтения экрана: экран отказал бы — действия нет.
    expect(availableActions(access(['costs.invoice.edit'], costs))).toEqual([]);
    // Права есть, модуля нет: экрана нет вовсе.
    expect(availableActions(access(['costs.invoice.read', 'costs.invoice.edit']))).toEqual([]);
    // Читает, но не заводит — бухгалтеру действие было бы дверью в отказ.
    expect(availableActions(access(['costs.invoice.read'], costs))).toEqual([]);

    expect(availableActions(access(['costs.invoice.read', 'costs.invoice.edit'], costs)).map(a => a.label))
      .toEqual(['Новый счёт', 'Счёт из файла']);
    expect(availableActions(access(['core.constructions.read', 'core.constructions.edit'])).map(a => a.label))
      .toEqual(['Новая стройка']);
  });

  it('действие ведёт на свой экран с параметром', () => {
    expect(intentHref(PALETTE_ACTIONS.waybillNew)).toBe('/waybills?do=waybill-new');
  });

  /**
   * ⚠️ Действие, которое не принимает ни один экран, — дверь в никуда: палитра привела бы на список и
   * ничего не сделала. Каждое обязано иметь `usePaletteIntent(PALETTE_ACTIONS.<имя>` в экране.
   */
  it('у каждого действия есть экран, который его исполняет', () => {
    const screens = Object.entries(clientSources).filter(([file]) => file.startsWith('/src/features/'));
    const orphans = Object.keys(PALETTE_ACTIONS).filter(name =>
      !screens.some(([, text]) => text.includes(`usePaletteIntent(PALETTE_ACTIONS.${name},`)));
    expect(orphans).toEqual([]);
  });
});
