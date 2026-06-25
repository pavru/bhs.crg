import { useRef, useState } from 'react';
import {
  useUpdateDataSetFile,
  useDeleteDataSetFile,
  downloadDataSetFile,
} from '@/shared/api/datasets';
import type { CatalogScope, DataSetFile } from '@/shared/api/types';

/**
 * Shared state + handlers for a single data-set file row (download / replace / delete).
 * Keeps the two presentational FileRow variants (full page vs scoped panel) free of logic.
 */
export function useDataSetFileActions(file: DataSetFile, scope: CatalogScope, scopeId?: string) {
  const del = useDeleteDataSetFile();
  const update = useUpdateDataSetFile();
  const [confirming, setConfirming] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const updateInputRef = useRef<HTMLInputElement>(null);

  async function handleReplace(e: React.ChangeEvent<HTMLInputElement>) {
    const f = e.target.files?.[0];
    if (!f) return;
    await update.mutateAsync({ id: file.id, file: f, scope, scopeId });
    if (updateInputRef.current) updateInputRef.current.value = '';
  }

  async function handleDownload() {
    setDownloading(true);
    try { await downloadDataSetFile(file.id, file.name); }
    finally { setDownloading(false); }
  }

  async function handleDelete() {
    await del.mutateAsync({ id: file.id, scope, scopeId });
    setConfirming(false);
  }

  return {
    del, update,
    confirming, setConfirming,
    downloading,
    updateInputRef,
    handleReplace, handleDownload, handleDelete,
  };
}
