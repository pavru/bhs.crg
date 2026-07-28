import { describe, it, expect } from 'vitest';
import {
  buildRows, referencingFiles, resolveRelative, validatePath,
} from './userLibTree';
import type { UserLibFile } from '@/shared/api/typstUserLib';

const f = (path: string, content = ''): UserLibFile => ({ path, content });

describe('buildRows', () => {
  it('раскладывает папки заголовками, файлы — с отступом', () => {
    const rows = buildRows([f('gost/forms/f3.typ'), f('util/text.typ'), f('root.typ')]);
    expect(rows.map(r => `${r.kind}:${r.label}:${r.depth}`)).toEqual([
      'file:root.typ:0',
      'folder:gost:0', 'folder:forms:1', 'file:f3.typ:2',
      'folder:util:0', 'file:text.typ:1',
    ]);
  });

  it('не повторяет общий сегмент соседних папок', () => {
    const rows = buildRows([f('gost/forms/f3.typ'), f('gost/tables/t1.typ')]);
    expect(rows.filter(r => r.kind === 'folder').map(r => r.path))
      .toEqual(['gost', 'gost/forms', 'gost/tables']);
  });

  it('порядок устойчив — строки не прыгают между сохранениями', () => {
    const a = buildRows([f('b.typ'), f('a.typ')]).map(r => r.path);
    const b = buildRows([f('a.typ'), f('b.typ')]).map(r => r.path);
    expect(a).toEqual(b);
  });
});

describe('resolveRelative', () => {
  it('поднимается по «..» от папки файла', () =>
    expect(resolveRelative('gost/forms', '../../util/text.typ')).toBe('util/text.typ'));

  it('файл в корне дерева адресует соседа без префикса', () =>
    expect(resolveRelative('', 'b.typ')).toBe('b.typ'));

  it('выход выше дерева — не наш файл', () =>
    expect(resolveRelative('gost', '../../../data.json')).toBeNull());
});

/** Перед удалением надо сказать поимённо, что сломается, — автоправку чужого кода не делаем. */
describe('referencingFiles', () => {
  it('находит ссылающихся по относительному импорту', () => {
    const files = [
      f('gost/forms/f3.typ', '#import "../../util/text.typ": shout'),
      f('util/text.typ', '#let shout(s) = upper(s)'),
    ];
    expect(referencingFiles(files, 'util/text.typ', '')).toEqual(['gost/forms/f3.typ']);
  });

  it('координаты пакетов не считаются ссылкой на файл', () => {
    const files = [f('a.typ', '#import "@preview/cetz:0.3.1": canvas')];
    expect(referencingFiles(files, 'cetz', '')).toEqual([]);
  });

  /**
   * Точка входа ссылается иначе — из корня, через префикс `userlib/`. Пока приложение само правило
   * её при переименовании (#473), молчание об этой ссылке было безобидным; после отказа от
   * автоматики (#492) оно означало бы, что пользователь переименует файл и узнает о поломке, только
   * когда встанет генерация всех документов.
   */
  it('точка входа считается ссылающейся и называется первой', () => {
    const files = [f('gost/f3.typ', '#let place-f3() = []')];
    const entry = '#import "userlib/gost/f3.typ": *';
    expect(referencingFiles(files, 'gost/f3.typ', entry)).toEqual(['userlib.typ']);
  });

  it('точка входа без ссылки на этот файл не попадает в список', () => {
    const files = [f('a.typ', ''), f('b.typ', '')];
    expect(referencingFiles(files, 'a.typ', '#import "userlib/b.typ": *')).toEqual([]);
  });

  it('пустая точка входа — только файлы дерева', () =>
    expect(referencingFiles([f('a.typ', '#import "b.typ": *')], 'b.typ', '')).toEqual(['a.typ']));

  /**
   * Пока строку писало приложение, она всегда была канонической. Теперь импорты ведёт пользователь
   * (#492), и «./userlib/…» — обычная запись; без нормализации ссылка не нашлась бы, а удаление
   * файла прошло бы без предупреждения.
   */
  it('точка входа с «./» тоже считается ссылающейся', () =>
    expect(referencingFiles([f('gost/f3.typ', '')], 'gost/f3.typ', '#import "./userlib/gost/f3.typ": *'))
      .toEqual(['userlib.typ']));

  /**
   * Импорты ведёт пользователь (#492), поэтому временно закомментировать строку — обычное действие.
   * Считая её живой, мы обещали бы «на файл ссылаются» там, где ссылки нет (#498).
   */
  it('закомментированный импорт не считается ссылкой', () => {
    expect(referencingFiles([f('a.typ', '// #import "b.typ": *')], 'b.typ', '')).toEqual([]);
    expect(referencingFiles([f('a.typ', '/* #import "b.typ": * */')], 'b.typ', '')).toEqual([]);
    expect(referencingFiles([], 'gost/f3.typ', '// #import "userlib/gost/f3.typ": *')).toEqual([]);
  });

  it('никто не ссылается — пусто', () =>
    expect(referencingFiles([f('a.typ', ''), f('b.typ', '')], 'a.typ', '')).toEqual([]));
});

describe('validatePath', () => {
  it.each([
    ['', 'пустой'],
    ['/abs.typ', 'абсолютный'],
    ['a.txt', 'не .typ'],
    ['../a.typ', 'выход наружу'],
    ['a//b.typ', 'пустой сегмент'],
  ])('отклоняет %s (%s)', (path) => expect(validatePath(path, [])).not.toBeNull());

  it('принимает обычный путь', () => expect(validatePath('gost/forms/f3.typ', [])).toBeNull());

  it('ловит дубль пути', () => expect(validatePath('a.typ', ['a.typ'])).not.toBeNull());

  /** На Linux это разные файлы, на Windows — один; разошлось бы только в продакшене. */
  it('ловит расхождение только по регистру', () =>
    expect(validatePath('A.typ', ['a.typ'])).not.toBeNull());

  it('переименование файла в себя же не считается дублем', () =>
    expect(validatePath('a.typ', ['a.typ'], 'a.typ')).toBeNull());
});
