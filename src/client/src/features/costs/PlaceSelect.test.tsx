// @vitest-environment jsdom
import { useState, type ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { PlaceSelect } from './PlaceSelect';
import { NO_PLACE, type Place, type Places } from './places';
import { LOST } from './lostReferences';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { SiteRevealScope } from '@/shared/siteFilter/SiteOptions';
import { REVEAL_CHOICE } from '@/shared/siteFilter/siteReveal';

/**
 * Выбор стройки под фильтром «Избранные стройки» (issue #1219) — на живом нативном `<select>`:
 * пункт-выход не меняет значения, неизбранное стоящее не теряется, удалённое — по-прежнему потеря.
 */

afterEach(cleanup);

const site = (id: string, name: string) => ({ id, name, sections: [] });
const PLACES: Places = {
  sites: [site('a', 'ЖК «Север»'), site('b', 'Школа №7'), site('c', 'ТЦ «Меридиан»'), site('d', 'Склад-2')],
  articles: [],
};

const filter: SiteFilterValue = {
  status: 'ready', favorites: new Set(['a', 'b']), on: true, active: true,
  sites: PLACES.sites!, visible: () => true, setOn: () => {}, setFavorite: async () => {}, setFavorites: async () => {},
  consumers: 0, register: () => () => {},
};

function Under({ children }: { children: ReactNode }) {
  return <SiteFilterContext.Provider value={filter}>{children}</SiteFilterContext.Provider>;
}

/** Поле с настоящим состоянием формы: значение меняет только `onChange`. */
function Field({ initial, label, onChange }: { initial: Place; label: string; onChange?: (p: Place) => void }) {
  const [value, setValue] = useState(initial);
  return <PlaceSelect value={value} places={PLACES} label={label} className=""
    onChange={p => { onChange?.(p); setValue(p); }} />;
}

const at = (construction: string): Place => ({ ...NO_PLACE, construction });
const groups = (select: HTMLElement) =>
  [...select.querySelectorAll('optgroup')].map(g => [g.label, [...g.querySelectorAll('option')].map(o => o.textContent)]);

describe('PlaceSelect под фильтром избранного (#1219)', () => {
  it('пункт-выход не меняет значение и раскрывает список', () => {
    const onChange = vi.fn();
    render(<Under><Field initial={at('a')} label="Объект" onChange={onChange} /></Under>);
    const select = screen.getByLabelText('Объект') as HTMLSelectElement;
    expect(within(select).getByText('Показать все стройки (ещё 2)…')).toBeTruthy();

    fireEvent.change(select, { target: { value: REVEAL_CHOICE } });

    expect(onChange).not.toHaveBeenCalled();
    expect(select.value).toBe('стройка:a');
    expect(groups(select)).toEqual([
      ['Избранные стройки', ['ЖК «Север»', 'Школа №7']],
      ['Остальные стройки', ['ТЦ «Меридиан»', 'Склад-2']],
    ]);
    expect(within(select).queryByText(/Показать все стройки/)).toBeNull();
  });

  it('раскрытие действует на форму: соседний список раскрыт тоже', () => {
    render(<Under><SiteRevealScope>
      <Field initial={at('a')} label="Первый" />
      <Field initial={NO_PLACE} label="Второй" />
    </SiteRevealScope></Under>);
    fireEvent.change(screen.getByLabelText('Первый'), { target: { value: REVEAL_CHOICE } });
    expect(groups(screen.getByLabelText('Второй')).map(g => g[0])).toEqual(['Избранные стройки', 'Остальные стройки']);
  });

  it('стоящая неизбранная стройка — группой «Не в избранном», обычным именем, и остаётся значением', () => {
    render(<Under><Field initial={at('c')} label="Объект" /></Under>);
    const select = screen.getByLabelText('Объект') as HTMLSelectElement;
    // Значение поля — эта стройка: форма сохранит её, а не «— выберите —».
    expect(select.value).toBe('стройка:c');
    expect(groups(select)).toEqual([
      ['Избранные стройки', ['ЖК «Север»', 'Школа №7']],
      ['Не в избранном', ['ТЦ «Меридиан»']],
    ]);
    expect(within(select).queryByText(LOST.construction)).toBeNull();
    expect(within(select).getByText('Показать все стройки (ещё 1)…')).toBeTruthy();
  });

  it('удалённая стройка по-прежнему потеря, а не «не в избранном»', () => {
    render(<Under><Field initial={at('gone')} label="Объект" /></Under>);
    const select = screen.getByLabelText('Объект') as HTMLSelectElement;
    expect(select.value).toBe('стройка:gone');
    expect(within(select).getByText(LOST.construction)).toBeTruthy();
    expect(groups(select).map(g => g[0])).toEqual(['Избранные стройки']);
  });
});
