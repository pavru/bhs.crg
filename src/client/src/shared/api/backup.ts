import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type {
  BackupFileInfo, BackupFilesResponse, BackupScheduleSettings, BackupSizeEstimate, RestoreReport,
} from './types';
import type { ActiveJob } from './jobs';

const FILES_KEY = ['backup', 'files'];

/**
 * Вес копии и предел загрузки через браузер. Считается на сервере по требованию (issue #711):
 * построение манифеста плюс запрос размера на каждый файл — дёшево, но не настолько, чтобы уходить
 * при каждой загрузке страницы настроек.
 */
export async function fetchBackupSize(): Promise<BackupSizeEstimate> {
  const response = await apiClient.get<BackupSizeEstimate>('/backup/size');
  return response.data;
}

/** Что лежит в каталоге копий на сервере (issue #831). */
export function useBackupFiles(enabled = true) {
  return useQuery({
    queryKey: FILES_KEY,
    queryFn: async () => (await apiClient.get<BackupFilesResponse>('/backup/files')).data,
    enabled,
    refetchOnWindowFocus: false,
  });
}

/**
 * Снять копию. Ответ — 202 и номер фоновой задачи: копия с библиотекой качества снимается минутами,
 * и ждать её ответом на запрос нельзя. Ход виден пилюлей задач, итог приходит в колокольчик.
 */
export function useCreateBackup() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async () => (await apiClient.post<{ jobId: string }>('/backup/files')).data,
    // Список обновится не сразу — задача только поставлена. Обновляем и задачи (пилюля появится
    // немедленно, не дожидаясь очередного опроса), и список (его освежит завершение задачи).
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['jobs', 'active'] });
      qc.invalidateQueries({ queryKey: FILES_KEY });
    },
  });
}

/**
 * Идёт ли прямо сейчас снятие копии (issue #831). Читает тот же кэш `['jobs','active']`, что и
 * пилюля задач в шапке, — отдельного опроса не заводим, иначе на странице настроек их стало бы два.
 */
export function useBackupJob(): ActiveJob | undefined {
  const { data } = useQuery<ActiveJob[]>({
    queryKey: ['jobs', 'active'],
    queryFn: async () => (await apiClient.get<ActiveJob[]>('/jobs/active')).data,
    refetchInterval: q => ((q.state.data?.length ?? 0) > 0 ? 2000 : 10000),
  });
  return (data ?? []).find(j => j.kind === 'CreateBackup');
}

/**
 * Сохранить расписание. Отдельным вызовом, а не куском общих настроек: формы разделов не должны
 * затирать друг друга (та же причина, что у почты и проверки обновлений).
 */
export function useSaveBackupSchedule() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (dto: BackupScheduleSettings) => apiClient.put('/backup/schedule', dto),
    onSuccess: () => qc.invalidateQueries({ queryKey: FILES_KEY }),
  });
}

export function useDeleteBackupFile() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (fileName: string) =>
      apiClient.delete(`/backup/files/${encodeURIComponent(fileName)}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: FILES_KEY }),
  });
}

/**
 * Восстановление из копии, ЛЕЖАЩЕЙ НА СЕРВЕРЕ: по сети идёт одно имя файла, а не гигабайты
 * (issue #831). Предела размера у этого пути нет вовсе — он и заведён затем, чтобы предел
 * транспорта перестал решать, какую копию система согласна принять.
 */
export function useRestoreFromFile() {
  return useMutation({
    mutationFn: async (fileName: string) =>
      (await apiClient.post<RestoreReport>('/backup/restore', { fileName })).data,
  });
}

/**
 * Принести копию с другой установки через браузер — удобство для небольших файлов. Файл ложится в
 * тот же каталог, дальше он неотличим от снятого здесь. Крупную копию так не переносят: предел
 * транспорта (BACKUP_MAX_ARCHIVE_MB) остаётся, и отказ по нему называет выход — каталог на хосте.
 */
export async function uploadBackupFile(file: File): Promise<BackupFileInfo> {
  const formData = new FormData();
  formData.append('file', file);
  const response = await apiClient.post<BackupFileInfo>('/backup/files/upload', formData);
  return response.data;
}

/**
 * Забрать копию через браузер.
 *
 * Файл проходит через память вкладки (ответ приходит Blob-ом — иначе к запросу не приложить
 * заголовок авторизации). Для копии в гигабайты это не путь, и интерфейс говорит об этом прямо:
 * такую копию забирают из каталога на сервере.
 */
export async function downloadBackupFile(fileName: string): Promise<void> {
  const response = await apiClient.get<Blob>(`/backup/files/${encodeURIComponent(fileName)}`,
    { responseType: 'blob' });
  const url = URL.createObjectURL(response.data);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}
