import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import { recognitionKey } from './invoiceRecognition';

/**
 * Читаемый образ файла счёта (issue #1270): Excel и Word рядом с формой показываются не сами, а
 * видом для чтения — PDF, который из них строит сервер.
 *
 * ⚠️ Документ — оригинал. Вид — приближённая перевёрстка: текст и числа взяты из файла, а
 * расположение, шрифты и разбивка на страницы — нет. Скачивается всегда приложенный файл.
 */
export interface InvoiceImage {
  /** `original` — вида нет и не нужно: показывается сам файл (PDF, изображение) либо показать нечем. */
  state: 'original' | 'built' | 'refused';
  pages: number | null;
  /** Пометки построителя: чем вид отличается от файла. */
  notes: string[];
  converter: string | null;
  builtAt: string | null;
  /** Почему вид не построен — словами сервера. */
  reason: string | null;
  /**
   * Отказ — свойство файла (пароль, пуст, повреждён), и сервер его запомнил: сам собой вид не
   * построится, только «Построить заново». Иначе дело в сервисе или установке — конвертер занят,
   * молчит, не настроен, — и следующий вопрос строит заново.
   */
  aboutFile: boolean;
}

// ⚠️ Ключ — НЕ под ключом счетов: каждое сохранение счёта сбрасывает всё под ним, а вопрос о виде
// при отсутствии записи строит его — конвертер получал бы по преобразованию на каждое «Сохранить».
const imageKey = (id: string, blobPath: string | null) => ['costs-invoice-image', id, blobPath ?? ''] as const;

/**
 * Состояние вида. Первый вопрос о файле, приложенном до появления видов, СТРОИТ его — поэтому ответ
 * может идти секунды, и повторять вопрос сам по себе запрос не должен: построение не ускорится.
 *
 * @param blobPath путь файла — в ключе: после «Заменить файл» счёт тот же, а вид другой.
 */
export function useInvoiceImage(id: string, blobPath: string | null) {
  return useQuery({
    queryKey: imageKey(id, blobPath),
    queryFn: () => apiClient.get<InvoiceImage>(`/costs/invoices/${id}/scan/image`).then(r => r.data),
    staleTime: Infinity,
    retry: false,
  });
}

/**
 * «Перестроить» — и оно же «Повторить» после отказа сервиса. В счёт не пишет, версии не спрашивает.
 * Распознанное перечитывается: оно теперь может оказаться «по прежнему виду».
 */
export function useRebuildInvoiceImage(id: string, blobPath: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => apiClient.post<InvoiceImage>(`/costs/invoices/${id}/scan/image`).then(r => r.data),
    onSuccess: image => {
      // Отказ построенного вида не затирает: сервер оставил прежний, и на экране остаётся он же.
      // Причину отказа получает тот, кто нажал, — ответом этого вызова.
      const shown = qc.getQueryData<InvoiceImage>(imageKey(id, blobPath));
      if (image.state !== 'refused' || shown?.state !== 'built') qc.setQueryData(imageKey(id, blobPath), image);
      void qc.invalidateQueries({ queryKey: recognitionKey(id) });
    },
  });
}

/** Сам вид — PDF. Ссылку отзывает тот, кто её получил. */
export async function loadInvoiceImage(id: string): Promise<string> {
  const response = await apiClient.get(`/costs/invoices/${id}/scan/image/content`, { responseType: 'blob' });
  return URL.createObjectURL(new Blob([response.data as BlobPart], { type: 'application/pdf' }));
}
