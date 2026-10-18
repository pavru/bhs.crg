import { describe, expect, it } from 'vitest';

/**
 * Обвязка приложения уходит со страницы только через охрану несохранённых правок (issue #1238).
 *
 * Охрана живёт в ссылках, а не в маршрутизаторе, и видит лишь то, что её позвало. Обвязка — всё, что
 * нарисовано вокруг ЛЮБОЙ страницы: рейл, палитра, колокольчик, баннеры — прямым переходом уводила
 * со счёта с правками молча; таких мест нашлось пять. Сторож читает исходники: поведение одного
 * места проверяет свой тест, а здесь стережётся появление шестого.
 */

const sources = import.meta.glob<string>(['/src/**/*.{ts,tsx}', '!/src/**/*.test.*'], { query: '?raw', import: 'default' });
const SHELL = '/src/shared/ui/AppShell.tsx';
/** Здесь охрана определена: `useNavigate` — её собственный инструмент. */
const GUARD = '/src/shared/ui/NavigationGuard.ts';

function resolve(from: string, spec: string): string | null {
  const base = spec.startsWith('@/')
    ? `/src/${spec.slice(2)}`
    : new URL(spec, `file://${from}`).pathname;
  return [`${base}.tsx`, `${base}.ts`, `${base}/index.tsx`, `${base}/index.ts`].find(f => sources[f]) ?? null;
}

/**
 * Оболочка и всё, что она подключает из интерфейса, — насквозь, а не на один шаг (ревью PR #1313):
 * строка уведомления, вынесенная в свой файл, остаётся обвязкой. Слой данных (`shared/api`) не в
 * счёт — он ничего не рисует и никуда не ведёт.
 */
async function chrome(): Promise<Record<string, string>> {
  const files: Record<string, string> = {};
  const queue = [SHELL];
  while (queue.length) {
    const file = queue.pop()!;
    if (file in files || file.startsWith('/src/shared/api/')) continue;
    const text = files[file] = await sources[file]();
    for (const [, spec] of text.matchAll(/from '((?:@\/|\.{1,2}\/)[^']+)'/g)) {
      const next = resolve(file, spec);
      if (next) queue.push(next);
    }
  }
  return files;
}

/** Способы уйти со страницы, которым в обвязке не место: каждый минует охрану. */
const BYPASSES: [RegExp, string][] = [
  [/\buseNavigate\b/, 'useNavigate — возьмите useGuardedNavigate'],
  [/<Navigate\b/, '<Navigate> — переход без охраны'],
  [/<a\s[^>]*href=["'{]\s*[`"']?\//, '<a href="/…"> — переход перезагрузкой, мимо охраны'],
  [/location\.(?:assign|replace)\(|location\.href\s*=/, 'переход через window.location'],
];

describe('обвязка приложения', () => {
  it('найдена насквозь: палитра, колокольчик и сами окна на месте', async () => {
    const files = Object.keys(await chrome());
    expect(files).toContain('/src/shared/ui/CommandPalette.tsx');
    expect(files).toContain('/src/features/notifications/NotificationList.tsx');
    // Не через оболочку напрямую — через тех, кого она подключает.
    expect(files).toContain('/src/shared/ui/Modal.tsx');
    expect(files).toContain('/src/shared/ui/modalDismissal.ts');
    expect(files.some(f => f.startsWith('/src/shared/api/'))).toBe(false);
  });

  it('не переходит мимо охраны', async () => {
    const found: string[] = [];
    for (const [file, text] of Object.entries(await chrome())) {
      if (file === GUARD) continue;
      for (const [pattern, why] of BYPASSES)
        if (pattern.test(text)) found.push(`${file}: ${why}`);
      for (const [tag] of text.matchAll(/<(?:Nav)?Link\b[^>]*>/g))
        if (!/onClick=\{guarded\w*\(/.test(tag))
          found.push(`${file}: ссылка без охраны — ${tag.replace(/\s+/g, ' ').slice(0, 70)}`);
    }
    expect(found).toEqual([]);
  });

  // Не «нет двух известных записей голого выхода», а «каждое упоминание — одно из разрешённых»
  // (ревью PR #1313): `onClick={() => logout()}` прежняя проверка пропускала.
  it('выходит из учётной записи только через охрану', async () => {
    const found: string[] = [];
    for (const [file, text] of Object.entries(await chrome())) {
      const rest = text
        .replace(/\/\*[\s\S]*?\*\/|\/\/.*$/gm, '')                      // комментарии
        .replace(/'[^'\n]*'/g, '')                                      // строки: адреса, идентификаторы
        .replace(/\w*[lL]eave\(logout\)/g, '')                          // выход через охрану
        .replace(/const \{[^}]*\} = useAuth\(\);/g, '')                 // получение из сессии
        .replace(/\}, \[[^\]]*\]\);/g, '');                             // список зависимостей хука
      if (/\blogout\b/.test(rest) && !/AuthProvider|useAuth\.ts/.test(file))
        found.push(`${file}: выход из учётной записи мимо охраны — оберните в useGuardedLeave`);
    }
    expect(found).toEqual([]);
  });
});

/**
 * Обвязка не открывает палитру и не уводит со страницы поверх окна, которое держит человека, — а
 * узнаёт о нём по атрибуту `data-modal-state`. Окно, забывшее себя назвать, из правила выпадает
 * молча: так выпали оба окна удаления с потерей ссылок (ревью PR #1313).
 */
describe('каждое окно называет своё состояние', () => {
  it('у каждого `Dialog.Content` стоит `data-modal-state`', async () => {
    const found: string[] = [];
    let windows = 0;
    for (const [file, load] of Object.entries(sources)) {
      if (!file.endsWith('.tsx')) continue;
      const text = await load();
      for (const [tag] of text.matchAll(/<Dialog\.Content\b[\s\S]*?>\s*$/gm)) {
        windows++;
        if (!/data-modal-state=/.test(tag)) found.push(file);
      }
    }
    expect(found).toEqual([]);
    expect(windows).toBeGreaterThanOrEqual(7);
    // Читает исходники всего клиента: под полной нагрузкой прогона 5 с по умолчанию не хватало
    // (плавающий красный, PR #1383–#1388), как и у сторожа `listDetailView`.
  }, 20_000);
});
