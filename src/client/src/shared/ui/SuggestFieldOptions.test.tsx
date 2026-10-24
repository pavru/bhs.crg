// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { apiClient } from '../api/client';
import { SuggestField } from './SuggestField';
import { matchOptions, SUGGEST_LIMIT, type SuggestItem } from './suggestMatch';

/**
 * Поле с подсказкой из готового перечня (UI-55, change `costs-filter-place-hints`). Поведение слоя и
 * клавиатуры у него то же, что у подсказки по адресу, — его держит `SuggestField.test.tsx`; здесь то, чем
 * перечень отличается: ни запроса, совпадение на клиенте, подписи групп и пометки пунктов.
 */

vi.mock('../api/client', () => ({ apiClient: { get: vi.fn() } }));

const ITEMS: SuggestItem[] = [
  { value: 'Комарова 36', group: 'Избранные стройки' },
  { value: 'Берёзовая 4', group: 'Остальные стройки' },
  { value: 'Комарова 36А', group: 'Остальные стройки' },
  { value: 'Склад', label: 'Склад — в архиве', group: 'Вне строек' },
];

let changes: string[] = [];

function Field({ options = ITEMS }: { options?: SuggestItem[] }) {
  const [value, setValue] = useState('');
  // Без QueryClientProvider нарочно: поле с перечнем на кэш запросов не подписывается.
  return <SuggestField label="Значение" options={options} value={value}
    onChange={v => { changes.push(v); setValue(v); }} />;
}

const box = () => screen.getByRole('combobox', { name: 'Значение' }) as HTMLInputElement;
const list = () => screen.queryByRole('listbox', { hidden: true });
const shownTexts = () => screen.queryAllByRole('option', { hidden: true }).map(o => o.textContent);
const groups = () => [...(list()?.querySelectorAll('[role="presentation"]') ?? [])].map(g => g.textContent);
const type = (text: string) => fireEvent.change(box(), { target: { value: text } });

afterEach(() => { cleanup(); changes = []; vi.resetAllMocks(); });

describe('SuggestField: перечень вместо адреса (UI-55)', () => {
  it('набор открывает список сразу и не шлёт ни одного запроса', () => {
    render(<Field />);
    type('ком');
    expect(box().getAttribute('aria-expanded')).toBe('true');
    expect(shownTexts()).toEqual(['Комарова 36', 'Комарова 36А']);
    expect(apiClient.get).not.toHaveBeenCalled();
  });

  it('ничего не совпало — списка нет, и «не найдено» не пишется', () => {
    render(<Field />);
    type('ленина');
    expect(box().getAttribute('aria-expanded')).toBe('false');
    expect(list()).toBeNull();
    expect(screen.queryByText(/не найден/i)).toBeNull();
  });

  it('ё и е одно и то же, совпадение выделено жирным', () => {
    render(<Field />);
    type('берез');
    expect(shownTexts()).toEqual(['Берёзовая 4']);
    expect(list()!.querySelector('.font-semibold')!.textContent).toBe('Берёз');
  });

  it('архивный пункт виден с пометкой, а в поле уходит значение', () => {
    render(<Field />);
    type('скл');
    expect(shownTexts()).toEqual(['Склад — в архиве']);
    fireEvent.keyDown(box(), { key: 'ArrowDown' });
    fireEvent.keyDown(box(), { key: 'Enter' });
    expect(box().value).toBe('Склад');
    expect(changes.at(-1)).toBe('Склад');
  });

  it('подпись группы стоит над её пунктами и пропадает, когда их в списке нет', () => {
    render(<Field />);
    type('ком');
    expect(groups()).toEqual(['Избранные стройки', 'Остальные стройки']);
    type('скл');
    expect(groups()).toEqual(['Вне строек']);
  });

  it('приписка — справа, отдельно от подписи', () => {
    render(<Field options={[{ value: 'Склад', note: 'и статья вне строек' }]} />);
    type('скл');
    expect(screen.getByText('и статья вне строек')).toBeTruthy();
  });
});

describe('matchOptions: что попадает в список', () => {
  it('с начала слова, а не из середины', () => {
    expect(matchOptions(ITEMS, '36').map(i => i.value)).toEqual(['Комарова 36', 'Комарова 36А']);
    expect(matchOptions(ITEMS, 'ова')).toEqual([]);
  });

  it('по значению, а не по подписи: слово пометки пункт не находит', () => {
    expect(matchOptions(ITEMS, 'архив')).toEqual([]);
  });

  it('не больше десяти пунктов, в порядке перечня', () => {
    const many = Array.from({ length: 15 }, (_, i) => ({ value: `Стройка ${i + 1}` }));
    expect(matchOptions(many, 'стр').map(i => i.value)).toEqual(many.slice(0, SUGGEST_LIMIT).map(i => i.value));
  });
});
