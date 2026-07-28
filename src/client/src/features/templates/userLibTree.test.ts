import { describe, it, expect } from 'vitest';
import {
  buildRows, referencingFiles, resolveRelative, validatePath,
  mergeFiles, mergeText, treeKey, checkStillApplies,
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

  /**
   * Снимая сперва ВСЕ блочные комментарии, а потом строчные, мы позволяли `/*` внутри строчного
   * открыть мнимый блок и съесть живой импорт ниже — удаление файла прошло бы без предупреждения,
   * и фронт разошёлся бы с сервером в понимании одного и того же файла (#500).
   */
  it('«/*» внутри строчного комментария не съедает импорт ниже', () => {
    const entry = [
      '// старый вариант ниже /*',
      '#import "userlib/a.typ": *',
      '// */',
    ].join('\n');
    expect(referencingFiles([f('a.typ', '')], 'a.typ', entry)).toEqual(['userlib.typ']);
  });

  it('никто не ссылается — пусто', () =>
    expect(referencingFiles([f('a.typ', ''), f('b.typ', '')], 'a.typ', '')).toEqual([]));

  /**
   * Строковые литералы переживают снятие комментариев (#501): иначе `/*` внутри строки открывал бы
   * мнимый блок и уносил импорты ниже — та же потеря диагностики, что и в предыдущем случае, этажом
   * ниже.
   */
  it('«/*» внутри строки не съедает импорт ниже', () => {
    const entry = [
      '#let fence = "/*"',
      '#import "userlib/a.typ": *',
      '/* настоящий комментарий */',
    ].join('\n');
    expect(referencingFiles([f('a.typ', '')], 'a.typ', entry)).toEqual(['userlib.typ']);
  });

  it('«//» в ссылке не съедает остаток своей строки', () => {
    const entry = '#let site = "https://typst.app"\n#import "userlib/a.typ": *';
    expect(referencingFiles([f('a.typ', '')], 'a.typ', entry)).toEqual(['userlib.typ']);
  });

  /** Непарная кавычка в разметке не должна проглотить полфайла до следующей кавычки. */
  it('непарная кавычка не ломает разбор ниже', () => {
    const entry = '#let note = [Кабель "ВВГнг проложен]\n#import "userlib/a.typ": *';
    expect(referencingFiles([f('a.typ', '')], 'a.typ', entry)).toEqual(['userlib.typ']);
  });

  /**
   * Блочные комментарии Typst вложенные, и закомментировать область, где комментарий уже есть, —
   * обычное действие редактора. Нежадное регулярное выражение закрывало блок на первом внутреннем
   * `*\/`, оставляя импорт живым: файл числился подключённым, а его имена шли в проверку дубликатов
   * (#504).
   */
  it('вложенный блочный комментарий закрывается на своём «*/»', () => {
    const entry = '/* черновик /* внутри */ #import "userlib/a.typ": * */';
    expect(referencingFiles([f('a.typ', '')], 'a.typ', entry)).toEqual([]);
  });

  it('после закрытия вложенного блока разбор продолжается', () => {
    const entry = '/* /* */ */\n#import "userlib/a.typ": *';
    expect(referencingFiles([f('a.typ', '')], 'a.typ', entry)).toEqual(['userlib.typ']);
  });

  /**
   * Ведущий «/» Typst считает от корня проекта, а не от папки файла (проверено на 0.15.1: такой
   * импорт из файла дерева компилируется). Разрешая его как относительный, мы приставляли папку
   * файла и ссылку молча не находили — удаление проходило бы без предупреждения (#505).
   */
  it('импорт от корня проекта из файла дерева считается ссылкой', () => {
    const files = [
      f('gost/f3.typ', '#import "/userlib/util/text.typ": *'),
      f('util/text.typ', '#let shout(s) = upper(s)'),
    ];
    expect(referencingFiles(files, 'util/text.typ', '')).toEqual(['gost/f3.typ']);
  });

  it('путь от корня мимо userlib/ нашим файлом не считается', () => {
    const files = [f('gost/f3.typ', '#import "/typeblocks.typ": *'), f('typeblocks.typ', '')];
    expect(referencingFiles(files, 'typeblocks.typ', '')).toEqual([]);
  });
});

/**
 * Слияние по базе — вместо заслона «есть несохранённое, серверные данные ждут» (#501): тот заслон
 * закрывался бы навсегда от чужого нового файла, а «Сохранить всё» отправило бы дерево без него.
 */
