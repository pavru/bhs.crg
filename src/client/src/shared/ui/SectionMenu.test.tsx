// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { railDef } from './railSections';
import { workNav } from './navConfig';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { SectionMenu } from './SectionMenu';

/**
 * Меню страниц заголовка на телефоне с группой «Закреплённые» (work-04): копия страницы галочкой не
 * отмечается, а вторая строка называет раздел и «вне пространства» — как в подпанели стола.
 */

afterEach(cleanup);

const byTo = (to: string) => workNav.find(n => n.to === to)!;

async function open(at: string) {
  const invoices = byTo('/invoices');
  render(
    <MemoryRouter initialEntries={[at]}>
      <NavigationGuardProvider>
        <SectionMenu value={{
          def: railDef(invoices.rail),
          outside: [],
          groups: [
            { label: 'Закреплённые', copies: true, items: [invoices, byTo('/estimates')],
              notes: { '/invoices': 'Учёт затрат', '/estimates': 'Планирование · вне пространства' } },
            { items: [invoices, byTo('/waybills')] },
          ],
        }}>
          <button type="button">Счета</button>
        </SectionMenu>
      </NavigationGuardProvider>
    </MemoryRouter>);
  const trigger = screen.getByRole('button', { name: 'Счета' });
  await act(async () => { trigger.focus(); fireEvent.keyDown(trigger, { key: 'Enter' }); });
  return screen.getByRole('menu');
}

describe('SectionMenu — «Закреплённые»', () => {
  it('текущей отмечена страница на своём месте, а не её закреплённая копия', async () => {
    const menu = await open('/invoices');
    const current = within(menu).getAllByRole('menuitem').filter(i => i.querySelector('[aria-current="page"]')
      || i.getAttribute('aria-current') === 'page');
    expect(current).toHaveLength(1);
    expect(current[0].textContent).not.toContain('Учёт затрат');
  });

  it('у закреплённых — вторая строка с разделом и «вне пространства»', async () => {
    const menu = await open('/waybills');
    expect(within(menu).getByText('Планирование · вне пространства')).toBeTruthy();
    expect(within(menu).getByText('Учёт затрат')).toBeTruthy();
  });
});
