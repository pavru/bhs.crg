// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router';
import { BottomNav } from './BottomNav';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { PageHeader } from './PageHeader';
import { railDef, type RailSection } from './railSections';
import { SectionMenuContext, type SectionMenuValue } from './sectionMenuContext';

/**
 * Оболочка телефона (issue #1434): разделы — нижней навигацией, страницы раздела — меню заголовка
 * страницы вместо подпанели.
 */

beforeAll(() => {
  // Слой меню меряет якорь — в jsdom этого нет.
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
});
afterEach(cleanup);

const page = (to: string, label: string, rail: RailSection['def']['id'], group?: string) =>
  ({ to, label, icon: railDef(rail).icon, rail, group });

const sections: RailSection[] = [
  { def: railDef('constructions'), groups: [{ items: [page('/document-sets', 'Стройки', 'constructions')] }], home: '/document-sets' },
  { def: railDef('costs'), groups: [{ items: [page('/invoices', 'Счета', 'costs'), page('/waybills', 'Накладные', 'costs')] }], home: '/invoices' },
  { def: railDef('system'), groups: [{ items: [page('/users', 'Пользователи', 'system')] }], home: '/users' },
];

const costs: SectionMenuValue = {
  def: railDef('costs'),
  groups: [
    { items: [page('/invoices', 'Счета', 'costs'), page('/waybills', 'Накладные', 'costs')] },
    { label: 'Отчёты и закрытие месяца', items: [page('/periods', 'Учётный период', 'costs', 'Отчёты и закрытие месяца')] },
  ],
  outside: [page('/datasets', 'Наборы данных', 'data')],
};

function Address() {
  return <output aria-label="адрес">{useLocation().pathname}</output>;
}

function shown(ui: React.ReactNode, at = '/invoices', menu: SectionMenuValue | null = null) {
  render(
    <MemoryRouter initialEntries={[at]}>
      <NavigationGuardProvider>
        <SectionMenuContext.Provider value={menu}>
          {ui}
          <Address />
        </SectionMenuContext.Provider>
      </NavigationGuardProvider>
    </MemoryRouter>,
  );
}

describe('нижняя навигация', () => {
  it('рабочие разделы, без «Системы»; свой отмечен', () => {
    shown(<BottomNav sections={sections} active="costs" />);
    const nav = screen.getByRole('navigation', { name: 'Разделы' });
    const links = nav.querySelectorAll('a');
    expect([...links].map(a => a.textContent)).toEqual(['Стройки', 'Учёт']);
    expect([...links].map(a => a.getAttribute('aria-current'))).toEqual([null, 'true']);
  });

  it('раздел ведёт на свою первую страницу', () => {
    shown(<BottomNav sections={sections} active="costs" />);
    fireEvent.click(screen.getByRole('link', { name: 'Стройки' }));
    expect(screen.getByLabelText('адрес').textContent).toBe('/document-sets');
  });

  it('без рабочих разделов строки нет', () => {
    shown(<BottomNav sections={sections.filter(s => s.def.unscoped)} active={null} />);
    expect(screen.queryByRole('navigation')).toBeNull();
  });
});

describe('меню раздела в заголовке страницы', () => {
  const trigger = () => screen.getByTestId('section-menu-trigger');
  function open() {
    act(() => trigger().focus());
    fireEvent.keyDown(trigger(), { key: 'Enter' });
    return screen.getByRole('menu');
  }

  it('на рабочем столе заголовок — просто заголовок', () => {
    shown(<PageHeader title="Счета на оплату" />);
    expect(screen.queryByTestId('section-menu-trigger')).toBeNull();
    expect(screen.getByRole('heading', { level: 1 }).textContent).toBe('Счета на оплату');
  });

  it('на телефоне заголовок раскрывает страницы раздела, над ним — имя раздела', () => {
    shown(<PageHeader title="Счета на оплату" />, '/invoices', costs);
    // Заголовок звучит названием страницы, о меню — описание кнопки.
    expect(screen.getByRole('heading', { level: 1, name: 'Счета на оплату' })).toBeTruthy();
    expect(trigger().getAttribute('aria-describedby')).toBeTruthy();
    expect(document.getElementById(trigger().getAttribute('aria-describedby')!)?.textContent)
      .toBe('Другие страницы раздела «Учёт затрат»');
    expect(screen.getByText('Учёт затрат')).toBeTruthy();
    expect(trigger().getAttribute('aria-haspopup')).toBe('menu');
    open();
    expect(screen.getAllByRole('menuitem').map(i => i.textContent)).toEqual(
      ['Счета', 'Накладные', 'Учётный период', 'Наборы данных']);
    // Группы подписаны, страницы вне пространства — своей группой в конце, как в палитре.
    expect(screen.getByText('Отчёты и закрытие месяца')).toBeTruthy();
    expect(screen.getByText('Вне пространства')).toBeTruthy();
    expect(screen.getByRole('menuitem', { name: 'Счета' }).getAttribute('aria-current')).toBe('page');
  });

  it('выбор страницы ведёт на неё', () => {
    shown(<PageHeader title="Счета на оплату" />, '/invoices', costs);
    open();
    fireEvent.click(screen.getByRole('menuitem', { name: 'Накладные' }));
    expect(screen.getByLabelText('адрес').textContent).toBe('/waybills');
  });

  it('заголовок-контрол оставлен своему делу — меню открывает кнопка рядом', () => {
    shown(<PageHeader title={<button type="button">Представление</button>} />, '/invoices', costs);
    expect(trigger().getAttribute('aria-label')).toBe('Другие страницы раздела «Учёт затрат»');
    expect(screen.getByRole('heading', { level: 1 }).contains(trigger())).toBe(false);
    open();
  });
});
