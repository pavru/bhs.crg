// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { SchemaField } from '@/shared/api/schema';
import { SCAN_NOTE } from '@/shared/ui/fieldBox';
import { PrimitiveInput } from './PrimitiveInput';

/**
 * Проброс метки «со скана» (costs-invoice-screen-13-undo, D-13.6): `PrimitiveInput` своего знака не рисует,
 * а отдаёт `fromScan` полю `shared/ui`. Документы его не задают — их поля прежние.
 */

afterEach(cleanup);

const field = (over: Partial<SchemaField> = {}): SchemaField =>
  ({ key: 'Объект', title: 'Объект', type: 'string', required: false, ...over }) as SchemaField;
const marks = () => document.querySelectorAll('[data-field-scan-mark]');

describe('PrimitiveInput и метка «со скана»', () => {
  it('без `fromScan` — поле прежнее: ни знака, ни приписки (так рисуются реквизиты документов)', () => {
    render(<PrimitiveInput field={field()} value="Склад 3" onChange={() => {}} invalid={false} label="Объект" />);
    expect(marks()).toHaveLength(0);
    expect(screen.getByRole('textbox', { name: 'Объект' })).toBeTruthy();
  });

  it('с `fromScan` — знак поля и приписка для диктора; у текста и у поля только для чтения тоже', () => {
    render(<>
      <PrimitiveInput field={field()} value="Склад 3" onChange={() => {}} invalid={false} label="Объект" fromScan />
      <PrimitiveInput field={field({ key: 'Примечание', type: 'text' })} value="…" onChange={() => {}} invalid={false}
        label="Примечание" fromScan />
      <PrimitiveInput field={field({ key: 'Код' })} value="7" onChange={() => {}} invalid={false} label="Код" readOnly fromScan />
    </>);
    expect(marks()).toHaveLength(3);
    expect(screen.getByRole('textbox', { name: `Объект (${SCAN_NOTE})` })).toBeTruthy();
  });

  it('с замком в хвосте знака «со скана» нет: значение кладёт источник ([UI-88])', () => {
    render(<PrimitiveInput field={field()} value="7" onChange={() => {}} invalid={false} label="Код" readOnly fromScan
      trailing={<span>замок</span>} trailingMark />);
    expect(marks()).toHaveLength(0);
  });
});
