import { describe, it, expect } from 'vitest';
import { isLockedField } from './lockedFields';
import { isMissing } from './fieldValidation';
import type { SchemaField } from '@/shared/api/schema';

const field = (over: Partial<SchemaField> = {}): SchemaField =>
  ({ key: 'Табельный', title: 'Табельный номер', type: 'string', ...over }) as SchemaField;

describe('замок поля', () => {
  it('ставится только явным признаком', () => {
    expect(isLockedField(field({ locked: true }))).toBe(true);
    expect(isLockedField(field({ locked: false }))).toBe(false);
    expect(isLockedField(field())).toBe(false);
    // Происхождение — не замок: модуль ЗАВОДИТ поле «Табельный номер», а заполняет его человек
    // (issue #957). Спутать эти два признака значит запереть поле, которое некому заполнить.
    expect(isLockedField(field({ origin: 'module' }))).toBe(false);
  });

  it('снимает с поля обязательность заполнения', () => {
    // Обязательное поле с замком кладёт код модуля. Требовать его от человека — претензия без
    // пути: ввода у поля нет, и снять её он не сможет никогда.
    expect(isMissing(field({ required: true }), '')).toBe(true);
    expect(isMissing(field({ required: true, locked: true }), '')).toBe(false);
  });
});

/**
 * Сторож против расхождения форм по схеме (issue #958).
 *
 * Форм, рисующих поля схемы, три — реквизиты документа, документ качества и запись общих данных, —
 * и замок каждая из них обязана уважать сама: ввод, обязательность, прогресс, секция «заполняются
 * автоматически». Появись четвёртая (или отвались замок у одной из трёх), дыра была бы ТИХОЙ:
 * поле осталось бы редактируемым, отказ пришёл бы с сервера уже при сохранении и без указания,
 * какое из сорока полей его вызвало.
 *
 * Признак формы — импорт `PrimitiveInput`: им рисуется скалярное поле схемы, и без него формы по
 * схеме не бывает. Текстом, а не разбором синтаксиса: так же ловится импорт через общий индекс
 * модуля, каким он и написан во всех трёх формах.
 */
// Путь от корня проекта (ведущий «/»), а не относительный: относительный glob вернул бы соседей
// по каталогу как «./Имя.tsx», и правило «внутренности модуля полей не форма» их не узнало бы.
const sources = import.meta.glob('/src/**/*.tsx', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

/** Внутренности самого модуля полей: вложенные поля составного типа модуль не объявляет. */
const NOT_A_FORM = /\/fields\//;

describe('формы по схеме', () => {
  it('все до одной спрашивают про замок', () => {
    const offenders = Object.entries(sources)
      .filter(([file]) => !NOT_A_FORM.test(file))
      .filter(([, code]) => code.includes('PrimitiveInput'))
      .filter(([, code]) => !code.includes('isLockedField'))
      .map(([file]) => file)
      .sort();

    expect(offenders, 'форма рисует поля схемы, но не различает запертые (ТЗ CORE-20.2)').toEqual([]);
  });

  it('видит сами формы — иначе проверять было бы нечего', () => {
    const forms = Object.entries(sources)
      .filter(([file]) => !NOT_A_FORM.test(file))
      .filter(([, code]) => code.includes('PrimitiveInput'))
      .map(([file]) => file.replace(/^\/src\//, ''))
      .sort();

    // Список полный и поимённый: «нашлось хотя бы столько-то» прошло бы и на пустом стекле —
    // сломайся glob, первая проверка стала бы зелёной, ничего не проверяя.
    expect(forms).toEqual([
      'features/document-sets/catalog/index.tsx',
      'features/document-sets/editor/RequisitesTab.tsx',
      'features/quality-docs/QualityDocForm.tsx',
    ]);
  });
});
