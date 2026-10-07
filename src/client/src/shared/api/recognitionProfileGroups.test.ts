import { describe, expect, it } from 'vitest';
import {
  groupByModule, hiddenSummary, withBoundOption, type HiddenRecognitionProfile,
} from './recognitionProfileGroups';

const hidden = (id: string, name: string, moduleTitle: string | null = 'Исполнительная документация'): HiddenRecognitionProfile =>
  ({ id, name, kind: 'Table', kindLabel: 'Таблица', module: moduleTitle ? 'id' : null, moduleTitle });

describe('groupByModule', () => {
  it('складывает по владельцу и хранит порядок сервера внутри группы', () => {
    const groups = groupByModule([
      { id: 'b', module: 'id', moduleTitle: 'Исполнительная документация' },
      { id: 'i', module: 'core', moduleTitle: 'Общие' },
      { id: 'a', module: 'id', moduleTitle: 'Исполнительная документация' },
    ]);

    expect(groups.map(g => g.title)).toEqual(['Исполнительная документация', 'Общие']);
    expect(groups[0].items.map(p => p.id)).toEqual(['b', 'a']);
  });

  it('профиль без владельца не теряется, а получает свою группу', () => {
    const groups = groupByModule([{ id: 'x', module: null, moduleTitle: null }]);

    expect(groups).toHaveLength(1);
    expect(groups[0].title).toBe('Без модуля');
  });
});

describe('hiddenSummary', () => {
  it('скрывать нечего — строки нет', () => {
    expect(hiddenSummary([])).toBeNull();
  });

  it('склоняет число и называет модуль', () => {
    expect(hiddenSummary([hidden('1', 'a')])).toBe('Скрыт 1 профиль: модуль «Исполнительная документация» выключен');
    expect(hiddenSummary([hidden('1', 'a'), hidden('2', 'b'), hidden('3', 'c'), hidden('4', 'd')]))
      .toBe('Скрыто 4 профиля: модуль «Исполнительная документация» выключен');
  });

  it('несколько модулей перечислены, каждый один раз', () => {
    expect(hiddenSummary([hidden('1', 'a'), hidden('2', 'b'), hidden('3', 'c', 'Учёт работ')]))
      .toBe('Скрыто 3 профиля: модули «Исполнительная документация», «Учёт работ» выключены');
  });
});

describe('withBoundOption', () => {
  const offered = [{ id: 'p1', name: 'Спецификация' }];

  it('привязки нет или она среди предлагаемых — список не меняется', () => {
    expect(withBoundOption(offered, null, [])).toEqual(offered);
    expect(withBoundOption(offered, 'p1', [hidden('p1', 'x')])).toEqual(offered);
  });

  it('привязан профиль выключенного модуля — он назван и выбрать его нельзя', () => {
    expect(withBoundOption(offered, 'h1', [hidden('h1', 'Кабельный журнал')])).toEqual([
      ...offered,
      { id: 'h1', name: 'Кабельный журнал — модуль «Исполнительная документация» выключен', disabled: true },
    ]);
  });

  it('о привязанном профиле ещё ничего не известно — «недоступным» он не объявляется', () => {
    expect(withBoundOption(offered, 'h1', [])).toEqual(offered);
  });
});
