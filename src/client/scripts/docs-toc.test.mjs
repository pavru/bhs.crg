import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  DOCS_DIR, FIX_COMMAND, TOC_DOCS, headings, slugger, tocProblems, withToc,
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
