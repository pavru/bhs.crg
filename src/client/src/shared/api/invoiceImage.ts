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
  /**
   * Откуда раскладка вида на бумагу (issue #1456): `profile` — книга `.xlsx` без своей раскладки, профиль
   * печати к ней применяется (или применится перестроением); `file` — раскладку задал автор книги;
   * `null` — не книга `.xlsx`: профили к ней не относятся.
   */
  layout: 'profile' | 'file' | null;
  /** Профиль, по которому построен вид, — как он называется сейчас; `null` — не записан или удалён. */
  profile: { id: string; name: string } | null;
  /**
   * `changed` — профиль правили после построения; `deleted` — его удалили; `none` — вид построен до
   * профилей. `null` — профиль к виду не относится или подставить его не удалось (пометка построителя).
   */
  profileState: 'current' | 'changed' | 'deleted' | 'none' | null;
  /**
   * Пометка построителя о раскладке по профилю (issue #1495): как вид разложен и чем отличается от печати
   * в Excel, либо что профиль применить не удалось. В `notes` её нет — где показать, решает экран.
   * `null` — вид по профилю не раскладывался.
   */
  layoutNote: string | null;
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
 *
 * <p>`printProfileId` — профиль печати, выбранный человеком (issue #1456); не назван — сервер возьмёт
 * профиль прежнего вида. Выбор сервер запоминает за файлом.</p>
 */
export function useRebuildInvoiceImage(id: string, blobPath: string | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (printProfileId: string | void) => apiClient
      .post<InvoiceImage>(`/costs/invoices/${id}/scan/image`, printProfileId ? { printProfileId } : undefined)
      .then(r => r.data),
    onSuccess: image => {
      // Отказ построенного вида не затирает: сервер оставил прежний, и на экране остаётся он же.
      // Причину отказа получает тот, кто нажал, — ответом этого вызова.
      const shown = qc.getQueryData<InvoiceImage>(imageKey(id, blobPath));
      if (image.state !== 'refused' || shown?.state !== 'built') qc.setQueryData(imageKey(id, blobPath), image);
      void qc.invalidateQueries({ queryKey: recognitionKey(id) });
    },
  });
}

/**
 * Сам вид — PDF, байтами (issue #1490): просмотрщику нужны байты, а не адрес — под `connect-src 'self'`
 * поставки `blob:` он не прочёл бы. Тип ставим сами: ответу сервера не доверяем.
 */
export async function loadInvoiceImage(id: string): Promise<Blob> {
  const response = await apiClient.get(`/costs/invoices/${id}/scan/image/content`, { responseType: 'blob' });
  return new Blob([response.data as BlobPart], { type: 'application/pdf' });
}
