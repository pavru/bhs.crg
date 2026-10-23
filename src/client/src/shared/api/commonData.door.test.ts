import { describe, expect, it } from 'vitest';
import { WORK_TYPE_CODE, archiveRequest, ownDoorCode, recordWriteBase, recordWritePermission } from './commonData';

describe('адрес записи по типу — дверь видов работ (WORK-59)', () => {
  it('вид работы пишется своей дверью, остальное — общим адресом', () => {
    expect(recordWriteBase(WORK_TYPE_CODE)).toBe('/work-types');
    expect(recordWriteBase('Организация')).toBe('/common-data');
    // Тип не найден — запись ядра общего адреса: дверь видов работ по догадке не выбирается.
    expect(recordWriteBase(undefined)).toBe('/common-data');
  });

  it('подтип вида работы — тоже вид работы: дверь ищется по цепочке предков', () => {
    const types = [
      { id: 'w', code: WORK_TYPE_CODE, parentId: null },
      { id: 'w2', code: 'ВидРаботыЭлектромонтаж', parentId: 'w' },
      { id: 'w3', code: 'ВидРаботыКабель', parentId: 'w2' },
      { id: 'o', code: 'Организация', parentId: null },
      // Петля в данных не вешает поиск.
      { id: 'a', code: 'А', parentId: 'b' }, { id: 'b', code: 'Б', parentId: 'a' },
    ];
    expect(ownDoorCode('w3', types)).toBe(WORK_TYPE_CODE);
    expect(recordWriteBase(ownDoorCode('w2', types))).toBe('/work-types');
    expect(ownDoorCode('o', types)).toBeUndefined();
    expect(ownDoorCode('a', types)).toBeUndefined();
    expect(ownDoorCode('нет-такого', types)).toBeUndefined();
  });

  it('право записи — своё у классификатора', () => {
    expect(recordWritePermission(WORK_TYPE_CODE)).toBe('core.worktypes.edit');
    expect(recordWritePermission(undefined)).toBe('core.catalog.edit');
  });

  it('в архив вид работы уходит дверью и с версией', () => {
    expect(archiveRequest({ id: 'w1', archived: true, doorCode: WORK_TYPE_CODE, version: '7' }))
      .toEqual({ url: '/work-types/w1/archive', headers: { 'If-Match': '7' } });
  });

  it('вернуть из архива без версии можно общим адресом — он возвращает любую запись', () => {
    expect(archiveRequest({ id: 'w1', archived: false, doorCode: WORK_TYPE_CODE }))
      .toEqual({ url: '/common-data/w1/unarchive' });
    expect(archiveRequest({ id: 'w1', archived: false, doorCode: WORK_TYPE_CODE, version: '8' }))
      .toEqual({ url: '/work-types/w1/unarchive', headers: { 'If-Match': '8' } });
  });

  it('прочие записи архивируются общим адресом без версии', () => {
    expect(archiveRequest({ id: 'o1', archived: true, doorCode: 'Организация', version: '3' }))
      .toEqual({ url: '/common-data/o1/archive' });
  });
});
