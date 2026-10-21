import { describe, expect, it } from 'vitest';
import type { CompanyTimeZoneView } from '@/shared/api/companyTimeZone';
import { SERVER_ZONE, backupZoneHint, companyNow, nextBackupText, zoneChoices, zoneNotice } from './companySettings';

/**
 * Часовой пояс компании на экране (issue #1148): что предложить и что сказать, когда действует не
 * то, что администратор, возможно, думает. Отказа у невыбранного пояса нет — сутки просто
 * наступают не в полночь, — поэтому сказать обязан экран.
 */

const view = (over: Partial<CompanyTimeZoneView> = {}): CompanyTimeZoneView => ({
  timeZoneId: 'Europe/Moscow',
  effectiveTimeZoneId: 'Europe/Moscow',
  serverTimeZoneId: 'Etc/UTC',
  resolved: true,
  effectiveOffset: 'UTC+03:00',
  serverOffset: 'UTC+00:00',
  effectiveNow: '2026-10-13T14:32',
  timeZones: [
    { id: 'Europe/Moscow', city: 'Москва', offset: 'UTC+03:00' },
    { id: 'Asia/Vladivostok', city: 'Владивосток', offset: 'UTC+10:00' },
  ],
  ...over,
});

describe('что сказать о поясе', () => {
  it('выбран и действует — говорить нечего', () => {
    expect(zoneNotice(view())).toBeNull();
  });

  it('не выбран — назван пояс сервера и последствие', () => {
    const text = zoneNotice(view({ timeZoneId: null, effectiveTimeZoneId: 'Etc/UTC', effectiveOffset: 'UTC+00:00' }));
    expect(text).toContain('Пояс не выбран');
    expect(text).toContain('Etc/UTC (UTC+00:00)');
    expect(text).toContain('не в полночь');
  });

  it('выбранный неизвестен этой машине — сказано, что действует, и что делать', () => {
    const text = zoneNotice(view({ timeZoneId: 'Europe/Kirov', resolved: false, effectiveTimeZoneId: 'Etc/UTC' }));
    expect(text).toContain('«Europe/Kirov» этой машине неизвестен');
    expect(text).toContain('Etc/UTC (UTC+00:00)');
    expect(text).toContain('Выберите пояс заново');
  });
});

describe('что предложить на выбор', () => {
  it('первым — «не выбран»; дальше список сервера', () => {
    const choices = zoneChoices(view());
    expect(choices.map(c => c.value)).toEqual([SERVER_ZONE, 'Europe/Moscow', 'Asia/Vladivostok']);
    expect(choices[0].label).toBe('Не выбран — как на сервере');
    expect(choices[1].label).toBe('Москва (UTC+03:00)');
  });

  // Пояс задан запросом к API и в список не входит: без своего пункта поле показало бы «не выбран»,
  // и первое же сохранение его стёрло бы.
  it('выбранный вне списка остаётся пунктом — со своим смещением', () => {
    const choices = zoneChoices(view({
      timeZoneId: 'Asia/Novosibirsk', effectiveTimeZoneId: 'Asia/Novosibirsk', effectiveOffset: 'UTC+07:00',
    }));
    expect(choices.map(c => c.value)).toEqual([SERVER_ZONE, 'Asia/Novosibirsk', 'Europe/Moscow', 'Asia/Vladivostok']);
    expect(choices[1].label).toBe('Asia/Novosibirsk (UTC+07:00)');
  });

  it('неизвестный машине — тоже пунктом, и назван неизвестным, а не смещением сервера', () => {
    const choices = zoneChoices(view({ timeZoneId: 'Europe/Kirov', resolved: false, effectiveOffset: 'UTC+00:00' }));
    expect(choices[1]).toEqual({ value: 'Europe/Kirov', label: 'Europe/Kirov — неизвестен этой машине' });
  });
});

describe('число и час у компании', () => {
  it('показаны как пришли — пояс браузера их не двигает', () => {
    expect(companyNow('2026-10-13T14:32')).toBe('13.10.2026, 14:32');
    expect(companyNow('2026-01-01T00:05')).toBe('01.01.2026, 00:05');
  });
});

describe('пояс плановой копии (issue #1468)', () => {
  it('выбран — назван городом и смещением', () => {
    expect(backupZoneHint(view())).toBe('По поясу компании: Москва (UTC+03:00)');
  });

  it('не выбран — время по поясу сервера, и это сказано', () => {
    expect(backupZoneHint(view({ timeZoneId: null, effectiveTimeZoneId: 'Etc/UTC', effectiveOffset: 'UTC+00:00' })))
      .toBe('Пояс компании не выбран — время по поясу сервера: Etc/UTC (UTC+00:00)');
  });

  it('неизвестен машине — назван выбранный, время по серверу', () => {
    expect(backupZoneHint(view({ timeZoneId: 'Asia/Atlantis', resolved: false, effectiveTimeZoneId: 'Etc/UTC' })))
      .toBe('Пояс «Asia/Atlantis» этой машине неизвестен — время по поясу сервера: Etc/UTC (UTC+00:00)');
  });

  it('пояса нет в списке — назван идентификатором, а не пустотой', () => {
    expect(backupZoneHint(view({ timeZoneId: 'Asia/Tokyo', effectiveTimeZoneId: 'Asia/Tokyo', effectiveOffset: 'UTC+09:00' })))
      .toBe('По поясу компании: Asia/Tokyo (UTC+09:00)');
  });
});

describe('ближайшая плановая копия (issue #1468)', () => {
  // 21.10.2026, 23:30 по Москве — у Гринвича ещё 20:30.
  const now = Date.parse('2026-10-21T20:30:00Z');

  it('день — в поясе срока, а не браузера', () => {
    expect(nextBackupText('2026-10-22T04:00:00+03:00', now)).toBe('завтра, 04:00');
    expect(nextBackupText('2026-10-21T23:45:00+03:00', now)).toBe('сегодня, 23:45');
    // У Владивостока (UTC+10) уже 22-е: та же минута — «сегодня».
    expect(nextBackupText('2026-10-22T07:00:00+10:00', now)).toBe('сегодня, 07:00');
  });

  it('дальше завтра — датой; срок настал — в течение минуты', () => {
    expect(nextBackupText('2026-10-24T04:00:00+03:00', now)).toBe('24.10.2026, 04:00');
    expect(nextBackupText('2026-10-21T23:30:00+03:00', now)).toBe('в течение минуты');
  });

  it('по Гринвичу и нечитаемое', () => {
    expect(nextBackupText('2026-10-22T04:00:00Z', now)).toBe('завтра, 04:00');
    expect(nextBackupText('завтра', now)).toBeNull();
  });
});
