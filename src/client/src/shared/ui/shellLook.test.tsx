// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { SectionRail } from './SectionRail';
import { panelLink } from './panelLink';
import { SearchInput } from './SearchInput';
import { InLayerContext } from './layerPlace';
import { ListDetailShell } from './ListDetailShell';
import { dockedPlace, PALETTE_SURFACE } from './paletteDock';
import { NavigationGuardProvider } from './NavigationGuardProvider';
import { railDef, type RailSection } from './railSections';

/** Вид оболочки по MD3 (sharedui-md3-15): рейл, пункт подпанели, поиск в списке, палитра, столбец списка. */

vi.mock('@/shared/api/branding', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/branding')>(),
  useProductName: () => 'Стенд',
  useBranding: () => ({ data: undefined }),
}));

afterEach(cleanup);

const sections: RailSection[] = (['constructions', 'costs', 'system'] as const)
  .map(id => ({ def: railDef(id), groups: [], home: `/${id}` }));

describe('SectionRail — navigation rail', () => {
  function rail() {
    render(
      <MemoryRouter>
        <NavigationGuardProvider>
          <SectionRail sections={sections} active="costs" panelHidden={false} onShowPanel={() => {}} />
        </NavigationGuardProvider>
      </MemoryRouter>,
    );
    return screen.getByRole('navigation', { name: 'Разделы' });
  }

  it('рейл шириной 80 на поверхности рамки', () => {
    const root = rail().parentElement!;
    expect(root.className).toContain('w-20');
    expect(root.className).toContain('bg-surface-container-low');
  });

  it('знак 24 у каждого раздела, залитый — только у выбранного', () => {
    rail();
    for (const s of sections) {
      const link = screen.getByRole('link', { name: s.def.label });
      const svg = link.querySelector('svg')!;
      expect(svg.getAttribute('width')).toBe('24');
      const d = svg.querySelector('path')!.getAttribute('d');
      expect(d).toBe(s.def.id === 'costs' ? s.def.icon.fill : s.def.icon.d);
    }
  });

  it('подпись — label-medium одним весом и цветом, без переноса', () => {
    rail();
    const on = screen.getByRole('link', { name: railDef('costs').label });
    const off = screen.getByRole('link', { name: railDef('system').label });
    expect(on.className).toContain('text-label-medium');
    const label = (a: HTMLElement) => a.querySelector('span:last-child')!.className;
    expect(label(on)).toBe(label(off));
    expect(label(on)).toContain('whitespace-nowrap');
    expect(label(on)).not.toMatch(/font-/);
  });
});

describe('panelLink — пункт подпанели', () => {
  it('48, отступ 16, label-large; выбранный — secondary-container', () => {
    expect(panelLink(false)).toMatch(/\bh-12\b.*\bpx-4\b/);
    expect(panelLink(false)).toContain('text-label-large');
    expect(panelLink(false)).toContain('state-layer');
    expect(panelLink(true)).toContain('bg-secondary-container text-on-secondary-container');
    expect(panelLink(true)).not.toContain('font-medium');
  });
});

describe('SearchInput — search bar', () => {
  const field = () => screen.getByRole('textbox', { name: 'Поиск' });

  it('на странице — 56 формы full на surface-container-high, без рамки', () => {
    render(<SearchInput value="" onChange={() => {}} />);
    expect(field().className).toMatch(/\bh-14\b/);
    expect(field().className).toContain('rounded-full');
    expect(field().className).toContain('bg-surface-container-high');
    expect(field().className).not.toMatch(/\bborder\b/);
  });

  it('в окне и листе — плотное 40', () => {
    render(<InLayerContext.Provider value><SearchInput value="" onChange={() => {}} /></InLayerContext.Provider>);
    expect(field().className).toMatch(/\bh-10\b/);
  });

  it('очистка — кнопкой-иконкой', () => {
    const onChange = vi.fn();
    render(<SearchInput value="счёт" onChange={onChange} />);
    screen.getByRole('button', { name: 'Очистить поиск' }).click();
    expect(onChange).toHaveBeenCalledWith('');
  });
});

describe('CommandPalette — docked search view', () => {
  const anchor = (left: number, top: number, width: number) =>
    ({ getBoundingClientRect: () => ({ left, top, width }) }) as HTMLElement;

  it('встаёт на место строки поиска, не уже 560', () => {
    expect(dockedPlace(anchor(400, 4, 720), 1366)).toEqual({ left: 400, top: 4, width: 720 });
    expect(dockedPlace(anchor(400, 4, 300), 1366)).toEqual({ left: 400, top: 4, width: 560 });
  });

  it('не выходит за край окна', () => {
    expect(dockedPlace(anchor(700, 4, 300), 1024)).toEqual({ left: 1024 - 560 - 16, top: 4, width: 560 });
  });

  it('дверь ужата до значка, её нет или окно уже 768 — по центру', () => {
    expect(dockedPlace(anchor(400, 4, 56), 1366)).toBeNull();
    expect(dockedPlace(null, 1366)).toBeNull();
    expect(dockedPlace(anchor(0, 4, 400), 700)).toBeNull();
  });

  it('своя поверхность — surface-container-high уровня 3, не слой меню', () => {
    expect(PALETTE_SURFACE).toBe('bg-surface-container-high shadow-elevation-3');
  });
});

describe('ListDetailShell — столбец списка', () => {
  it('на surface с чертой у замыкающего края', () => {
    render(<ListDetailShell title="Счета" nav={<div />} detail={<div />} />);
    const nav = screen.getByTestId('ld-nav');
    expect(nav.className).toMatch(/\bbg-surface\b/);
    expect(nav.className).not.toContain('bg-surface-container-low');
    expect(nav.className).toContain('border-outline-variant');
  });
});
