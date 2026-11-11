import { describe, it, expect } from 'vitest';
import { extensionOf, imageAssetPath, assetReference, isValidImageAssetName, suggestAssetName } from './templateAssetRef';
import nameCases from './templateAssetNameCases.json';
import type { TemplateAssetDto } from './templateAssets';

const asset = (p: Partial<TemplateAssetDto>): TemplateAssetDto => ({
  id: '1', scope: 'System', scopeId: null, kind: 'Image', name: 'Логотип',
  fileName: 'logo.png', mimeType: 'image/png', fontFamilyName: null,
  createdAt: '', updatedAt: '', ...p,
});

describe('extensionOf', () => {
  it.each([
    ['logo.png', '.png'],
    ['ГОСТ 21.101. Форма 3.svg', '.svg'],   // точки в имени — обычное дело у форм ГОСТ
    ['noext', ''],
    ['.hidden', ''],
  ])('%s → «%s»', (file, ext) => expect(extensionOf(file)).toBe(ext));
});

describe('imageAssetPath', () => {
  /** Материализуется как /assets/{Имя}{расширение}: имя пользовательское, расширение из файла. */
  it('склеивает имя ассета с расширением файла', () =>
    expect(imageAssetPath({ name: 'Логотип', fileName: 'logo-v2.png' })).toBe('/assets/Логотип.png'));

  /**
   * Ведущий «/» — не косметика (issue #513): путь пишут в том числе в файл дерева библиотеки на
   * любой глубине, а Typst резолвит его относительно файла, где написан вызов. Ту же строку строит
   * сервер (`AssetPath.FromRoot`, тест `AssetPathTests`) — через границу процессов константу не
   * разделить, поэтому обе стороны прибиты тестом на точную форму.
   */
  it('путь начинается от корня пакета файлов', () =>
    expect(imageAssetPath({ name: 'Логотип', fileName: 'logo.png' })).toMatch(/^\/assets\//));

  it('имя с пробелами и точками не ломает путь', () =>
    expect(imageAssetPath({ name: 'ГОСТ 21.101. Форма 3', fileName: 'f3.svg' }))
      .toBe('/assets/ГОСТ 21.101. Форма 3.svg'));
});

describe('assetReference', () => {
  it('картинка адресуется путём', () =>
    expect(assetReference(asset({}))).toBe('/assets/Логотип.png'));

  /** Шрифт Typst находит по семейству; имя файла на диске меняется на font_N и не значит ничего. */
  it('шрифт адресуется именем семейства, а не именем ассета', () =>
    expect(assetReference(asset({ kind: 'Font', name: 'gost-type-a', fileName: 'GOST_A.ttf', fontFamilyName: 'GOST type A' })))
      .toBe('GOST type A'));

  /** Подставить `name` вместо нераспознанного семейства значило бы предложить строку,
   *  которая молча не сработает. */
  it('шрифт без распознанного семейства — ссылки нет', () =>
    expect(assetReference(asset({ kind: 'Font', name: 'gost-type-a', fontFamilyName: null }))).toBeNull());
});

// Общий набор с сервером (TemplateAssetNameRuleTests): правило на двух сторонах обязано совпадать.
describe('isValidImageAssetName', () => {
  it.each(nameCases.valid)('«%s» годится', (name) => expect(isValidImageAssetName(name)).toBe(true));
  it.each(nameCases.invalid)('«%s» не годится', (name) => expect(isValidImageAssetName(name)).toBe(false));
});

describe('suggestAssetName', () => {
  it.each([
    ['img.png', 'img'],
    ['ГОСТ 21.101. Форма 3.svg', 'ГОСТ 21.101. Форма 3'],   // пробелы и точки допустимы (#476)
    ['Печать (скан).png', 'Печать (скан)'],                 // скобки допустимы
    ['Сечение 2,5мм².png', 'Сечение 2,5мм²'],
    ['схема:1.png', 'схема_1'],
    ['a..b.svg', 'a.b'],
    [' logo .png', 'logo'],
    ['noext', 'noext'],
  ])('%s → «%s»', (file, name) => expect(suggestAssetName(file)).toBe(name));

  // Подсказка не предлагает имени, которое сервер отклонит: иначе отказ приходил бы сразу после выбора файла.
  it.each([...nameCases.valid, ...nameCases.invalid])('из «%s» выходит годное имя или пусто', (stem) => {
    const name = suggestAssetName(`${stem}.png`);
    expect(name === '' || isValidImageAssetName(name)).toBe(true);
  });
});
