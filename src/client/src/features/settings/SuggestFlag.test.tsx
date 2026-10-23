// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { SchemaField } from '@/shared/api/schema';
import { SUGGEST_REFUSAL_TEXT, chipTagEntries, tagsForKind, withoutFlagTag, type SuggestRefusal } from '@/shared/api/suggestable';
import type { TagDefinition } from '@/shared/api/tags';
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

describe('тэг input.noSuggest — флажок, пока флажок показан', () => {
  const tag = (code: string): TagDefinition => ({ code, scope: 'Field', appliesTo: [] }) as unknown as TagDefinition;
  const offered = [tag('identity'), tag('input.noSuggest')];

  it('у подсказываемой строки — не чип и не пункт выбора; запись с номером у прочих сохраняется', () => {
    const f = field({ tags: ['identity:2', 'input.noSuggest'] });
    expect(chipTagEntries(f)).toEqual(['identity:2']);
    expect(withoutFlagTag(f, offered).map(t => t.code)).toEqual(['identity']);
    expect(chipTagEntries(field())).toEqual([]);
  });

  it('флажка нет (однозначное, запертое, не строка) — тэг остаётся чипом, его можно снять', () => {
    for (const f of [field({ tags: ['org.inn', 'input.noSuggest'] }), field({ locked: true, tags: ['input.noSuggest'] }),
      field({ type: 'text', tags: ['input.noSuggest'] })]) {
      expect(chipTagEntries(f)).toContain('input.noSuggest');
      expect(withoutFlagTag(f, offered).map(t => t.code)).toContain('input.noSuggest');
    }
  });

  it('смена вида со строки снимает тэг, прочие тэги целы; строка его сохраняет', () => {
    expect(tagsForKind(['identity:2', 'input.noSuggest'], 'text')).toEqual(['identity:2']);
    expect(tagsForKind(['input.noSuggest'], 'number')).toBeUndefined();
    const kept = ['identity:2', 'input.noSuggest'];
    expect(tagsForKind(kept, 'string')).toBe(kept);
    expect(tagsForKind(undefined, 'text')).toBeUndefined();
  });
});