describe('mergeFiles', () => {
  const base = [f('a.typ', 'A'), f('b.typ', 'B')];

  it('первая загрузка — целиком серверное дерево', () =>
    expect(mergeFiles([], null, base)).toEqual(base));

  it('локальная правка переживает перечитывание', () => {
    const local = [f('a.typ', 'A-правка'), f('b.typ', 'B')];
    expect(mergeFiles(local, base, base)).toEqual(local);
  });

  it('чужой новый файл приходит, наша правка остаётся', () => {
    const local = [f('a.typ', 'A-правка'), f('b.typ', 'B')];
    const server = [...base, f('c.typ', 'C')];
    expect(mergeFiles(local, base, server))
      .toEqual([f('a.typ', 'A-правка'), f('b.typ', 'B'), f('c.typ', 'C')]);
  });

  it('чужая правка нетронутого нами файла принимается', () => {
    const server = [f('a.typ', 'A'), f('b.typ', 'B-сосед')];
    expect(mergeFiles(base, base, server)).toEqual(server);
  });

  it('удалённый локально файл не воскресает', () => {
    const local = [f('a.typ', 'A')];
    expect(mergeFiles(local, base, base)).toEqual(local);
  });

  it('созданный локально файл не пропадает', () => {
    const local = [...base, f('new.typ', 'N')];
    expect(mergeFiles(local, base, base)).toEqual(local);
  });

  /**
   * Обычный ход: создали файл, нажали Ctrl+S и продолжили печатать, пока перечитывание в пути. База
   * этого файла ещё не знает, сервер уже знает — и серверная копия молча уносила бы набранное с
   * момента сохранения, причём без точки-маркера (#502).
   */
  it('печать поверх только что сохранённого файла не теряется', () => {
    const local = [f('a.typ', 'A'), f('new.typ', '// new\n#let g() = []')];
    const server = [f('a.typ', 'A'), f('new.typ', '// new\n')];
    expect(mergeFiles(local, base.slice(0, 1), server)).toEqual(local);
  });

  /** Удаление соседом против нашей несохранённой правки: терять несохранённое хуже. */
  it('файл, удалённый на сервере, остаётся при нашей правке', () => {
    const local = [f('a.typ', 'A-правка'), f('b.typ', 'B')];
    expect(mergeFiles(local, base, [f('b.typ', 'B')])).toEqual([f('b.typ', 'B'), f('a.typ', 'A-правка')]);
  });

  it('файл, удалённый на сервере и не правленный нами, уходит', () =>
    expect(mergeFiles(base, base, [f('b.typ', 'B')])).toEqual([f('b.typ', 'B')]));

  /** React StrictMode прогоняет эффект дважды — второй прогон не должен ничего менять. */
  it('повторное слияние с тем же сервером устойчиво', () => {
    const local = [f('a.typ', 'A-правка'), f('b.typ', 'B')];
    const once = mergeFiles(local, base, base);
    expect(mergeFiles(once, base, base)).toEqual(once);
  });
});

/**
 * Отпечаток проверенного дерева: по нему видно, относится ли последняя проверка собираемости к тому,
 * что сейчас на сервере. По времени это не решается — сохранение само запускает перечитывание.
 */
describe('treeKey', () => {
  it('порядок файлов не значим — сервер волен вернуть их иначе', () =>
    expect(treeKey('E', [f('b.typ', 'B'), f('a.typ', 'A')]))
      .toBe(treeKey('E', [f('a.typ', 'A'), f('b.typ', 'B')])));

  it('правка содержимого меняет отпечаток', () =>
    expect(treeKey('E', [f('a.typ', 'A')])).not.toBe(treeKey('E', [f('a.typ', 'A2')])));

  it('правка точки входа меняет отпечаток', () =>
    expect(treeKey('E', [f('a.typ', 'A')])).not.toBe(treeKey('E2', [f('a.typ', 'A')])));

  it('новый файл меняет отпечаток', () =>
    expect(treeKey('E', [f('a.typ', 'A')])).not.toBe(treeKey('E', [f('a.typ', 'A'), f('b.typ', '')])));
});

/**
 * Предикат вынесен из панели именно затем, чтобы это проверялось тестом: живой проверкой два его
 * условия неразличимы — экран одинаково молчит и когда отпечатки совпали, и когда сработало
 * временное условие (#505).
 */
describe('checkStillApplies', () => {
  const base = { checkedKey: 'K', checkedAt: 100, serverKey: 'K', dataUpdatedAt: 200 };

  it('проверки в этом сеансе не было — не относится', () =>
    expect(checkStillApplies({ ...base, checkedKey: null })).toBe(false));

  it('отпечатки совпали — относится, даже если чтение свежее', () =>
    expect(checkStillApplies(base)).toBe(true));

  /** Сосед сохранил своё между нашим PUT и перечитыванием: наша проверка описывает не то дерево. */
  it('отпечатки разошлись, чтение свежее проверки — не относится', () =>
    expect(checkStillApplies({ ...base, serverKey: 'иной' })).toBe(false));

  /** Перечитывание после сохранения не дошло: проверка описывает дерево свежее прочитанного. */
  it('отпечатки разошлись, но проверка свежее чтения — относится', () =>
    expect(checkStillApplies({ ...base, serverKey: 'иной', checkedAt: 300 })).toBe(true));
});

describe('mergeText', () => {
  it('первая загрузка — серверный текст', () => expect(mergeText('', null, 'S')).toBe('S'));
  it('не правили — берём серверный', () => expect(mergeText('B', 'B', 'S')).toBe('S'));
  it('правили — правка остаётся', () => expect(mergeText('моё', 'B', 'S')).toBe('моё'));
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
