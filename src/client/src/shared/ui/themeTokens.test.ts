import { describe, it, expect } from 'vitest';

/**
 * Сторож против возврата дефекта #1065: цвет в клиентском коде задаётся ТОКЕНАМИ темы, а не
 * шестнадцатеричными литералами.
 *
 * Литерал — это всегда одна тема. Единственная редактируемая сетка (`ArrayTableModal`) держала
 * границы и фон шапки литералами светлой темы, и на тёмной у неё оставались светло-серые линии и
 * почти белая шапка — при том что весь остальной интерфейс тему держит. Видно это только глазами
 * и только в тёмной теме, поэтому проверка нужна автоматическая.
 *
 * Переписью, а не правилом линтера, по той же причине, что и у сторожа защищённого контекста
 * (`secureContextApis.test.ts`): линт отвечает стеной накопленных ошибок, и новая в ней теряется,
 * а тест падает один и виден сразу.
 *
 * Исходники берём через `import.meta.glob`, а не через `node:fs`: клиентский проект собирается без
 * типов Node, и `tsc -b` — то есть сборка — на таком импорте встаёт.
 *
 * Определения самих токенов (`src/index.css`) под перепись не попадают: glob берёт только `.ts` и
 * `.tsx`, а литералы цвета уместны ровно там — это и есть их единственное место.
 */

const sources = import.meta.glob('../../**/*.{ts,tsx}', { query: '?raw', import: 'default', eager: true }) as Record<string, string>;

/** Где литерал допустим: сам этот сторож — в нём цвета служат примерами. */
const ALLOWED = [/themeTokens\.test\.ts$/];

/**
 * Ловим не «решётку с шестнадцатеричными знаками», а ЗНАЧЕНИЕ ЦВЕТА: либо литерал целиком
 * (`'#fff'`, `fill="#161616"`), либо hex внутри css-значения (`'1px solid #d1d5db'`).
 *
 * Шире нельзя: комментарии и подписи этого проекта полны номеров задач, а `#1065` — это четыре
 * шестнадцатеричных знака подряд, `#154` — три. Первая редакция переписи была шире и поймала
 * «MD3 outlined-поле (issue #574)» — из-за «outline» в соседнем слове. Перепись, которая шумит,
 * будет отключена следующей же правкой, поэтому она узкая намеренно. Запятая в списке слева от
 * hex тоже пробовалась — и поймала восемнадцать перечислений задач вида «(issue #305, #870)».
 */
const ONLY_HEX = /['"`]\s*#[0-9a-fA-F]{3,8}\s*['"`]/;
const IN_CSS_VALUE = /\b(?:solid|dashed|dotted|inset|px|em|rem)\s+#[0-9a-fA-F]{3,8}\b/;

function offendersIn(files: Record<string, string>): string[] {
  const found: string[] = [];
  for (const [file, code] of Object.entries(files)) {
    if (ALLOWED.some(re => re.test(file))) continue;
    code.split('\n').forEach((line, i) => {
      if (ONLY_HEX.test(line) || IN_CSS_VALUE.test(line)) found.push(`${file}:${i + 1} — ${line.trim().slice(0, 80)}`);
    });
  }
  return found;
}

describe('цвета в клиентском коде', () => {
  it('задаются токенами темы, а не шестнадцатеричными литералами', () => {
    const offenders = offendersIn(sources);
    expect(offenders, offenders.length
      ? 'Эти цвета не переживут смену темы (issue #1065) — возьмите токен '
        + '(`border-stroke`, `bg-muted`, `text-fg2`, `var(--color-brand)`):\n' + offenders.join('\n')
      : '').toEqual([]);
  });

  it('сам сторож видит нарушение, а не просто молчит', () => {
    // Проверяем не догадкой, а на выдуманных файлах: тест, который «зелёный всегда», хуже
    // отсутствующего — он ещё и создаёт уверенность. Первые два — ровно то, чем был дефект.
    const fake = {
      'src/features/Grid.tsx': "  const BORDER = '1px solid #d1d5db';",
      'src/features/Pre.tsx': "  style={{ background: '#161616' }}",
      'src/features/Short.tsx': '  <rect fill="#fff" />',
    };
    expect(offendersIn(fake)).toHaveLength(3);
  });

  it('номер задачи нарушением не считается', () => {
    const fake = {
      'src/features/Ok.tsx': '  // Вынесено из ComplexFields (issue #1014), правка #1065, см. #858 и #154.',
      'src/features/Ok2.tsx': "  const cls = 'border border-stroke bg-muted';",
      'src/features/Ok3.tsx': '   * MD3 outlined-поле (issue #574) — рамка solid, как у соседей.',
    };
    expect(offendersIn(fake)).toEqual([]);
  });
});
