import type { QueryClient } from '@tanstack/react-query';
import { isConflict } from '@/shared/utils/apiError';
import type { DataSetSource } from './types';

// Кэш списков наборов глазами мутаций источника: сбросить, вписать сохранённое, перечитать после
// отказа. Отдельным файлом, а не в datasets.ts: тот вырос до храповика размера, а пользуются этим
// и он, и datasetProcessing.ts.

/**
 * Список источников изменился: перечитать и наборы, и КАНДИДАТОВ (issue #717).
 *
 * Ключ кандидатов — ['datasets','source-candidates',fileId], и сброс ['datasets','files'] его не
 * задевает: префикс другой. Пока занятый кандидат просто исчезал, это было незаметно; теперь у него
 * есть счётчик и действие «Добавить ещё», и без сброса панель сразу после создания показывала бы
 * старое состояние — то есть прятала бы вход ровно тогда, когда он нужен.
 */
export function invalidateSources(qc: QueryClient) {
  qc.invalidateQueries({ queryKey: ['datasets', 'files'] });
  qc.invalidateQueries({ queryKey: ['datasets', 'source-candidates'] });
  // ...и список доступного документу: привязку выбирают ПО ИСТОЧНИКУ, так что создание или
  // удаление источника меняет и его. Ключ ['datasets','available'] под префикс files не попадает.
  qc.invalidateQueries({ queryKey: ['datasets', 'available'] });
}

type SourceField = keyof DataSetSource;

/** Что меняет правка обработки. */
export const PROCESSING_FIELDS = ['rowFilter', 'computedColumns', 'sortSpec'] as const satisfies readonly SourceField[];
/** Что меняет правка извлечения: определение и разобранная по нему схема. */
export const EXTRACTION_FIELDS =
  ['name', 'sheetOrPath', 'columnExpressions', 'cachedSchema', 'cachedRowCount'] as const satisfies readonly SourceField[];
/** Что меняет настройка материализации. */
export const MATERIALIZATION_FIELDS =
  ['materializeTypeId', 'materializeMapping', 'materializeDiscriminator', 'materializeByIdColumn'] as const satisfies readonly SourceField[];

/** Версии едут с любой правкой: ради них всё и затеяно. */
const VERSION_FIELDS = ['processingVersion', 'materializationVersion'] as const satisfies readonly SourceField[];

/**
 * Вписывает сохранённое в кэш списков наборов сразу, из ответа мутации, — и только потом список
 * перечитывается в фоне (issue #1141).
 *
 * Зачем сразу. Диалоги источника открываются со СНИМКА его копии на странице и называют серверу её
 * версию. Пока страница не узнала о своей же правке, следующий диалог открылся бы с прежней версии
 * и получил бы отказ «источник тем временем изменили» — на правку, которую сделал сам человек.
 * Ждать ради этого перечитывания списка нельзя: у системных наборов оно пересчитывает живые
 * счётчики и идёт секундами, а диалог всё это время висел бы на «Сохранение…».
 *
 * Почему только `changed`, а не весь ответ. Ответ мутации беднее списка: счётчика привязок в нём нет
 * («не считали»), а у системного источника нет живых схемы, числа строк и оговорки — список считает
 * их на чтении. Вписанный целиком, ответ стёр бы их до фонового перечитывания.
 */
export function putSourceInCache(qc: QueryClient, saved: DataSetSource, changed: readonly SourceField[]) {
  const patch = Object.fromEntries([...changed, ...VERSION_FIELDS].map(field => [field, saved[field]]));
  qc.setQueriesData<unknown>({ queryKey: ['datasets', 'files'] }, (cached: unknown) => {
    // Под этим префиксом лежат не только списки наборов (страницы PDF — ['datasets','files',id,'pages']):
    // трогаем лишь то, что выглядит набором с источниками.
    if (!Array.isArray(cached)) return cached;
    return cached.map((file: { id?: string; sources?: DataSetSource[] } | null) =>
      file?.id === saved.fileId && Array.isArray(file.sources)
        ? { ...file, sources: file.sources.map(s => (s.id === saved.id ? { ...s, ...patch } : s)) }
        : file);
  });
}

/** Правка источника сохранена: вписать её в кэш и перечитать списки в фоне. */
export function sourceSaved(qc: QueryClient, saved: DataSetSource, changed: readonly SourceField[]) {
  putSourceInCache(qc, saved, changed);
  invalidateSources(qc);
}

/**
 * Источник тем временем изменили (409): страница показывает прежнюю копию. Перечитываем её сразу,
 * не дожидаясь, пока человек обновит страницу сам, — значки в списке перестают врать. Открытому
 * диалогу это не помогает и помогать не должно: он собран по прежней копии и называет её версию,
 * так что повторное «Сохранить» получит тот же отказ.
 */
export function refreshIfSourceMoved(qc: QueryClient, error: unknown) {
  if (isConflict(error)) invalidateSources(qc);
}
