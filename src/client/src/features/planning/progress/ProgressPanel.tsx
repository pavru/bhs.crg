import { useState } from 'react';
import { useGetConstruction } from '@/shared/api/constructions';
import { useConstructionProgress } from '@/shared/api/progress';
import { Banner } from '@/shared/ui/Banner';
import { DateField } from '@/shared/ui/DateField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError } from '@/shared/utils/apiError';
import { ProgressGrid } from './ProgressGrid';
import { LABOR_STRIP, ProgressLaborStrip } from './ProgressLaborStrip';
import { ProgressStrips } from './ProgressStrips';

const WHOLE = 'whole-construction';

export function ProgressPanel({ constructionId, sectionId }: { constructionId: string; sectionId?: string }) {
  return <ProgressContent key={`${constructionId}/${sectionId ?? ''}`} constructionId={constructionId} sectionId={sectionId} />;
}

function ProgressContent({ constructionId, sectionId }: { constructionId: string; sectionId?: string }) {
  const [filter, setFilter] = useState(WHOLE);
  const [laborMonth, setLaborMonth] = useState<string>();
  const site = useGetConstruction(constructionId);
  const sections = site.data?.sections ?? [];
  const active = sectionId ?? (sections.some(section => section.id === filter) ? filter : undefined);
  const progress = useConstructionProgress(constructionId, active, laborMonth);
  const data = progress.isError ? undefined : progress.data;
  const labor = data?.strips.find(strip => strip.code === LABOR_STRIP);

  return <section aria-labelledby="construction-progress-title" className="@container grid min-w-0 gap-4">
    <header className="flex flex-wrap items-center justify-between gap-3">
      <h2 id="construction-progress-title" className="text-title-large">Ход работ</h2>
      {sectionId === undefined && sections.length > 0 && <div className="w-64 max-w-full">
        <Select label="Раздел стройки" density="dense" value={active ?? WHOLE} onValueChange={setFilter}>
          <SelectItem value={WHOLE}>Вся стройка</SelectItem>
          {sections.map(section => <SelectItem key={section.id} value={section.id}>{section.name}</SelectItem>)}
        </Select>
      </div>}
    </header>
    {progress.isPending && <p role="status" className="text-body-medium">Читаем ход работ…</p>}
    {progress.isError && <Banner tone="error" actions={[{ label: 'Повторить', onClick: () => void progress.refetch() }]}>
      {apiError(progress.error, 'Ход работ не прочитался.')}
    </Banner>}
    {data && <>
      <ProgressStrips strips={data.strips.filter(strip => strip.code !== LABOR_STRIP)} costs={data.costs} sectionId={data.sectionId}
        columns={data.columns}
        partial={data.columns.some(column => column.code === 'fact' && column.state === 'partial')} />
    </>}
    {(labor?.month || laborMonth) && <div className="w-64 max-w-full">
      <DateField label="Месяц трудозатрат" precision="month" density="dense"
        value={laborMonth ?? labor?.month ?? ''} onChange={value => setLaborMonth(value || undefined)} />
    </div>}
    {labor && <ProgressLaborStrip strip={labor} />}
    {data && <>
      <h3 className="text-title-medium">Виды работ</h3>
      <ProgressGrid columns={data.columns} rows={data.rows} section={data.sectionId !== null} />
    </>}
  </section>;
}
