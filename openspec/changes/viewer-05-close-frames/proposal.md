# Proposal: просмотрщик PDF — закрыть фреймы (храповик 0, CSP)

Дочерний change зонтика `viewer-00-epic`, общая архитектура — `../viewer-00-epic/design.md` (D3, D8, D9,
D12).

## Why

Issue #1494 (эпик #1489, просмотрщик 5/5).

После `viewer-02`…`viewer-04` в клиенте не остаётся фрейма с PDF, кроме скрытого фрейма печати Ctrl+P
внутри просмотрщика (решение владельца, D3 зонтика). Пока храповик не на нуле и смотрит только
`features/`, новый фрейм может вернуться незамеченным, в том числе в `shared/`.

Политика поставки при этом пускает фреймы с `'self'`, хотя нужен только `blob:` фрейма печати. Кроме
того, вкладку по-прежнему открывают два способа: `openAttachmentInNewTab` и общая функция.

## What Changes

- **Храповик `native-pdf-frame`:**
  - базовый уровень → 0;
  - правило смотрит и `shared/`, кроме `shared/ui/viewer`. Исключению по пути нужна область правила по
    пути файла: её вводит открытый `sharedui-ratchet-shared-scope` (#1469); не слит — минимум ставится
    здесь, с тестом;
  - исключение `iframe` у `native-title` снимается (`scripts/ui-ratchet-lib.mjs:399`).
- **CSP поставки** (`deploy/nginx.conf`):
  - `frame-src 'self' blob:` → `frame-src blob:`, только то, что нужно фрейму печати;
  - в комментарий дописано, что `'unsafe-eval'` держит ещё и wasm pdf.js.
  Сторож CSP из `viewer-01` сверяет новую строку с `vite preview`.
- `openAttachmentInNewTab` и остальные открытия вкладки идут через одну функцию из `viewer-01`.
- `docs/UI.md`: исключение `iframe` у нативного `title` убрано, правило «фреймов нет» записано.

## Capabilities

### New Capabilities
- Нет.

### Modified Capabilities
- `sharedui`: единственный фрейм — печать, CSP поставки фреймов не пускает (`[UI-26]`); все открытия
  вкладки — одной функцией (`[UI-27]`).

## Impact

- **Клиент:**
  - `scripts/ui-ratchet-lib.mjs`, `scripts/ui-ratchet.test.mjs`, `ui-baseline.json`;
  - `shared/api/attachments.ts` (`openAttachmentInNewTab`);
  - его потребители не меняются: `features/quality-docs/*`, `features/datasets/SourcePreviewDialog.tsx`,
    `features/document-sets/editor/LinkPickerModal.tsx`, `features/settings/BugReportsPage.tsx`.
- **Поставка:** `deploy/nginx.conf` (CSP, комментарий).
- **Документы:** `docs/UI.md` («Подсказка» и абзац храповика о `native-title`), `docs/DEV_NOTES.md`
  (CSP поставки).
- **PR:** метка `ui`, без `version:minor`: поведения для пользователя не добавляет. «Closes #1494».
  Зависит от `viewer-02`, `viewer-03`, `viewer-04`.
