import { describe, expect, it } from 'vitest';

/**
 * Обвязка приложения уходит со страницы только через охрану несохранённых правок (issue #1238).
 *
 * Охрана живёт в ссылках, а не в маршрутизаторе, и видит лишь то, что её позвало. Обвязка — всё, что
 * нарисовано вокруг ЛЮБОЙ страницы: рейл, палитра, колокольчик, баннеры — прямым переходом уводила
 * со счёта с правками молча; таких мест нашлось пять. Сторож читает исходники: поведение одного
 * места проверяет свой тест, а здесь стережётся появление шестого.
 */

const sources = import.meta.glob<string>('/src/**/*.tsx', { query: '?raw', import: 'default' });
const SHELL = '/src/shared/ui/AppShell.tsx';

/** Оболочка и всё, что она подключает из интерфейса напрямую, — это и есть обвязка. */
async function chrome(): Promise<Record<string, string>> {
  const shell = await sources[SHELL]();
  const files: Record<string, string> = { [SHELL]: shell };
  for (const [, path] of shell.matchAll(/from '@\/((?:shared\/ui|features)\/[^']+)'/g)) {
    const file = `/src/${path}.tsx`;
    if (sources[file]) files[file] = await sources[file]();
  }
  return files;
}

describe('обвязка приложения', () => {
  it('состоит из оболочки и её частей — палитра и колокольчик на месте', async () => {
    const files = Object.keys(await chrome());
    expect(files).toContain('/src/shared/ui/CommandPalette.tsx');
    expect(files).toContain('/src/features/notifications/NotificationsCenter.tsx');
    expect(files.length).toBeGreaterThan(5);
  });

  it('не переходит мимо охраны: ни `useNavigate`, ни ссылки без охраны, ни голого выхода', async () => {
    const found: string[] = [];
    for (const [file, text] of Object.entries(await chrome())) {
      if (/\buseNavigate\b/.test(text))
        found.push(`${file}: useNavigate — возьмите useGuardedNavigate`);
      for (const [tag] of text.matchAll(/<(?:Nav)?Link\b[^>]*>/g))
        if (!/onClick=\{guarded\w*\(/.test(tag))
          found.push(`${file}: ссылка без охраны — ${tag.replace(/\s+/g, ' ').slice(0, 70)}`);
      if (/onClick=\{logout\}|run:\s*logout\b/.test(text))
        found.push(`${file}: выход из учётной записи мимо охраны — оберните в useGuardedLeave`);
    }
    expect(found).toEqual([]);
  });
});
