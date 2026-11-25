export type TierId = 'issued' | 'estimate' | 'catalog';
export type Scenario = 'normal' | 'no-costs' | 'no-plan' | 'hidden' | 'no-item' | 'empty' | 'no-catalog';
export type Material = { id: string; name: string; unit: string; notIssued?: boolean };
export type Tier = {
  id: TierId; state: 'ok' | 'not-connected' | 'hidden' | 'no-item'; items: Material[];
  reason?: string; inArchive?: number; more?: boolean;
};
export const titles: Record<TierId, string> = {
  issued: 'Отпущено на стройку', estimate: 'Ресурсы позиции сметы', catalog: 'Справочник',
};
export const shortTitles: Record<TierId, string> = { issued: 'Стройка', estimate: 'Смета', catalog: 'Справочник' };
export const scenarios: { id: Scenario; label: string }[] = [
  { id: 'normal', label: 'Все источники' }, { id: 'no-costs', label: 'Учёт затрат выключен' },
  { id: 'no-plan', label: 'Планирование выключено' }, { id: 'hidden', label: 'Скрыто правами' },
  { id: 'no-item', label: 'Нет позиции сметы' }, { id: 'empty', label: 'Материалы не найдены' },
  { id: 'no-catalog', label: 'Справочник не заведён' },
];
const cable = { id: 'cable', name: 'Кабель ВВГнг(А)-LS 3×2,5', unit: 'м' };
const box = { id: 'box', name: 'Коробка распаечная IP55 100×100', unit: 'шт.' };
const conduit = { id: 'conduit', name: 'Труба гофрированная ПВХ Ø20', unit: 'м' };
const tie = { id: 'tie', name: 'Стяжка нейлоновая 200×3,6', unit: 'шт.' };
const clip = { id: 'clip', name: 'Клипса для гофрированной трубы Ø20', unit: 'шт.' };

/** Фикстуры выдачи. Поиск и исключение уже выбранных материалов имитируют работу списка. */
export function tiersFor(scenario: Scenario, query: string, selected: Material[]): Tier[] {
  const queryText = query.trim().toLocaleLowerCase('ru');
  const issued: Tier = scenario === 'no-costs'
    ? { id: 'issued', state: 'not-connected', items: [], reason: 'Модуль «Учёт затрат» не подключён.' }
    : scenario === 'hidden'
      ? { id: 'issued', state: 'hidden', items: [], reason: 'Скрыто правами · costs.materials.hint' }
      : { id: 'issued', state: 'ok', items: [cable, conduit] };
  const estimate: Tier = scenario === 'no-plan'
    ? { id: 'estimate', state: 'not-connected', items: [], reason: 'Модуль «Планирование» не подключён.' }
    : scenario === 'hidden'
      ? { id: 'estimate', state: 'hidden', items: [], reason: 'Скрыто правами · plan.estimate.hint' }
      : scenario === 'no-item'
        ? { id: 'estimate', state: 'no-item', items: [], reason: 'У этой работы нет позиции сметы.' }
        : { id: 'estimate', state: 'ok', items: issued.state === 'ok' ? [{ ...box, notIssued: true }] : [cable, box] };
  const catalog: Tier = scenario === 'no-catalog'
    ? { id: 'catalog', state: 'not-connected', items: [], reason: 'Справочник номенклатуры не заведён.' }
    : { id: 'catalog', state: 'ok', items: [tie, clip, ...(scenario === 'hidden' ? [cable, box] : [])],
        more: !queryText, inArchive: !queryText || queryText.includes('гофр') ? 2 : 0 };
  return [issued, estimate, catalog].map(tier => ({
    ...tier, items: scenario === 'empty' ? [] : tier.items.filter(item =>
      item.name.toLocaleLowerCase('ru').includes(queryText) && !selected.some(current => current.id === item.id)),
    ...(scenario === 'empty' && tier.id === 'catalog' ? { more: false, inArchive: 0 } : {}),
  }));
}
