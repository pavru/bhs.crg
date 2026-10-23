import { describe, expect, it } from 'vitest';
import { WORK_TYPE_CODE, archiveRequest, recordWriteBase, recordWritePermission } from './commonData';

describe('адрес записи по типу — дверь видов работ (WORK-59)', () => {
  it('вид работы пишется своей дверью, остальное — общим адресом', () => {
    expect(recordWriteBase(WORK_TYPE_CODE)).toBe('/work-types');
    expect(recordWriteBase('Организация')).toBe('/common-data');
    // Тип не найден — запись ядра общего адреса: дверь видов работ по догадке не выбирается.
    expect(recordWriteBase(undefined)).toBe('/common-data');
  });

  it('право записи — своё у классификатора', () => {
    expect(recordWritePermission(WORK_TYPE_CODE)).toBe('core.worktypes.edit');
    expect(recordWritePermission('ЕдиницаИзмерения')).toBe('core.catalog.edit');
  });

  it('в архив вид работы уходит дверью и с версией', () => {
    expect(archiveRequest({ id: 'w1', archived: true, typeCode: WORK_TYPE_CODE, version: '7' }))
      .toEqual({ url: '/work-types/w1/archive', headers: { 'If-Match': '7' } });
  });

  it('вернуть из архива без версии можно общим адресом — он возвращает любую запись', () => {
    expect(archiveRequest({ id: 'w1', archived: false, typeCode: WORK_TYPE_CODE }))
      .toEqual({ url: '/common-data/w1/unarchive' });
    expect(archiveRequest({ id: 'w1', archived: false, typeCode: WORK_TYPE_CODE, version: '8' }))
      .toEqual({ url: '/work-types/w1/unarchive', headers: { 'If-Match': '8' } });
  });

  it('прочие записи архивируются общим адресом без версии', () => {
    expect(archiveRequest({ id: 'o1', archived: true, typeCode: 'Организация', version: '3' }))
      .toEqual({ url: '/common-data/o1/archive' });
  });
});
