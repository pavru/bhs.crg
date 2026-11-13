import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { SUITE_SECONDS, parseShardArg, splitIntoShards } from './shards.mjs';

/**
 * Доли живых прогонов CI (`shards.mjs`): ни один набор не потерян и не идёт дважды.
 *
 * Зачем сторож. Доля — отдельная работа CI, и каждая видит только свою часть. Набор, не попавший
 * ни в одну, не гоняется нигде, а все три работы зелёные; набор в двух долях — лишние минуты и
 * повод думать, что раскладка «как-то работает». Узнать об этом из CI нельзя ничем, кроме чтения
 * трёх логов подряд.
 */
const here = path.dirname(fileURLToPath(import.meta.url));
const pkg = JSON.parse(readFileSync(path.join(here, '..', 'package.json'), 'utf8'));
const inCi = (pkg.scripts['test:e2e:ci'] ?? '').split(/\s+/).slice(2);
const ci = readFileSync(path.join(here, '..', '..', '..', '.github', 'workflows', 'ci.yml'), 'utf8');

describe('доли живых прогонов CI', () => {
  // Число долей — из ci.yml: и список матрицы, и знаменатель в `--shard` обязаны его называть.
  const matrix = /^\s*shard: \[([\d, ]+)\]\s*$/m.exec(ci);
  const denominators = [...ci.matchAll(/--shard \$\{\{ matrix\.shard \}\}\/(\d+)/g)].map(m => Number(m[1]));

  it('в ci.yml матрица долей 1…N и запуск с тем же N', () => {
    expect(matrix, 'в ci.yml не нашлась матрица `shard: [1, 2, …]` — считать не из чего').not.toBeNull();
    expect(denominators.length, 'в ci.yml не нашёлся запуск `--shard ${{ matrix.shard }}/N`').toBe(1);
    const listed = matrix[1].split(',').map(s => Number(s.trim()));
    const count = denominators[0];
    expect(listed, `матрица ${listed.join(', ')} не совпадает с --shard …/${count}: доля без работы `
      + 'не гоняется нигде, а работа без доли отказывает').toEqual(Array.from({ length: count }, (_, i) => i + 1));
  });

  it('у каждого набора CI есть вес, и нет весов без набора', () => {
    expect(inCi.filter(name => !(name in SUITE_SECONDS)),
      'впишите вес нового набора в SUITE_SECONDS (e2e/shards.mjs): секунды из лога CI, на глаз — тоже годится')
      .toEqual([]);
    expect(Object.keys(SUITE_SECONDS).filter(name => !inCi.includes(name)),
      'вес есть, а набора в test:e2e:ci нет — уберите вес').toEqual([]);
  });

  it('каждый набор CI — ровно в одной доле, и пустых долей нет', () => {
    const count = denominators[0];
    const shards = splitIntoShards(inCi, SUITE_SECONDS, count);
    expect(shards).toHaveLength(count);
    for (const [i, list] of shards.entries()) expect(list.length, `доля ${i + 1} пуста`).toBeGreaterThan(0);
    expect(shards.flat().sort()).toEqual([...inCi].sort());
  });
});

describe('splitIntoShards', () => {
  it('самый тяжёлый — в самую лёгкую долю, порядок внутри доли — как в списке', () => {
    const seconds = { a: 10, b: 50, c: 30, d: 20, e: 25 };
    // b 50 → доля 1 (50); c 30 → 2 (30); e 25 → 2 (55); d 20 → 1 (70); a 10 → 2 (65)
    expect(splitIntoShards(['a', 'b', 'c', 'd', 'e'], seconds, 2)).toEqual([['b', 'd'], ['a', 'c', 'e']]);
  });

  it('равные веса — в порядке списка, равные доли — младшая первой', () => {
    expect(splitIntoShards(['x', 'y', 'z'], { x: 5, y: 5, z: 5 }, 3)).toEqual([['x'], ['y'], ['z']]);
  });

  it('одна доля — весь список как есть', () => {
    expect(splitIntoShards(inCi, SUITE_SECONDS, 1)).toEqual([inCi]);
  });

  it('набор без веса — отказ с его именем', () => {
    expect(() => splitIntoShards(['a', 'new-one'], { a: 1 }, 2)).toThrow(/new-one/);
  });
});

describe('parseShardArg', () => {
  it('без ключа — весь список', () => {
    expect(parseShardArg(['a', 'b'])).toEqual({ shard: null, rest: ['a', 'b'] });
  });

  it('обе записи ключа', () => {
    expect(parseShardArg(['--shard', '2/3', 'a'])).toEqual({ shard: { index: 2, count: 3 }, rest: ['a'] });
    expect(parseShardArg(['a', '--shard=1/2'])).toEqual({ shard: { index: 1, count: 2 }, rest: ['a'] });
  });

  it.each(['', '0/3', '4/3', '2', 'a/b', '1/0'])('битое значение «%s» — отказ, а не весь список', value => {
    expect(() => parseShardArg(['--shard', value, 'a'])).toThrow(/--shard/);
  });

  it('ключ без значения — отказ', () => {
    expect(() => parseShardArg(['a', '--shard'])).toThrow(/--shard/);
  });
});
