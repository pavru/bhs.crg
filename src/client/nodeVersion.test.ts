import { readdirSync, readFileSync } from 'node:fs';
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
  /** Workflow `.github/workflows/*.yml`: имя файла → текст. */
  workflows: Record<string, string>;
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
  let found = 0;
  for (const [file, text] of Object.entries(src.workflows)) {
    setupNodeSteps(text).forEach((step, i) => {
      found++;
      if (!/^\s+node-version-file: \.nvmrc\s*$/m.test(step) || /^\s+node-version:/m.test(step)) {
        out.push(`.github/workflows/${file}: setup-node №${i + 1} — версия не из .nvmrc (нужно node-version-file: .nvmrc)`);
      }
    });
  }
  if (found === 0) out.push('.github/workflows: шагов actions/setup-node не найдено');
  return out;
}

/**
 * Шаги с `actions/setup-node` целиком — от `- ` шага до следующего шага того же уровня, в какой бы
 * форме шаг ни был записан: `- uses:` первым или `- name:` первым, а `uses:` ниже.
 */
function setupNodeSteps(workflow: string): string[] {
  const lines = workflow.split(/\r?\n/);
  const indent = (line: string) => line.length - line.trimStart().length;
  const steps: string[] = [];
  lines.forEach((line, at) => {
    if (!/^\s*(- )?uses:\s*actions\/setup-node@/.test(line)) return;
    let start = at;
    while (start > 0 && !/^\s*- /.test(lines[start])) start--;
    const level = indent(lines[start]);
    let end = start + 1;
    while (end < lines.length && (lines[end].trim() === '' || indent(lines[end]) > level)) end++;
    steps.push(lines.slice(start, end).join('\n'));
  });
  return steps;
}

const engines = (file: string) => (JSON.parse(read(file)) as { engines?: { node?: string } }).engines?.node;

const actual = (): Sources => ({
  nvmrc: read('.nvmrc'),
  dockerfile: read('deploy/Dockerfile.web'),
  workflows: Object.fromEntries(readdirSync(path.join(ROOT, '.github/workflows'))
    .filter(file => /\.ya?ml$/.test(file))
    .map(file => [file, read(`.github/workflows/${file}`)])),
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
    const ci = src.workflows['ci.yml'].replace('node-version-file: .nvmrc', "node-version: '26'");
    expect(nodeVersionMismatches({ ...src, workflows: { ...src.workflows, 'ci.yml': ci } })).toEqual([
      '.github/workflows/ci.yml: setup-node №1 — версия не из .nvmrc (нужно node-version-file: .nvmrc)',
    ]);
  });

  it('шаг «name первым» и setup-node в другом workflow — тоже под сверкой', () => {
    const src = actual();
    const release = [
      'jobs:', '  build:', '    steps:',
      '      - uses: actions/checkout@abc', '        with:', '          fetch-depth: 0',
      '      - name: Node', '        uses: actions/setup-node@def', '        with:', "          node-version: '22'",
      '      - run: npm ci',
    ].join('\n');
    expect(nodeVersionMismatches({ ...src, workflows: { ...src.workflows, 'release.yml': release } })).toEqual([
      '.github/workflows/release.yml: setup-node №1 — версия не из .nvmrc (нужно node-version-file: .nvmrc)',
    ]);
  });
});
