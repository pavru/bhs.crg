// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { SchemaField } from '@/shared/api/schema';
import type { EnumTypeDef, PrimitiveTypeDef } from '@/shared/api/types';
import { formatDateInMask } from '@/shared/format/format';
import { IconButton } from '@/shared/ui/Button';
import { Close } from '@/shared/ui/icons';
import { PrimitiveInput } from './PrimitiveInput';

/**
 * Значение реквизита из источника и под замком — поле `shared/ui` только для чтения ([UI-86], вариант А):
 * подпись в вырезе, без подложки, хвост и ошибка у самого поля. Сырого `<input>` со своей рамкой больше нет.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(cleanup);

const field = (type: SchemaField['type'], over: Partial<SchemaField> = {}): SchemaField =>
  ({ key: 'Поле', title: 'Поле', type, required: true, ...over }) as SchemaField;

const primitive = (baseType: PrimitiveTypeDef['baseType'], constraints: PrimitiveTypeDef['constraints'] = {}) =>
  ({ id: 'p', name: 'Номер договора', code: 'n', baseType, description: 'Как в договоре', constraints,
    allowedTags: [], group: null, createdAt: '', updatedAt: '' }) as PrimitiveTypeDef;

const statuses = { id: 'e', values: [{ code: 'draft', label: 'Черновик' }] } as unknown as EnumTypeDef;

const cases: [string, SchemaField, unknown, Partial<Parameters<typeof PrimitiveInput>[0]>, string][] = [
  ['текст', field('text'), 'Выполнение работ', {}, 'Выполнение работ'],
  ['перечисление — подпись варианта, не код', field('enum', { typeId: 'e' }), 'draft', { enumTypeDef: statuses }, 'Черновик'],
  ['примитив-строка', field('primitive', { typeId: 'p' }), 'Д-42', { primitiveTypeDef: primitive('string') }, 'Д-42'],
  ['строка', field('string'), 'ООО «Ромашка»', {}, 'ООО «Ромашка»'],
  ['число', field('number'), 12.5, {}, '12.5'],
  ['дата — готовым текстом', field('date'), '2026-05-12', {}, formatDateInMask('2026-05-12')],
  ['примитив-дата — по точности типа', field('primitive', { typeId: 'p' }), '2026-05-12',
    { primitiveTypeDef: primitive('date', { datePrecision: 'month' }) }, formatDateInMask('2026-05-12', 'month')],
];

describe('PrimitiveInput: поле только для чтения ([UI-86])', () => {
  it.each(cases)('%s — поле shared/ui с подписью в вырезе, без подложки и звёздочки', (_, f, value, extra, shown) => {
    const { container } = render(<PrimitiveInput field={f} value={value} label="Номер договора" onChange={() => {}}
      invalid={false} readOnly {...extra} />);
    const input = screen.getByRole('textbox', { name: 'Номер договора' }) as HTMLInputElement | HTMLTextAreaElement;
    expect(input.readOnly).toBe(true);
    expect(input.value).toBe(shown);
    // Подпись — в вырезе рамки, а не строкой над полем.
    expect(container.querySelector('legend')).not.toBeNull();
    expect(container.querySelector('.bg-surface-container')).toBeNull();
    expect(container.querySelector('fieldset')!.className).toContain('border-outline-variant');
    // Звёздочки у поля только для чтения нет (#958): заполняет его не человек.
    expect(container.textContent).not.toContain('*');
  });

  it('хвост, ошибка на месте подсказки и пояснение знаков для диктора', () => {
    render(<>
      <span id="origin">Значение распознано со скана</span>
      <PrimitiveInput field={field('string')} value="" label="Номер договора" onChange={() => {}} invalid
        readOnly hint="Поле заполняет модуль" error="Обязательное поле" describedBy="origin"
        trailing={<><span data-testid="lock" /><IconButton label="Привязка к источнику" icon={Close} /></>} trailingMark />
    </>);
    const input = screen.getByRole('textbox', { name: 'Номер договора' });
    expect(screen.getByRole('button', { name: 'Привязка к источнику' })).toBeDefined();
    expect(screen.getByTestId('lock')).toBeDefined();
    // Ошибка заняла место подсказки — отдельной строки под полем нет.
    expect(screen.queryByText('Поле заполняет модуль')).toBeNull();
    const described = (input.getAttribute('aria-describedby') ?? '').split(/\s+/)
      .map(id => document.getElementById(id)?.textContent);
    expect(described).toEqual(expect.arrayContaining(['Обязательное поле', 'Значение распознано со скана']));
    // Знак перед кнопкой — место хвоста шире.
    expect(input.className).toContain('pr-[calc(4.125rem+var(--field-mark,0px))]');
  });

  it('многострочный текст получает хвост и ошибку тоже', () => {
    render(<PrimitiveInput field={field('text')} value="" label="Предмет" onChange={() => {}} invalid readOnly
      error="Обязательное поле" trailing={<IconButton label="Привязка к источнику" icon={Close} />} />);
    expect(screen.getByRole('button', { name: 'Привязка к источнику' })).toBeDefined();
    expect(screen.getByText('Обязательное поле')).toBeDefined();
  });

  it('без подписи — ошибка типов', () => {
    // @ts-expect-error `label` обязателен (D7 sharedui-md3-19): режим без подписи был сырым полем
    render(<PrimitiveInput field={field('string')} value="x" onChange={() => {}} invalid={false} readOnly />);
  });
});
