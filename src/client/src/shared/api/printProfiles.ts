import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Профили печати вида для чтения (issue #1454, страница — #1455): как раскладывать на бумагу книги
 * Excel, в которых автор раскладку не задал. Перечни — строками, как их отдаёт сервер.
 */
export type PrintPaper = 'A4' | 'A3' | 'A5';
export type PrintOrientation = 'portrait' | 'landscape';
export type PrintFit = 'width' | 'none';

export interface PrintMargins {
  top: number;
  bottom: number;
  left: number;
  right: number;
}

export interface PrintProfileParams {
  paper: PrintPaper;
  orientation: PrintOrientation;
  fit: PrintFit;
  margins: PrintMargins;
}

export interface PrintProfile extends PrintProfileParams {
  id: string;
  name: string;
  /** Профиль «по умолчанию»: по нему строится вид каждой книги, пока не выбран другой. */
  isSystem: boolean;
  /** Параметры совпадают с заводскими. У своего профиля заводских нет — всегда `false`. */
  isFactory: boolean;
  /** Заводские параметры — только у системного: по ним «Сбросить к заводским». */
  factory: (PrintProfileParams & { name: string }) | null;
}

export interface PrintProfileInput extends PrintProfileParams {
  name: string;
}

/** «A4 · книжная · по ширине · поля 5 мм»; разные поля — «поля 5/5/10/10 мм» (верх/низ/лево/право). */
export function profileSummary(p: PrintProfileParams): string {
  const orientation = p.orientation === 'landscape' ? 'альбомная' : 'книжная';
  const fit = p.fit === 'width' ? 'по ширине' : 'не вписывать';
  const { top, bottom, left, right } = p.margins;
  const margins = top === bottom && top === left && top === right ? `${top}` : `${top}/${bottom}/${left}/${right}`;
  return `${p.paper} · ${orientation} · ${fit} · поля ${margins} мм`;
}

const KEY = ['print-profiles'];
const URL = '/settings/print-profiles';

/** Список читает всякий, кто работает с файлами (право `core.files.use`) — им же кормится меню «Перестроить». */
export function usePrintProfiles({ enabled = true }: { enabled?: boolean } = {}) {
  return useQuery<PrintProfile[]>({
    queryKey: KEY,
    queryFn: () => apiClient.get(URL).then(r => r.data),
    enabled,
  });
}

export function useCreatePrintProfile() {
  const qc = useQueryClient();
  return useMutation<PrintProfile, Error, PrintProfileInput>({
    mutationFn: input => apiClient.post(URL, input).then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useUpdatePrintProfile() {
  const qc = useQueryClient();
  return useMutation<PrintProfile, Error, { id: string } & PrintProfileInput>({
    mutationFn: ({ id, ...input }) => apiClient.put(`${URL}/${id}`, input).then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useResetPrintProfile() {
  const qc = useQueryClient();
  return useMutation<PrintProfile, Error, { id: string }>({
    mutationFn: ({ id }) => apiClient.post(`${URL}/${id}/reset`).then(r => r.data),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}

export function useDeletePrintProfile() {
  const qc = useQueryClient();
  return useMutation<void, Error, { id: string }>({
    mutationFn: ({ id }) => apiClient.delete(`${URL}/${id}`).then(() => undefined),
    onSuccess: () => { qc.invalidateQueries({ queryKey: KEY }); },
  });
}
