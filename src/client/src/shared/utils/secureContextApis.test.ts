import { describe, it, expect } from 'vitest';

/**
 * Сторож против возврата дефекта #848: прямых обращений к API, которых нет вне ЗАЩИЩЁННОГО
 * контекста, в коде быть не должно.
 *
 * Почему тестом, а не только правилом линтера. Правило в eslint.config.js добавлено и полезно
 * подсказкой в редакторе, но воротами оно не работает: линт не запускается ни в одном workflow, а
 * локально уже отвечает сотней с лишним ошибок, накопленных раньше, — сто тринадцатую в этой стене
 * никто не заметит. Тест виден в `npm test` сразу и падает один.
 *
 * Ищем ТЕКСТОМ, а не разбором синтаксиса, намеренно: так ловятся и `crypto['randomUUID']()`, и
 * `const { randomUUID } = crypto` — оба мимо селектора линтера (проверено).
 *
 * Исходники берём через import.meta.glob, а не через node:fs: клиентский проект собирается без
 * типов Node, и `tsc -b` — то есть сборка — на таком импорте встаёт.
 *
 * `crypto.subtle` внесён авансом: сегодня он не используется, но ограничен тем же контекстом, и
 * первый же вызов повторил бы историю — падение только у тех, кто без HTTPS.
 */

const sources = import.meta.glob('../../**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

/** Где обращение уместно: сама утилита-обёртка, её тест и этот сторож. */
const ALLOWED = [/(^|\/)localId\.ts$/, /(^|\/)localId\.test\.ts$/, /secureContextApis\.test\.ts$/];

const FORBIDDEN: { needle: RegExp; hint: string }[] = [
  { needle: /randomUUID/, hint: 'newLocalId() из @/shared/utils/localId' },
  { needle: /crypto\s*\??\.\s*subtle/, hint: 'crypto.subtle недоступен по HTTP — решайте задачу на сервере' },
];

describe('API защищённого контекста', () => {
  it('вызываются только через обёртку, которая умеет работать по HTTP', () => {
    const offenders: string[] = [];

    for (const [file, code] of Object.entries(sources)) {
      if (ALLOWED.some(re => re.test(file))) continue;

      code.split('\n').forEach((line, i) => {
        for (const { needle, hint } of FORBIDDEN) {
          if (needle.test(line)) offenders.push(`${file}:${i + 1} — ${line.trim().slice(0, 70)} → ${hint}`);
        }
      });
    }

    expect(offenders, offenders.length
      ? `Эти вызовы упадут на установке по HTTP (issue #848):\n${offenders.join('\n')}`
      : '').toEqual([]);
  });

  it('сам сторож видит нарушение, а не просто молчит', () => {
    // Проверяем не догадкой, а на выдуманном файле: тест, который «зелёный всегда», хуже
    // отсутствующего — он ещё и создаёт уверенность.
    const fake = { 'src/features/Fake.tsx': 'const id = crypto.randomUUID();' };
    const found = Object.entries(fake).flatMap(([f, code]) =>
      FORBIDDEN.filter(({ needle }) => needle.test(code)).map(({ hint }) => `${f} → ${hint}`));

    expect(found).toHaveLength(1);
  });
});
