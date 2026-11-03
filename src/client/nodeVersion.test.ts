import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Одна версия Node у разработчика, в CI и в образе сборки `web` ([UI-85], change infra-node26).
 * Источник — `.nvmrc` в корне: по нему ставит Node `setup-node` в CI. `FROM` образа и нижнюю границу
 * `engines` из файла не прочитать, поэтому они названы литералом, а совпадение сверяется здесь.
 * Разошлись — CI проверяет одно, в продакшн едет другое, и об этом не говорит никто.
 */

const ROOT = path.resolve(__dirname, '../..');
const read = (file: string) => readFileSync(path.join(ROOT, file), 'utf8');

interface Sources {
  nvmrc: string;
  dockerfile: string;
  ci: string;
  engines: Record<string, string | undefined>;
}

/** Расхождения словами: файл, что там найдено и что ожидалось. Пусто — версия одна. */
function nodeVersionMismatches(src: Sources): string[] {
  const version = src.nvmrc.trim();
  if (!/^\d+\.\d+\.\d+$/.test(version)) return [`.nvmrc: «${version}» — нужна полная версия X.Y.Z`];
  const out: string[] = [];
  const from = /^FROM node:(\S+)/m.exec(src.dockerfile)?.[1];
  if (from !== `${version}-alpine`) {
    out.push(`deploy/Dockerfile.web: FROM node:${from ?? '?'} — ожидалось node:${version}-alpine`);
  }
  for (const [file, range] of Object.entries(src.engines)) {
    if (range !== `>=${version}`) out.push(`${file}: engines.node «${range ?? 'нет'}» — ожидалось «>=${version}»`);
  }
  const steps = src.ci.split(/^\s*- uses: /m).filter(step => step.startsWith('actions/setup-node@'));
  if (steps.length === 0) out.push('.github/workflows/ci.yml: шагов actions/setup-node не найдено');
  steps.forEach((step, i) => {
    if (!/^\s+node-version-file: \.nvmrc\s*$/m.test(step) || /^\s+node-version:/m.test(step)) {
      out.push(`.github/workflows/ci.yml: setup-node №${i + 1} — версия не из .nvmrc (нужно node-version-file: .nvmrc)`);
    }
  });
  return out;
}

const engines = (file: string) => (JSON.parse(read(file)) as { engines?: { node?: string } }).engines?.node;

const actual = (): Sources => ({
  nvmrc: read('.nvmrc'),
  dockerfile: read('deploy/Dockerfile.web'),
  ci: read('.github/workflows/ci.yml'),
  engines: {
    'src/client/package.json': engines('src/client/package.json'),
    'docs/tools/package.json': engines('docs/tools/package.json'),
  },
});

describe('одна версия Node', () => {
  it('.nvmrc, образ web, engines и CI называют одну версию', () => {
    expect(nodeVersionMismatches(actual())).toEqual([]);
  });

  it('версию подняли в одном месте — красный с названием места и обеих версий', () => {
    const src = actual();
    const bumped = src.nvmrc.trim().replace(/\d+$/, n => String(Number(n) + 1));
    expect(nodeVersionMismatches({ ...src, nvmrc: bumped })).toEqual(expect.arrayContaining([
      expect.stringMatching(new RegExp(`^deploy/Dockerfile\\.web: .*ожидалось node:${bumped}-alpine$`)),
      expect.stringMatching(/^src\/client\/package\.json: engines\.node/),
      expect.stringMatching(/^docs\/tools\/package\.json: engines\.node/),
    ]));
  });

  it('setup-node с версией литералом — красный', () => {
    const src = actual();
    const ci = src.ci.replace('node-version-file: .nvmrc', "node-version: '26'");
    expect(nodeVersionMismatches({ ...src, ci })).toEqual([
      '.github/workflows/ci.yml: setup-node №1 — версия не из .nvmrc (нужно node-version-file: .nvmrc)',
    ]);
  });
});
