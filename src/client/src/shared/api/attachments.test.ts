import { describe, it, expect } from 'vitest';
import { filenameFromContentDisposition } from './attachments';

describe('filenameFromContentDisposition', () => {
  it('предпочитает RFC 5987 filename*=UTF-8 (кириллица) ASCII-фолбэку с подчёркиваниями', () => {
    const cd = "attachment; filename=241101-____ 2_.pdf; filename*=UTF-8''241101-%D0%AD%D0%9E%D0%9C.pdf";
    expect(filenameFromContentDisposition(cd, 'fallback')).toBe('241101-ЭОМ.pdf');
  });

  it('берёт ASCII filename=, если звёздочного варианта нет', () => {
    const cd = 'attachment; filename="report.xlsx"';
    expect(filenameFromContentDisposition(cd, 'fallback')).toBe('report.xlsx');
  });

  it('снимает кавычки у plain filename', () => {
    expect(filenameFromContentDisposition('attachment; filename=data.csv', 'fb')).toBe('data.csv');
  });

  it('возвращает fallback без заголовка', () => {
    expect(filenameFromContentDisposition(undefined, 'fallback.pdf')).toBe('fallback.pdf');
  });

  it('возвращает fallback при пустом заголовке', () => {
    expect(filenameFromContentDisposition('', 'fallback.pdf')).toBe('fallback.pdf');
  });

  it('при повреждённом percent-кодировании падает в ASCII-фолбэк', () => {
    // Невалидный %-escape → decodeURIComponent бросает → берём plain filename=.
    const cd = "attachment; filename=safe.pdf; filename*=UTF-8''%E0%A4%A.pdf";
    expect(filenameFromContentDisposition(cd, 'fb')).toBe('safe.pdf');
  });
});
