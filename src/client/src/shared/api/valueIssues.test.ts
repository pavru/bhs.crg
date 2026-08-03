import { describe, it, expect } from 'vitest';
import { valueIssuesByPath, deepIssueCount, issueCountInFields } from './valueIssues';
import type { AuditFinding } from './documentTypes';

const finding = (code: string, path: string, message = 'сообщение'): AuditFinding => ({
  instanceId: 'i1', instanceName: 'Документ', code, severity: 'Warning', path, message,
});

describe('valueIssuesByPath', () => {
  it('берёт только находки о значении', () => {
    const issues = valueIssuesByPath([
      finding('value-type', 'Кол'),
      finding('type-mismatch', 'Работы'),
      // Осиротевший ключ форме показывать негде — такого поля в схеме нет.
      finding('orphan-key', 'Лишнее'),
    ]);
    expect([...issues.keys()]).toEqual(['Кол', 'Работы']);
  });

  it('копит несколько сообщений на одном пути', () => {
    // У одного значения бывает сразу две претензии: «не целое» и «меньше допустимого».
    const issues = valueIssuesByPath([
      finding('value-type', 'Кол', 'не целое'),
      finding('value-type', 'Кол', 'меньше допустимого'),
    ]);
    expect(issues.get('Кол')).toEqual(['не целое', 'меньше допустимого']);
  });

  it('пустой вход даёт пустую карту', () => {
    expect(valueIssuesByPath(undefined).size).toBe(0);
    expect(valueIssuesByPath([]).size).toBe(0);
  });
});

describe('deepIssueCount', () => {
  const issues = valueIssuesByPath([
    finding('value-type', 'Работы[0].Порядок'),
    finding('value-type', 'Работы[3].Порядок'),
    finding('value-type', 'Работы'),
    finding('value-type', 'Кол'),
  ]);

  it('считает только вложенные, не само поле', () => {
    // Претензия к самому полю рисуется подсказкой под ним — в свод-бейдже она была бы вторым счётом.
    expect(deepIssueCount(issues, 'Работы')).toBe(2);
  });

  it('не путает поля с общим началом имени', () => {
    const other = valueIssuesByPath([finding('value-type', 'Количество')]);
    expect(deepIssueCount(other, 'Кол')).toBe(0);
  });
});

describe('issueCountInFields', () => {
  it('складывает свои и вложенные по полям раздела', () => {
    const issues = valueIssuesByPath([
      finding('value-type', 'Кол'),
      finding('value-type', 'Работы[0].Порядок'),
      finding('value-type', 'Работы'),
    ]);
    expect(issueCountInFields(issues, ['Кол', 'Работы'])).toBe(3);
    expect(issueCountInFields(issues, ['Номер'])).toBe(0);
  });
});
