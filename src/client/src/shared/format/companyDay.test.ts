import { afterEach, describe, expect, it } from 'vitest';
import { companyToday, setCompanyTimeZone } from './companyDay';

afterEach(() => setCompanyTimeZone(null));

describe('companyToday', () => {
  const instant = new Date('2026-10-31T20:00:00Z');

  it.each([
    ['Asia/Vladivostok', '2026-11-01'],
    ['Europe/Moscow', '2026-10-31'],
    ['America/New_York', '2026-10-31'],
    ['Pacific/Kiritimati', '2026-11-01'],
  ])('%s → %s', (zone, day) => {
    setCompanyTimeZone(zone);
    expect(companyToday(instant)).toBe(day);
  });

  it('пояс не известен или не по форме — день браузера, а не отказ', () => {
    const local = new Date(2026, 9, 31, 12);
    expect(companyToday(local)).toBe('2026-10-31');
    setCompanyTimeZone('Mars/Olympus');
    expect(companyToday(local)).toBe('2026-10-31');
  });
});
