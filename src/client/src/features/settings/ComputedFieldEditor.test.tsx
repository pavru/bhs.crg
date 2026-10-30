// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { SchemaField } from '@/shared/api/schema';
import { Modal } from '@/shared/ui/Modal';
import { ComputedFieldEditor } from './ComputedFieldEditor';

/** «+ Вставить поле» у расчётного поля — слой над окном ([UI-49], #1537). */

afterEach(cleanup);

const SIBLINGS = [
  { key: 'Количество', title: 'Количество', type: 'number' },
  { key: 'Цена', title: 'Цена за единицу', type: 'number' },
];

function Editor() {
  const [field, setField] = useState<SchemaField>({ key: 'Сумма', title: 'Сумма', type: 'number', computed: true, expression: 'get("Количество") * ' } as SchemaField);
  return <ComputedFieldEditor field={field} siblingFields={SIBLINGS} onChange={p => setField(f => ({ ...f, ...p }))} />;
}

function setup() {
  const onOpenChange = vi.fn();
  render(<Modal open onOpenChange={onOpenChange} title="Новый тип документа" isDirty={false}><Editor /></Modal>);
  return {
    onOpenChange,
    trigger: () => screen.getByRole('button', { name: '+ Вставить поле' }),
    // В jsdom у якоря нулевой прямоугольник, и `hideWhenDetached` прячет слой (visibility: hidden):
    // имени по ролям у скрытого нет — слой и пункт ищутся атрибутом и текстом. Проверяется поведение,
    // а не раскладка: раскладку проверяет живой прогон `types`.
    layer: () => document.querySelector<HTMLElement>('[role="dialog"][aria-label="Вставить поле"]'),
    formula: () => screen.getByPlaceholderText('get("Количество") * get("Цена")') as HTMLTextAreaElement,
  };
}

describe('ComputedFieldEditor: «+ Вставить поле»', () => {
  it('список — в портале вне окна, фокус — в фильтре', async () => {
    const user = userEvent.setup();
    const { trigger, layer } = setup();
    await user.click(trigger());
    const list = layer();
    expect(list).not.toBeNull();
    expect(screen.getByRole('dialog', { name: 'Новый тип документа' }).contains(list)).toBe(false);
    expect(document.activeElement).toBe(screen.getByPlaceholderText('Фильтр…'));
  });

  it('Esc закрывает список, а не окно, и возвращает фокус на кнопку', async () => {
    const user = userEvent.setup();
    const { trigger, layer, onOpenChange } = setup();
    await user.click(trigger());
    expect(layer()).not.toBeNull();
    await user.keyboard('{Escape}');
    await waitFor(() => expect(layer()).toBeNull());
    expect(onOpenChange).not.toHaveBeenCalled();
    await waitFor(() => expect(document.activeElement).toBe(trigger()));
  });

  it('щелчок в формулу закрывает список, окно остаётся', async () => {
    const user = userEvent.setup();
    const { trigger, layer, formula, onOpenChange } = setup();
    await user.click(trigger());
    expect(layer()).not.toBeNull();
    await user.click(formula());
    await waitFor(() => expect(layer()).toBeNull());
    expect(onOpenChange).not.toHaveBeenCalled();
  });

  it('выбор вставляет get("<ключ>") на место курсора и отдаёт фокус формуле', async () => {
    const user = userEvent.setup();
    const { trigger, layer, formula, onOpenChange } = setup();
    formula().setSelectionRange(formula().value.length, formula().value.length);
    await user.click(trigger());
    await user.keyboard('цен');
    await user.click(screen.getByText('Цена за единицу'));
    expect(formula().value).toBe('get("Количество") * get("Цена")');
    await waitFor(() => expect(layer()).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(formula()));
    expect(onOpenChange).not.toHaveBeenCalled();
  });
});
