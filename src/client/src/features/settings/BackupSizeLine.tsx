import { useQuery } from '@tanstack/react-query';
import { AlertTriangle } from 'lucide-react';
import { formatBytes } from '@/shared/api/attachments';
import { apiError, isConflict } from '@/shared/utils/apiError';
import { fetchBackupSize } from '@/shared/api/backup';
import type { BackupScope } from '@/shared/api/types';

// Вес копии против предела загрузки через браузер. Вынесен из BackupSection (issue #1427): файл
// стоит на храповике размера, а строке веса понадобился ещё один исход — отказ хранилища.

/**
 * Сколько весит копия ВЫБРАННОГО состава и на каком пороге её откажется принять ЗАГРУЗКА через
 * браузер (issue #711, #833).
 *
 * Считается по требованию и только для выбранного состава: полный манифест — это все объекты с их
 * данными и все источники наборов вместе с разобранным кэшем, и держать его в памяти сервера ради
 * строки на экране настроек можно лишь тогда, когда именно этот состав человек и выбрал.
 *
 * Запрос уходит только когда раздел раскрыт: CollapsibleSection не монтирует содержимое свёрнутого.
 */
export function BackupSizeLine({ scope }: { scope: BackupScope }) {
  const { data, isPending, isError, error } = useQuery({
    queryKey: ['backup', 'size', scope],
    queryFn: () => fetchBackupSize(scope),
    staleTime: 5 * 60 * 1000,
    refetchOnWindowFocus: false,
    retry: false,
  });

  if (isPending) return <p className="text-xs text-fg4">Размер копии оценивается…</p>;
  // Хранилище отказало (issue #1427) — это не «оценить не удалось», а «копия выйдет без файлов»:
  // самый опасный исход раздела, и выглядеть он обязан предупреждением, а не серой подсказкой.
  // Текст — сервера: он называет, что проверить. Только по 409: у сетевой ошибки текста сервера
  // нет, и apiError показал бы английское «Network Error».
  if (isError && isConflict(error)) {
    return (
      <p className="text-xs text-warning flex items-start gap-1">
        <AlertTriangle size={13} className="shrink-0 mt-px" />
        <span>{apiError(error, 'Хранилище файлов отказало в ответе.')}</span>
      </p>
    );
  }
  // Молча ничего не показывать нельзя, но и мешать копированию нечем: кнопка работает и без оценки.
  if (isError || !data) return <p className="text-xs text-fg4">Размер копии оценить не удалось.</p>;

  const variant = data.variant;
  const exceedsUploadLimit = variant.totalBytes > data.limitBytes;

  return (
    <div className="space-y-1">
      <p className="text-xs text-fg3">
        Размер копии ≈ <b>{formatBytes(variant.totalBytes)}</b>
        {variant.blobCount > 0 && <> · файлов: {variant.blobCount}</>}
      </p>
      {/*
        Предел касается ТОЛЬКО браузерной загрузки: копия сверх него снимается и восстанавливается
        по-прежнему — просто переносится файлом. Сказать об этом надо заранее: с полным составом
        (умолчание) перерасти предел стало обычным делом, и узнать о нём при попытке загрузить
        копию на новый сервер значит узнать в середине переезда.
      */}
      {exceedsUploadLimit && (
        <p className="text-xs text-warning flex items-start gap-1">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>
            Копия крупнее {formatBytes(data.limitBytes)} — через браузер её не загрузить. Для
            переноса кладите файл в каталог на сервере (путь показан ниже); скачивать и загружать
            такую копию вкладкой браузера не стоит.
          </span>
        </p>
      )}
      {variant.missingBlobCount > 0 && (
        <p className="text-xs text-warning flex items-start gap-1">
          <AlertTriangle size={13} className="shrink-0 mt-px" />
          <span>
            Файлов нет в хранилище: {variant.missingBlobCount}. В копию они не попадут — ссылки на
            них останутся битыми и после восстановления.
          </span>
        </p>
      )}
    </div>
  );
}
