import { describe, expect, it } from 'vitest';
import { scopePurgeOffered, type ScopePurgeOffer } from '@/shared/api/scopePurge';
import { purgeFailure } from './catalog/purgeRecord';
import { exitNote, leavingLines, levelWords, scopeConsequences, scopePurgedToast } from './scopePurge';

const answer = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });
const purge = (references: number, allowed = true) =>
  ({ allowed, references, untraceable: references, holders: allowed ? [{ owner: 'М', what: 'x', rows: references, documents: null, traceable: false }] : [] });
const leaving = { sections: 2, sets: 1, objects: 5, qualityDocuments: 0, workPlanItems: 0 };

describe('предложение из отказа удаления уровня', () => {
  it('несёт состав уходящего', () => {
    const offer = scopePurgeOffered(answer(409, { error: 'держат', purge: purge(3), leaving }));
    expect(offer?.references).toBe(3);
    expect(offer?.leaving).toEqual(leaving);
  });

  it('без права — без состава, без поля purge — предложения нет', () => {
    expect(scopePurgeOffered(answer(409, { error: 'держат', purge: purge(3, false) }))?.leaving).toBeNull();
    expect(scopePurgeOffered(answer(409, { error: 'ядро', purge: null }))).toBeNull();
  });
});

describe('слова уровня', () => {
  it('выход в отказе: с правом — действие, без права — «может администратор»', () => {
    expect(exitNote('construction', true)).toBe('Стройку можно удалить совсем — ссылки будут потеряны.');
    expect(exitNote('section', false)).toBe('Удалить раздел, потеряв ссылки, может администратор.');
  });

  it('нулевые строки состава не выводятся, а пустой состав — пуст', () => {
    expect(leavingLines(leaving)).toEqual([
      '2 раздела', '1 комплект', 'документы (с их PDF) и записи общих данных — 5',
    ]);
    expect(leavingLines({ ...leaving, sections: 0, sets: 0, objects: 0 })).toEqual([]);
    expect(leavingLines(null)).toEqual([]);
  });

  it('последствия называют уровень, а о ссылках говорят те же слова, что у записи', () => {
    const lines = scopeConsequences('set', { ...purge(2), leaving: null });
    expect(lines[0]).toContain('Комплект со всем содержимым будет удалён совсем');
    expect(lines[1]).toContain('будут вести в пустоту');
    expect(lines.at(-1)).toContain('какой комплект');
  });

  it('успех согласован в роде', () => {
    expect(scopePurgedToast('construction', 'ЖК', 3)).toBe('Стройка «ЖК» удалена. Потеряно ссылок: 3.');
    expect(scopePurgedToast('section', 'ЭОМ', 1)).toBe('Раздел «ЭОМ» удалён. Потеряно ссылок: 1.');
  });
});

describe('неудача удаления уровня', () => {
  const shown: ScopePurgeOffer = { ...purge(3), leaving };
  const words = levelWords('construction').outcome;

  it('пересчёт приносит и новый состав — иначе блок «уйдёт вместе» остался бы прежним', () => {
    const fresh = { ...leaving, sets: 4 };
    const failure = purgeFailure(
      answer(409, { error: 'Число ссылок не совпало', purge: purge(5), leaving: fresh }), shown, words, scopePurgeOffered);
    expect(failure.kind).toBe('recount');
    if (failure.kind === 'recount') {
      expect(failure.offer.leaving).toEqual(fresh);
      expect(failure.note).toContain('было 3, стало 5');
    }
  });

  it('исчезнувшая стройка и немой сервер названы словами стройки', () => {
    expect(purgeFailure(answer(404, ''), shown, words, scopePurgeOffered))
      .toEqual({ kind: 'final', text: 'Стройки уже нет: её удалили, пока диалог был открыт.' });
    const silent = purgeFailure(new Error('Network Error'), shown, words, scopePurgeOffered);
    expect(silent.kind).toBe('retry');
    expect(silent.kind === 'retry' && silent.text).toContain('Удалена ли стройка — неизвестно');
  });
});
