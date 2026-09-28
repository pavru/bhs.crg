import { describe, it, expect } from 'vitest';
import { offendingLines } from '@/shared/testing/clientSources';

/**
 * Сторож против возврата дефекта #1065: цвет в клиентском коде задаётся ТОКЕНАМИ темы, а не
 * литералами.
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
 * Определения самих токенов (`src/index.css`) под перепись не попадают: охват — только `.ts` и
 * `.tsx`, а литералы цвета уместны ровно там, это и есть их единственное место.
 *
 * ⚠️ Чего сторож НЕ ловит, чтобы обещание не было шире улова: готовые классы Tailwind с зашитым
 * цветом (`text-white`, `bg-black/5`). Они переживают смену темы штатно — `dark:`-вариантом
 * рядом, — и ловить их значило бы спорить с принятым в проекте способом писать полупрозрачные
 * подложки. Цвет, который тему НЕ переживает, — это литерал: hex, `rgb()/hsl()`, произвольное
 * значение Tailwind и именованный цвет в инлайн-стиле; они и ловятся.
 */

/** Литерал целиком: `'#fff'`, `fill="#161616"`. */
const HEX_LITERAL = /['"`]\s*#[0-9a-fA-F]{3,8}\s*['"`]/;
/** Hex внутри css-значения: `'1px solid #d1d5db'`. */
const HEX_IN_VALUE = /\b(?:solid|dashed|dotted|inset|px|em|rem)\s+#[0-9a-fA-F]{3,8}\b/;
/** Произвольное значение Tailwind: `border-[#d1d5db]`, `bg-[rgb(0,0,0)]`. */
const ARBITRARY = /-\[\s*(?:#[0-9a-fA-F]{3,8}|rgba?\(|hsla?\()/;
/** Функциональный цвет: `rgba(255,255,255,.06)`, `hsl(210 40% 50%)`. */
const FUNCTIONAL = /\b(?:rgba?|hsla?)\(\s*[\d.]/;
/** Именованный цвет в инлайн-стиле: `color: 'white'`. `transparent`/`currentColor` — не цвет темы. */
const NAMED_INLINE = new RegExp(
  '(?:background|backgroundColor|color|borderColor|outlineColor|fill|stroke)\\s*:\\s*'
  + "['\"`](?:white|black|red|green|blue|yellow|orange|purple|pink|brown|gray|grey|silver|gold"
  + "|navy|teal|lime|cyan|magenta|beige|ivory|coral|salmon|khaki|violet|indigo)['\"`]", 'i');

const RULES = [HEX_LITERAL, HEX_IN_VALUE, ARBITRARY, FUNCTIONAL, NAMED_INLINE];
const isColorLiteral = (line: string) => RULES.some(re => re.test(line));

/**
 * Где литерал допустим — каждый случай с причиной, а не списком «так исторически».
 *
 * `templateBlank` — заготовка Typst-ДОКУМЕНТА, а не разметки. Внесена авансом: цвета там сегодня
 * нет, но появится он как `rgb("#c4c6d0")`, и совет «возьмите `border-stroke`» внутри Typst
 * неприменим — сторож звал бы чинить тем, чего в этом языке нет.
 */
const ALLOWED = [
  /themeTokens\.test\.ts$/,
  /features\/templates\/templateBlank\.ts$/,
];

describe('цвета в клиентском коде', () => {
  it('задаются токенами темы, а не литералами', () => {
    const offenders = offendingLines(isColorLiteral, ALLOWED);
    expect(offenders, offenders.length
      ? 'Эти цвета не переживут смену темы (issue #1065) — возьмите токен '
        + '(`border-stroke`, `bg-muted`, `text-fg2`, `var(--color-brand)`):\n' + offenders.join('\n')
      : '').toEqual([]);
  });

  it('сам сторож видит нарушение, а не просто молчит', () => {
    // Проверяем не догадкой, а на выдуманных файлах. Первые два — ровно то, чем был дефект #1065.
    const fake = {
      '/src/features/Grid.tsx': "  const BORDER = '1px solid #d1d5db';",
      '/src/features/Pre.tsx': "  style={{ background: '#161616' }}",
      '/src/features/Svg.tsx': '  <rect fill="#fff" />',
      '/src/features/Arbitrary.tsx': '  <div className="border-[#d1d5db] bg-[#f3f4f6]" />',
      '/src/features/Rgba.tsx': "  const bg = 'rgba(255,255,255,.06)';",
      '/src/features/Named.tsx': "  style={{ color: 'white' }}",
    };
    expect(offendingLines(isColorLiteral, [], fake)).toHaveLength(6);
  });

  it('номер задачи и штатные классы нарушением не считаются', () => {
    // `#1065` — четыре шестнадцатеричных знака, `#154` — три; комментарии проекта полны таких
    // номеров, и перепись, которая шумит, будет отключена следующей же правкой. Первая редакция
    // ловила «MD3 outlined-поле (issue #574)» — из-за «outline» в соседнем слове — и восемнадцать
    // перечислений вида «(issue #305, #870)».
    const fake = {
      '/src/features/Ok.tsx': '  // Вынесено из ComplexFields (issue #1014), правка #1065, см. #858 и #154.',
      '/src/features/Ok2.tsx': "  const cls = 'border border-stroke bg-muted';",
      '/src/features/Ok3.tsx': '   * MD3 outlined-поле (issue #574) — рамка solid, как у соседей.',
      '/src/features/Ok4.tsx': '  <pre className="bg-black/5 dark:bg-white/5 text-fg2" />',
      '/src/features/Ok5.tsx': "  style={{ background: 'transparent', color: 'currentColor' }}",
    };
    expect(offendingLines(isColorLiteral, [], fake)).toEqual([]);
  });
});
