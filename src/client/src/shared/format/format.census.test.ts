import { describe, expect, it } from 'vitest';
import { offendingLines } from '@/shared/testing/clientSources';

/**
 * Перепись: число, сумму и дату в текст превращает только `shared/format` (задача N2 этапа 2,
 * issue #1103).
 *
 * <p>Форматирование по месту не ломает ничего, что видно на этом экране, — оно ломает обещание
 * настройки «Внешний вид»: человек меняет язык, и один экран его не слушается. Заметить это можно
 * только сравнив два экрана, поэтому сторожит перепись, а не ревью.</p>
 */

/**
 * Прямой вызов форматирования: методы `toLocale*`, конструкторы `Intl` и `toFixed`. Последний языка
 * не знает вовсе — и именно поэтому сюда попал: «1433.6 МБ» с точкой жило в `formatBytes` рядом с
 * экранами, которые уже слушали настройку (ревью PR #1207).
 */
const DIRECT = /\.toLocale(String|DateString|TimeString)\s*\(|\bIntl\.(NumberFormat|DateTimeFormat)\b|\.toFixed\s*\(/;

/** Комментарии вправе называть запрещённое по имени — иначе правило нельзя было бы объяснить. */
const isCode = (line: string) => !/^\s*(\/\/|\*|\/\*)/.test(line);

const isOffending = (line: string) => isCode(line) && DIRECT.test(line);

/** Где прямой вызов уместен: сам форматтер и его тесты. */
const ALLOWED = [/^\/src\/shared\/format\//];

describe('форматирование — только через shared/format', () => {
  it('ни одного прямого toLocale*, Intl.*Format и toFixed вне форматтера', () => {
    const found = offendingLines(isOffending, ALLOWED);

    expect(found, 'Число, сумму и дату форматирует `@/shared/format/format` — он знает язык из настройки '
      + '«Внешний вид». Формат по месту эту настройку не слышит:\n' + found.join('\n')).toEqual([]);
  });

  it('перепись видит нарушение, когда оно есть', () => {
    const sources = {
      '/src/features/x/Sum.tsx': "const a = total.toLocaleString('ru-RU');",
      '/src/features/x/Day.ts': 'const b = new Date(iso).toLocaleDateString();',
      '/src/features/x/Raw.ts': "const c = new Intl.NumberFormat('ru-RU').format(1);",
      '/src/features/x/Size.ts': 'const e = `${(bytes / 1024).toFixed(1)} КБ`;',
      '/src/features/x/Note.ts': '// раньше здесь стоял toLocaleString( — теперь форматтер',
      '/src/features/x/Fine.ts': 'const d = formatMoney(total);',
      '/src/shared/format/format.ts': 'new Intl.DateTimeFormat(locale, options)',
    };

    expect(offendingLines(isOffending, ALLOWED, sources).map(line => line.split(':')[0])).toEqual([
      '/src/features/x/Sum.tsx', '/src/features/x/Day.ts', '/src/features/x/Raw.ts', '/src/features/x/Size.ts',
    ]);
  });
});
