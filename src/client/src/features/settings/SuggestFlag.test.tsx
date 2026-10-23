// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { SchemaField } from '@/shared/api/schema';
import { SUGGEST_REFUSAL_TEXT, type SuggestRefusal } from '@/shared/api/suggestable';
import { offeredInRoles, shownTagEntries, type TagDefinition } from '@/shared/api/tags';
import { SuggestFlag, SuggestOffBadge } from './SuggestFlag';

/** Флажок «Подсказывать введённое ранее» в карточке поля (issue #1533, вариант В макета). */

afterEach(cleanup);

const field = (over: Partial<SchemaField> = {}): SchemaField =>
  ({ key: 'Производитель', title: 'Производитель', type: 'string', required: false, ...over }) as SchemaField;
const flag = () => screen.queryByRole('checkbox', { name: /Подсказывать введённое ранее/ }) as HTMLInputElement | null;

describe('флажок по коду правила', () => {
  it('подсказывается — флажок стоит; снятие ставит input.noSuggest, остальные тэги целы', () => {
    const change = vi.fn();
    render(<SuggestFlag field={field({ tags: ['identity:2'] })} onChange={change} />);
    expect(flag()!.checked).toBe(true);
    fireEvent.click(flag()!);
    expect(change).toHaveBeenCalledWith({ tags: ['identity:2', 'input.noSuggest'] });
  });

  it('выключено вручную — флажок снят; установка убирает тэг, а без тэгов — и сам список', () => {
    const change = vi.fn();
    render(<SuggestFlag field={field({ tags: ['input.noSuggest'] })} onChange={change} />);
    expect(flag()!.checked).toBe(false);
    fireEvent.click(flag()!);
    expect(change).toHaveBeenCalledWith({ tags: undefined });
  });

  it('многострочный текст и вычисляемое — ни флажка, ни причины', () => {
    const { container, rerender } = render(<SuggestFlag field={field({ type: 'text' })} onChange={() => {}} />);
    expect(container.textContent).toBe('');
    rerender(<SuggestFlag field={field({ computed: true })} onChange={() => {}} />);
    expect(container.textContent).toBe('');
  });

  const refusals: [SuggestRefusal, Partial<SchemaField>][] = [
    ['locked', { locked: true }],
    ['inn', { tags: ['org.inn'] }],
    ['docNumber', { tags: ['doc.number'] }],
    ['personnelNumber', { tags: ['employee.personnelNumber'] }],
    ['article', { tags: ['nomenclature.article'] }],
    ['orgName', { tags: ['org.name'] }],
    ['account', { tags: ['employee.account'] }],
  ];
  it.each(refusals)('%s — строка причины вместо флажка', (code, over) => {
    render(<SuggestFlag field={field(over)} onChange={() => {}} />);
    expect(flag()).toBeNull();
    expect(screen.getByText(SUGGEST_REFUSAL_TEXT[code])).toBeTruthy();
  });
});

describe('пометка в шапке карточки', () => {
  it('«без подсказки» — только у выключенного вручную', () => {
    const { rerender } = render(<SuggestOffBadge field={field({ tags: ['input.noSuggest'] })} />);
    expect(screen.getByText('без подсказки')).toBeTruthy();
    rerender(<SuggestOffBadge field={field()} />);
    expect(screen.queryByText('без подсказки')).toBeNull();
    // Однозначное поле не подсказывается и так: пометка о ручном выключении солгала бы о причине.
    rerender(<SuggestOffBadge field={field({ tags: ['org.inn', 'input.noSuggest'] })} />);
    expect(screen.queryByText('без подсказки')).toBeNull();
  });
});

describe('тэг со своим контролом чипом не показывается', () => {
  const tag = (code: string): TagDefinition => ({ code, scope: 'Field', appliesTo: [] }) as unknown as TagDefinition;

  it('чипы карточки — без input.noSuggest, запись с номером у прочих сохраняется', () => {
    expect(shownTagEntries(['identity:2', 'input.noSuggest'])).toEqual(['identity:2']);
    expect(shownTagEntries(undefined)).toEqual([]);
  });

  it('в списке выбора тэгов его нет — и при отборе по ролям, и без него', () => {
    const all = [tag('identity'), tag('input.noSuggest')];
    expect(offeredInRoles(all).map(t => t.code)).toEqual(['identity']);
    expect(offeredInRoles(all, [], ['input.noSuggest']).map(t => t.code)).toEqual(['identity']);
  });
});
