import { describe, it, expect } from 'vitest';
import { parseSchemaFields } from './schema';
import { schemaToJson } from '@/features/settings/schemaConstants';

/**
 * Круг схемы: разобрали → показали → сохранили (issue #1008).
 *
 * Редактор типа — ЕДИНСТВЕННЫЙ путь правки схемы, и поле он пересобирает ПОИМЁННО. Значит всякое
 * свойство, которого в перечне не оказалось, стирается при первом же сохранении — молча. Так уже
 * терялось происхождение поля (ревью PR #1004) и следом замок (#1008): второе стоило типу
 * возможности сохраниться вовсе — сервер отвечал «нельзя снять замок» на любую правку.
 *
 * ⚠️ Поле ниже — ОБЪЯВЛЕНИЕ контракта, а не пример. Добавили свойство в `SchemaField` — добавьте
 * его сюда; иначе сторож честно скажет, что круг его не переживает, только про старые свойства.
 */
const FIELD_WITH_EVERYTHING = {
  key: 'Сумма',
  title: 'Сумма',
  type: 'number',
  typeId: 'a0000000-0000-0000-0000-000000000001',
  options: ['a', 'b'],
  required: true,
  defaultValue: 10,
  tags: ['doc.total'],
  computed: false,
  expression: 'get("Кол") * 2',
  origin: 'module',
  locked: true,
  moduleTitle: 'Сумма по журналу',
};

describe('круг схемы', () => {
  it('поле переживает разбор и сериализацию без потерь', () => {
    const parsed = parseSchemaFields({ fields: [FIELD_WITH_EVERYTHING] });
    const saved = JSON.parse(schemaToJson(parsed, [], {}));
    const field = saved.fields[0];

    for (const [key, value] of Object.entries(FIELD_WITH_EVERYTHING))
      expect({ [key]: field[key] }).toEqual({ [key]: value });
  });

  it('метки модуля — то, ради чего сторож стоит', () => {
    // Отдельным утверждением, а не строкой в цикле: потеря именно этих трёх свойств стоит не
    // неудобства, а неработающей записи — замок снимается молча, тип перестаёт сохраняться, а
    // подпись поля модуля навсегда числится правленной человеком.
    const parsed = parseSchemaFields({ fields: [FIELD_WITH_EVERYTHING] });

    expect(parsed[0].origin).toBe('module');
    expect(parsed[0].locked).toBe(true);
    expect(parsed[0].moduleTitle).toBe('Сумма по журналу');
  });

  it('поле заказчика не обрастает метками из ниоткуда', () => {
    const parsed = parseSchemaFields({ fields: [{ key: 'Своё', title: 'Своё', type: 'string' }] });
    const saved = JSON.parse(schemaToJson(parsed, [], {}));

    expect(saved.fields[0].origin).toBeUndefined();
    expect(saved.fields[0].locked).toBeUndefined();
    expect(saved.fields[0].moduleTitle).toBeUndefined();
  });
});
