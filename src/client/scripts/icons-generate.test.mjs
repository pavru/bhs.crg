import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { exportName, generate, iconsDir, packageVersion, readManifest, readPath, reservedNames } from './icons-generate.mjs';

// Сторож набора значков ([UI-60]): почему набор генерируется — в icons-generate.mjs.
const read = file => readFileSync(file, 'utf8').replaceAll('\r\n', '\n');

describe('набор значков: файл равен генерации', () => {
  it('icons.generated.ts', () => {
    // Сообщение называет команду: иначе значок допишут в сгенерированный файл руками, мимо манифеста.
    expect(read(join(iconsDir, 'icons.generated.ts')) === generate(readManifest(), readPath, packageVersion(), reservedNames()),
      'src/shared/ui/icons/icons.generated.ts не равен генерации из manifest.json. Файл руками не правят:\n'
      + 'правка — в manifest.json, затем npm run icons:generate, и оба файла — в коммит.').toBe(true);
  });
});

describe('набор значков: генератор', () => {
  const fake = paths => name => paths[name] ?? null;

  it('экспорт — PascalCase от имени, заливка — вторым путём', () => {
    const text = generate({ icons: { check_circle: {}, receipt_long: { fill: true } } },
      fake({ check_circle: 'M1', receipt_long: 'M2', 'receipt_long-fill': 'M3' }), '0.0.0');
    expect(text).toContain("export const CheckCircle: IconDef = { d: 'M1' };");
    expect(text).toContain("export const ReceiptLong: FilledIconDef = { d: 'M2', fill: 'M3' };");
  });

  it('имя, которого нет в пакете, — отказ с перечнем', () => {
    expect(() => generate({ icons: { push_pin: {}, keep: { fill: true } } }, fake({ keep: 'M1' }), '0.0.0'))
      .toThrow(/нет keep-fill, push_pin/);
  });

  it('два значка с одним экспортом — отказ, выход — поле export', () => {
    expect(() => generate({ icons: { 'a_b': {}, 'a-b': {} } }, fake({ a_b: 'M1', 'a-b': 'M2' }), '0.0.0'))
      .toThrow(/экспорт AB у двух значков/);
    expect(generate({ icons: { history: { export: 'HistoryIcon' } } }, fake({ history: 'M1' }), '0.0.0'))
      .toContain('export const HistoryIcon: IconDef');
  });

  it('порядок — по кодам символов, не по языку машины', () => {
    const text = generate({ icons: { ab: {}, a_b: {}, a2: {} } }, fake({ ab: 'M1', a_b: 'M2', a2: 'M3' }), '0.0.0');
    expect(text.match(/export const (\w+)/g)).toEqual(['export const A2', 'export const AB', 'export const Ab']);
  });

  it('экспорт, затеняющий глобальное имя, — отказ, выход — поле export', () => {
    const reserved = reservedNames();
    // Язык (есть в Node) и DOM (только в lib.dom.d.ts): оба затеняли бы глобальное в файле экрана.
    for (const name of ['Error', 'Function', 'Image', 'History', 'Lock']) expect(reserved.has(name), name).toBe(true);
    expect(() => generate({ icons: { error: {} } }, fake({ error: 'M1' }), '0.0.0', reserved))
      .toThrow(/экспорт Error у значка error затеняет глобальное имя/);
    expect(generate({ icons: { error: { export: 'ErrorIcon' } } }, fake({ error: 'M1' }), '0.0.0', reserved))
      .toContain('export const ErrorIcon: IconDef');
  });

  it('имя с цифры — всё равно идентификатор', () => {
    expect(exportName('123')).toBe('N123');
    expect(exportName('10k')).toBe('N10k');
  });
});
