import { describe, expect, it } from 'vitest';
import census from './homeQueues.json';
import { HOME_QUEUE_ROUTES, homeQueueRoute } from './homeQueueRoutes';
import { QUEUE_LABEL, QUEUE_PARAM, parseQueue } from '@/features/costs/invoiceQueues';
import { workNav } from './navConfig';

/**
 * Перепись очередей Главной (shell-08, ТЗ UI-34): у каждой очереди сборки есть экран, и экран принимает
 * её адрес. Сторона сервера — `HomeQueueDeclarationTests` (объявления = этот же файл).
 */

/** Как экран модуля читает очередь из адреса и как называет её отбор. Новый экран очереди — строка сюда. */
const SCREENS: Record<string, {
  path: string; accepts: (search: URLSearchParams) => string | null; label: (id: string) => string | undefined;
}> = {
  '/invoices': {
    path: '/invoices', accepts: s => parseQueue(s.get(QUEUE_PARAM)),
    label: id => { const queue = parseQueue(id); return queue ? QUEUE_LABEL[queue] : undefined; },
  },
};

describe('очереди Главной', () => {
  it('у каждой очереди переписи есть адрес, и экран принимает её параметр', () => {
    for (const q of census.queues) {
      const { module, id } = q;
      const route = homeQueueRoute(module, id);
      expect(route, `${module}/${id}`).not.toBeNull();
      const url = new URL(route!.href, 'http://x');
      const screen = SCREENS[url.pathname];
      expect(screen, `экран ${url.pathname} не умеет читать очередь из адреса`).toBeDefined();
      expect(screen.accepts(url.searchParams)).toBe(id);
      // Подпись сервера (сверена с переписью `HomeQueueDeclarationTests`) — та же, что у отбора экрана.
      expect(screen.label(id), `${module}/${id}: подпись`).toBe(q.title);
    }
  });

  it('адреса без очереди в переписи нет: таблица и перепись — одно и то же', () => {
    expect(Object.keys(HOME_QUEUE_ROUTES).sort())
      .toEqual(census.queues.map(q => `${q.module}/${q.id}`).sort());
  });

  it('экран очереди — пункт меню: его закрывает то же право, что и раздел', () => {
    for (const { href } of Object.values(HOME_QUEUE_ROUTES))
      expect(workNav.some(i => i.to === new URL(href, 'http://x').pathname), href).toBe(true);
  });
});
