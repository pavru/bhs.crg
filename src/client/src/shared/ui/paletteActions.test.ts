import { describe, it, expect } from 'vitest';
import type { AccessInfo } from '@/shared/api/access';
import { clientSources } from '@/shared/testing/clientSources';
import { PALETTE_ACTIONS, availableActions, intentHref, primaryActionAt, type PaletteAction } from './paletteActions';

/** Файл компонента по его импорту в `from` (`@/…` или `./…`). */
function importOf(name: string, from: string): string | null {
  const spec = clientSources[from]?.match(new RegExp(`import \\{[^}]*\\b${name}\\b[^}]*\\} from '([^']+)'`))?.[1];
  if (!spec) return null;
  const path = spec.startsWith('@/') ? '/src/' + spec.slice(2) : from.replace(/[^/]+$/, '') + spec.replace(/^\.\//, '');
  return [path + '.tsx', path + '.ts'].find(f => f in clientSources) ?? null;
}

/** Файлы, которые рисуют адрес: компонент маршрута `App.tsx` и его индексный маршрут, если он есть. */
function screenFiles(to: string): string[] {
  const app = '/src/App.tsx';
  const segment = to.replace(/^\//, '');
  const page = clientSources[app].match(new RegExp(`<Route path="${segment}(?:/\\*)?" element=\\{<(\\w+)`))?.[1];
  const file = page ? importOf(page, app) : null;
  if (!file) return [];
  const index = clientSources[file].match(/<Route index element=\{<(\w+)/)?.[1];
  const inner = index ? importOf(index, file) : null;
  return inner ? [file, inner] : [file];
}

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

  it('экран адреса находится по маршрутам — сторож выше не слеп', () => {
    expect(screenFiles('/invoices')).toEqual(['/src/features/costs/InvoicesPage.tsx']);
    expect(screenFiles('/document-sets')).toEqual([
      '/src/features/document-sets/DocumentSetsPage.tsx', '/src/features/document-sets/ConstructionsList.tsx']);
  });

  it('действие ведёт на свой экран с параметром', () => {
    expect(intentHref(PALETTE_ACTIONS.waybillNew)).toBe('/waybills?do=waybill-new');
  });

  /**
   * ⚠️ Действие, которое не принимает его экран, — дверь в никуда: палитра привела бы на список и
   * ничего не сделала. Принимать обязан ИМЕННО экран адреса `to` — тот, что маршрут `App.tsx` рисует
   * на нём (с индексным маршрутом внутри, как у строек), а не любой файл (ревью PR #1478).
   */
  it('каждое действие принимает экран его адреса', () => {
    const misplaced = Object.entries(PALETTE_ACTIONS)
      .filter(([name, action]) => !screenFiles(action.to).some(file =>
        clientSources[file]?.includes(`usePaletteIntent(PALETTE_ACTIONS.${name},`)))
      .map(([name, action]) => `${name} → ${action.to}: ${screenFiles(action.to).join(', ') || 'маршрута нет'}`);
    expect(misplaced).toEqual([]);
  });
});

/** Основное действие экрана — плавающая кнопка телефона (issue #1434). */
describe('основное действие экрана', () => {
  const all: PaletteAction[] = Object.values(PALETTE_ACTIONS);

  it('у экрана оно одно', () => {
    const screens = all.filter(a => a.primary).map(a => a.to);
    expect(screens).toEqual([...new Set(screens)]);
  });

  it('только на самом экране и только с правом кнопки', () => {
    const clerk = access(['costs.invoice.read', 'costs.invoice.edit', 'core.constructions.read', 'core.constructions.edit'], ['costs']);
    expect(primaryActionAt('/invoices', clerk)?.label).toBe('Счёт из файла');
    expect(primaryActionAt('/document-sets', clerk)?.label).toBe('Новая стройка');
    // Карточка под экраном — у неё свои кнопки.
    expect(primaryActionAt('/document-sets/42', clerk)).toBeUndefined();
    // Читает, но не заводит — кнопки нет, как нет её и на экране.
    expect(primaryActionAt('/invoices', access(['costs.invoice.read'], ['costs']))).toBeUndefined();
  });

  /**
   * На телефоне filled-кнопка экрана прячется (`max-md:hidden`): плавающая и она рядом — одно действие
   * дважды. Сверяется по исходнику экрана: кнопка, забывшая класс, вернула бы дубль молча.
   */
  it('экран прячет на телефоне свою кнопку этого действия', () => {
    const shown = all.filter(a => a.primary).filter(a => !screenFiles(a.to).some(file =>
      new RegExp(`<Button variant="filled"(?:(?!</Button>)[\\s\\S])*?max-md:hidden(?:(?!</Button>)[\\s\\S])*?${a.label}\\s*</Button>`)
        .test(clientSources[file] ?? '')));
    expect(shown.map(a => a.label)).toEqual([]);
  });
});
