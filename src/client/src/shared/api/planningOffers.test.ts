import { describe, expect, it, vi } from 'vitest';
import { apiClient } from './client';
import { offerRefusal, readOfferPreview, writeOffer, type OfferCommand } from './planningOffers';

vi.mock('./client', () => ({ apiClient: { get: vi.fn(), put: vi.fn(), post: vi.fn() } }));

describe('ответы и версия КП', () => {
  it('различает валидацию, запрет, конфликт версии и устаревший предпросмотр', () => {
    const failure = (status: number, data: unknown) => offerRefusal({ response: { status, data } }, 'Ошибка КП');
    expect(failure(400, { error: 'Укажите дату' })).toEqual({ kind: 'validation', message: 'Укажите дату' });
    expect(failure(403, '')).toMatchObject({ kind: 'forbidden' });
    expect(failure(409, { error: 'КП тем временем изменили' })).toMatchObject({ kind: 'conflict' });
    const preview = { version: '82', fingerprint: 'new', source: { id: 'r1' }, target: { id: 'r3' }, changed: [],
      added: [], removed: [], unmatched: [], unmatchedSectionMarkups: [] };
    expect(failure(409, { error: 'Смета изменилась', preview })).toEqual({ kind: 'preview-changed', message: 'Смета изменилась', preview });
    expect(failure(409, { preview: { target: {} } })).toMatchObject({ kind: 'conflict' });
  });

  const commands: [string, OfferCommand, string, unknown][] = [
    ['put', { type: 'markups', input: { markup: 11, sections: [], lines: [] } }, '/markups', { markup: 11, sections: [], lines: [] }],
    ['post', { type: 'send', sentOn: '2026-11-23' }, '/send', { sentOn: '2026-11-23' }],
    ['post', { type: 'outcome', status: 'accepted' }, '/outcome', { status: 'accepted' }],
    ['post', { type: 'next' }, '/versions', undefined],
    ['post', { type: 'rebase', fingerprint: 'fingerprint', revisionId: 'r3' }, '/rebase', { revisionId: 'r3', fingerprint: 'fingerprint' }],
  ];

  it.each(commands)('%s с версией: %j', async (method, command, suffix, body) => {
    const request = vi.mocked(method === 'put' ? apiClient.put : apiClient.post);
    const offer = { id: 'o1' };
    request.mockResolvedValueOnce({ data: command.type === 'rebase' ? { offer, removed: [] } : offer });
    expect(await writeOffer('o1', '79', command)).toEqual(offer);
    expect(request).toHaveBeenLastCalledWith(`/plan/offers/o1${suffix}`, body, { headers: { 'If-Match': '79' } });
  });

  it('предпросмотр получает указанную редакцию', async () => {
    vi.mocked(apiClient.get).mockResolvedValueOnce({ data: { fingerprint: 'p' } });
    expect(await readOfferPreview('o1', 'r2')).toEqual({ fingerprint: 'p' });
    expect(apiClient.get).toHaveBeenLastCalledWith('/plan/offers/o1/rebase-preview', expect.objectContaining({ params: { revisionId: 'r2' } }));
  });
});
