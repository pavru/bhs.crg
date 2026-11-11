// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { workNav } from './navConfig';
import { PageList } from './PageList';
import { PinnedGroup } from './PinnedGroup';
import { PinnedPagesContext, type PinnedPages } from './pinnedPagesContext';
import { MAX_PINNED, pinnedGroup } from './pinnedSections';

/**
 * Закрепление в подпанели (ТЗ AUTH-16.4, work-04; макет Р0, стол — А): «⋮» у пункта и группа
 * «Закреплённые» первой, которая сворачивается.
 */

afterEach(cleanup);

function context(list: string[], collapsed = false): PinnedPages {
  return { list, has: to => list.includes(to), pin: vi.fn(), unpin: vi.fn(), move: vi.fn(), collapsed, setCollapsed: vi.fn() };
}

const byTo = (to: string) => workNav.find(n => n.to === to)!;

async function openMenu(name: string) {
  const trigger = screen.getByRole('button', { name });
  await act(async () => { trigger.focus(); fireEvent.keyDown(trigger, { key: 'Enter' }); });
  return screen.getByRole('menu');
}

function list(value: PinnedPages, pinnable = (to: string) => to !== '/document-sets/1') {
  render(
    <MemoryRouter>
      <PinnedPagesContext.Provider value={value}>
        <PageList pinnable={pinnable} groups={[
          { items: [byTo('/invoices'), byTo('/waybills')] },
          { label: 'Избранные стройки', items: [{ ...byTo('/document-sets'), to: '/document-sets/1', label: 'ЖК' }] },
        ]} />
      </PinnedPagesContext.Provider>
    </MemoryRouter>);
}

describe('«⋮» у пункта подпанели', () => {
  it('незакреплённая страница — «Закрепить страницу»', async () => {
    const value = context(['/invoices']);
    list(value);
    const menu = await openMenu('Действия: Накладные');
    await act(async () => { fireEvent.click(within(menu).getByRole('menuitem', { name: /Закрепить страницу/ })); });
    expect(value.pin).toHaveBeenCalledWith('/waybills');
  });

  it('закреплённая — «Открепить страницу»', async () => {
    const value = context(['/invoices']);
    list(value);
    const menu = await openMenu('Действия: Счета');
    await act(async () => { fireEvent.click(within(menu).getByRole('menuitem', { name: /Открепить страницу/ })); });
    expect(value.unpin).toHaveBeenCalledWith('/invoices');
  });

  it('двадцать закреплений — пункт погашен с пояснением', async () => {
    list(context(Array.from({ length: MAX_PINNED }, (_, i) => `/p${i}`)));
    const menu = await openMenu('Действия: Накладные');
    const item = within(menu).getByRole('menuitem', { name: /Закрепить страницу/ });
    expect(item.getAttribute('aria-disabled')).toBe('true');
    expect(within(menu).getByText('Закреплено 20 из 20 — открепите что-нибудь в профиле')).toBeTruthy();
  });

  it('у избранной стройки «⋮» нет: это запись, а не страница', () => {
    list(context([]));
    expect(screen.queryByRole('button', { name: 'Действия: ЖК' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Действия: Счета' })).toBeTruthy();
  });
});

describe('группа «Закреплённые»', () => {
  const entries = pinnedGroup(['/waybills', '/estimates'], [byTo('/waybills'), byTo('/estimates')],
    item => item.module === 'costs');

  function group(value: PinnedPages) {
    render(
      <MemoryRouter>
        <PinnedPagesContext.Provider value={value}><PinnedGroup entries={entries} /></PinnedPagesContext.Provider>
      </MemoryRouter>);
  }

  it('в порядке списка, вторая строка — раздел и «вне пространства»', () => {
    group(context(['/waybills', '/estimates']));
    const links = within(screen.getByTestId('pinned-group')).getAllByRole('link');
    expect(links.map(l => l.getAttribute('href'))).toEqual(['/waybills', '/estimates']);
    expect(links[0].textContent).toContain('Учёт затрат');
    expect(links[1].textContent).toContain('Планирование · вне пространства');
  });

  it('сворачивается: заголовок с числом остаётся, пунктов нет', () => {
    const open = context(['/waybills', '/estimates']);
    group(open);
    fireEvent.click(screen.getByRole('button', { name: /Закреплённые/ }));
    expect(open.setCollapsed).toHaveBeenCalledWith(true);
    cleanup();

    group(context(['/waybills', '/estimates'], true));
    const header = screen.getByRole('button', { name: /Закреплённые/ });
    expect(header.getAttribute('aria-expanded')).toBe('false');
    expect(header.textContent).toContain('2');
    expect(within(screen.getByTestId('pinned-group')).queryAllByRole('link')).toHaveLength(0);
  });

  it('закреплений нет — группы нет', () => {
    render(<MemoryRouter><PinnedGroup entries={[]} /></MemoryRouter>);
    expect(screen.queryByTestId('pinned-group')).toBeNull();
  });
});
