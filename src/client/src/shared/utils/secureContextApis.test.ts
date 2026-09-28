import { describe, it, expect } from 'vitest';
import { offendingLines } from '@/shared/testing/clientSources';

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
 * Исходники берём общим охватом (`clientSources`), а не своим `import.meta.glob`: копий этого
 * глоба стало четыре, и они разошлись — два сторожа видели только `.tsx`, и по их коду догадаться
 * об этом было нельзя (ревью PR #1067).
 *
 * `crypto.subtle` внесён авансом: сегодня он не используется, но ограничен тем же контекстом, и
 * первый же вызов повторил бы историю — падение только у тех, кто без HTTPS.
 */

/** Где обращение уместно: сама утилита-обёртка, её тест и этот сторож. */
const ALLOWED = [/(^|\/)localId\.ts$/, /(^|\/)localId\.test\.ts$/, /secureContextApis\.test\.ts$/];

const FORBIDDEN: { needle: RegExp; hint: string }[] = [
  { needle: /randomUUID/, hint: 'newLocalId() из @/shared/utils/localId' },
  { needle: /crypto\s*\??\.\s*subtle/, hint: 'crypto.subtle недоступен по HTTP — решайте задачу на сервере' },
];

const isForbidden = (line: string) => FORBIDDEN.some(({ needle }) => needle.test(line));

describe('API защищённого контекста', () => {
  it('вызываются только через обёртку, которая умеет работать по HTTP', () => {
    const offenders = offendingLines(isForbidden, ALLOWED);
    const hints = FORBIDDEN.map(f => f.hint).join('; ');

    expect(offenders, offenders.length
      ? ['Эти вызовы упадут на установке по HTTP (issue #848) — ' + hints, ...offenders].join('\n')
      : '').toEqual([]);
  });

  it('сам сторож видит нарушение, а не просто молчит', () => {
    // Проверяем не догадкой, а на выдуманном файле: тест, который «зелёный всегда», хуже
    // отсутствующего — он ещё и создаёт уверенность.
    const fake = { '/src/features/Fake.tsx': 'const id = crypto.randomUUID();' };
    expect(offendingLines(isForbidden, [], fake)).toHaveLength(1);
  });
});
