import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { renameSnackbar } from './snackbar-rename.mjs';

// Кодмод PR 0 sharedui-md3-12-feedback: фикстуры — файлы в src/, которых на диске нет.
const srcRoot = join(import.meta.dirname, '..', 'src');
const run = (file, text) => renameSnackbar(text, join(srcRoot, file), { srcRoot });

describe('snackbar-rename', () => {
  it('хук, провайдер и модуль — по алиасу, локальная переменная `toast` остаётся', () => {
    const { text } = run('features/costs/X.tsx', [
      "import { useToast } from '@/shared/ui/Toast';",
      "import { ToastProvider } from '@/shared/ui/ToastProvider';",
      'const toast = useToast();',
      'const el = <ToastProvider>{null}</ToastProvider>;',
    ].join('\n'));
    expect(text).toBe([
      "import { useSnackbar } from '@/shared/ui/Snackbar';",
      "import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';",
      'const toast = useSnackbar();',
      'const el = <SnackbarProvider>{null}</SnackbarProvider>;',
    ].join('\n'));
  });

  it('строка модуля и ключ фабрики в vi.mock', () => {
    const { text } = run('features/costs/X.test.tsx',
      "vi.mock('@/shared/ui/Toast', () => ({ useToast: () => ({ apiError }) }));");
    expect(text).toBe("vi.mock('@/shared/ui/Snackbar', () => ({ useSnackbar: () => ({ apiError }) }));");
  });

  it('относительный путь — по разрешённому файлу: свой ./Toast меняется, чужой — нет', () => {
    expect(run('shared/ui/Dialog.tsx', "import { useToast } from './Toast';").text)
      .toBe("import { useSnackbar } from './Snackbar';");
    expect(run('features/x/Y.tsx', "import { Toast } from './Toast';").text)
      .toBe("import { Toast } from './Toast';");
    expect(run('features/costs/useRailSites.ts',
      "import { useSavedHiddenToast } from '../../shared/siteFilter/useSavedHiddenToast';").text)
      .toBe("import { useSavedHiddenSnackbar } from '../../shared/siteFilter/useSavedHiddenSnackbar';");
  });

  it('имена из прототипа объекта таблицей не считаются', () => {
    const source = 'const s = searchParams.toString() + x.constructor.name + o.hasOwnProperty("a");';
    expect(run('features/x/Y.ts', source)).toEqual({ text: source, changes: 0 });
  });

  it('тексты, комментарии и данные не трогаются; повторный прогон ничего не меняет', () => {
    const source = [
      "// useToast — старое имя",
      "const hint = 'useToast';",
      "openBugReport({ origin: 'toast' });",
      "import { useToast } from '@/shared/ui/Toast';",
    ].join('\n');
    const once = run('features/x/Y.ts', source);
    expect(once.changes).toBe(2);
    expect(once.text.split('\n').slice(0, 3)).toEqual(source.split('\n').slice(0, 3));
    expect(run('features/x/Y.ts', once.text).changes).toBe(0);
  });
});
