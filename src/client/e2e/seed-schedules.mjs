// Посев Plan-20. Работы соседнего графика добавляет прогон после перезапуска API и проекции видов работ.
import { randomUUID } from 'node:crypto';

export const SCHEDULE_ESTIMATE = 'ЭОМ для графика (посев Plan-20)';
export const NEIGHBOR_SCHEDULE = 'Соседний график (посев Plan-20)';

export async function seedSchedule({ api, constructionId, estimates, importEstimate }) {
  const estimate = estimates.find(row => row.name === SCHEDULE_ESTIMATE) ?? (await importEstimate(SCHEDULE_ESTIMATE)).estimate;
  const graphs = await api('GET', `/plan/schedules?constructionId=${constructionId}`);
  const neighbor = graphs.find(row => row.name === NEIGHBOR_SCHEDULE) ?? await api('POST', '/plan/schedules', {
    constructionId, revisionId: null, creationKey: randomUUID(), name: NEIGHBOR_SCHEDULE,
  });
  return { scheduleEstimateId: estimate.id, neighborScheduleId: neighbor.id };
}
