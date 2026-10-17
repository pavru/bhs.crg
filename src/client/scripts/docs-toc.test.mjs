import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  DOCS_DIR, FIX_COMMAND, TOC_DOCS, headings, plain, slugger, tocBlock, tocProblems, withToc,
} from '../../../docs/tools/toc.mjs';

/**
 * Содержание инструкций сходится с их заголовками (issue #1374).
 *
 * Сторож стоит в проверке фронтенда — она обязательная, — а не рядом с тестами сервера: построитель
 * содержания написан на JS (docs/tools/toc.mjs), и сторож зовёт ЕГО ЖЕ. Своя копия правила якорей на
 * C# могла бы разойтись с ним молча — тогда сторож был бы зелёным, а ссылки вели бы в никуда.
 */
describe('содержание инструкций', () => {
  for (const file of TOC_DOCS) {
    it(`${file}: содержание совпадает с заголовками`, () => {
      const problems = tocProblems(readFileSync(resolve(DOCS_DIR, file), 'utf8'), file);
      expect(problems, `${problems.join('\n')}\nПерестроить: ${FIX_COMMAND}`).toEqual([]);
    });
  }
});

describe('содержание: построитель', () => {
  const doc = [
    '# Инструкция', '', 'Вводный абзац.', '', '<!-- toc -->', '<!-- /toc -->', '',
    '## 1. Вход', '### 1.1. Пароль', '## 2. Конфигурация (`.env`)',
    '```bash', '# 1. Взять со страницы выпуска', '```',
    '## 3. А — Б', '## 3. А — Б', '',
  ].join('\n');

  it('якоря — по правилам GitHub: знаки выброшены, тире даёт «--», повтор — «-1»', () => {
    expect(headings(doc).filter(h => h.level > 1).map(h => h.anchor)).toEqual([
      '1-вход', '11-пароль', '2-конфигурация-env', '3-а--б', '3-а--б-1',
    ]);
  });

  it('решётка в блоке кода — не заголовок', () => {
    expect(headings(doc).map(h => h.text)).not.toContain('1. Взять со страницы выпуска');
  });

  it('повторный запуск даёт тот же документ, остальной текст не тронут', () => {
    const once = withToc(doc);
    expect(withToc(once)).toBe(once);
    expect(once.replace(/<!-- toc -->[\s\S]*<!-- \/toc -->/, '')).toBe(doc.replace(/<!-- toc -->[\s\S]*<!-- \/toc -->/, ''));
    expect(once).toContain('  - [1.1. Пароль](#11-пароль)');
  });

  it('новая глава без перестройки — отказ с названием расхождения', () => {
    const stale = withToc(doc) + '\n## 4. Новая глава\n';
    expect(tocProblems(stale, 'X.md')).toEqual(['X.md: в содержании нет строки - [4. Новая глава](#4-новая-глава)']);
  });

  it('переименование без перестройки — и нет строки, и лишняя', () => {
    const renamed = withToc(doc).replace('## 1. Вход\n', '## 1. Вход в систему\n');
    expect(tocProblems(renamed, 'X.md')).toEqual([
      'X.md: в содержании нет строки - [1. Вход в систему](#1-вход-в-систему)',
      'X.md: в содержании лишняя строка - [1. Вход](#1-вход)',
    ]);
  });

  it('счётчик повторов один на документ — как у GitHub', () => {
    const slug = slugger();
    expect(['a', 'a', 'a-1', 'a'].map(slug)).toEqual(['a', 'a-1', 'a-1-1', 'a-2']);
  });
});

/** Находки ревью PR #1447: разбор, который молча разошёлся бы с GitHub или переписал бы прозу. */
describe('содержание: края разбора', () => {
  const frame = (...body) => ['# Док', '', '<!-- toc -->', '<!-- /toc -->', '', ...body, ''].join('\n');

  it('код в заголовке — как написан, «<путь>» не тег; выделение и сущности — как у GitHub', () => {
    expect(plain('Каталог `<путь>`')).toBe('Каталог <путь>');
    expect(plain('Ключ _важный_ и BACKUP_DIR')).toBe('Ключ важный и BACKUP_DIR');
    expect(plain('A &amp; B')).toBe('A & B');
    expect(headings(frame('## Каталог `<путь>`', '## A &amp; B')).map(h => h.anchor))
      .toEqual(['док', 'каталог-путь', 'a--b']);
  });

  it('метка, упомянутая в тексте, — не блок: перестройка прозу не трогает', () => {
    const doc = ['# Док', '', 'Содержание стоит между метками `<!-- toc -->` и `<!-- /toc -->`.', '',
      '<!-- toc -->', '<!-- /toc -->', '', '## 1. Раздел', ''].join('\n');
    const once = withToc(doc);
    expect(once).toContain('Содержание стоит между метками `<!-- toc -->` и `<!-- /toc -->`.');
    expect(once).toContain('- [1. Раздел](#1-раздел)');
    expect(tocProblems(once, 'X.md')).toEqual([]);
  });

  it('заголовок в цитате, в пункте списка и подчёркнутый — отказ с номером строки', () => {
    const doc = withToc(frame('> ### Пример', '- ## Пункт', 'Абзац', '---', '## 2. Конец'));
    expect(tocProblems(doc, 'X.md')).toEqual([
      expect.stringContaining('заголовок внутри цитаты или пункта списка — «> ### Пример»'),
      expect.stringContaining('заголовок внутри цитаты или пункта списка — «- ## Пункт»'),
      expect.stringContaining('подчёркнутый «---»'),
    ]);
  });

  it('черта после пустой строки, пункта списка или таблицы — черта, а не заголовок', () => {
    const doc = withToc(frame('Абзац', '', '---', '- пункт', '  продолжение', '---', '| a |', '|---|', '## 2. Конец'));
    expect(tocProblems(doc, 'X.md')).toEqual([]);
  });

  it('ссылка из текста на якорь, которого нет, — отказ; на существующий — нет', () => {
    const doc = withToc(frame('## 4. Конфигурация (`.env`)', 'См. [§4](#4-конфигурация-env) и [§5](#5-нет-такого).'));
    expect(tocProblems(doc, 'X.md')).toEqual([
      'X.md, строка 11: ссылка «#5-нет-такого» ведёт в никуда — заголовка с таким якорем нет',
    ]);
  });

  it('ссылка в заголовке — в содержании своим текстом, без ссылки в ссылке', () => {
    expect(tocBlock(frame('### См. [§4](#x) и [скобка]'))).toContain('  - [См. §4 и \\[скобка\\]](#см-4-и-скобка)');
  });
});
