import { storyId, type Story } from './story-model';

/** Keep only combinations supported by the current component APIs. */
export function normalizeStories(input: Story[]): Story[] {
  const output = input.filter(s => {
    const variant = s.variant;
    if (s.component === 'NumberInput' && (/Density=form/.test(variant) || /State=(ReadOnly|Error)/.test(variant))) return false;
    if (s.component === 'TimeInput' && !variant.includes('Density=cell')) return false;
    if (s.component === 'TimeField' && variant.includes('State=ReadOnly')) return false;
    if (s.component === 'SearchInput' && /State=(Disabled|ReadOnly|Error)/.test(variant)) return false;
    if (s.component === 'DateInput' && variant.includes('Density=form')) return false;
    if (s.component === 'TypePickerField' && /State=(Scan|Error)/.test(variant)) return false;
    if (s.component === 'CircularProgress' && s.props.size === 32) return false;
    if (s.component === 'SplitButton' && s.props.variant === 'text') return false;
    if (s.component === 'SourceOriginIcon' && variant.startsWith('Scope=')) return false;
    if (s.component === 'Checkbox' && variant.includes('State=Filled')) return false;
    if (s.component === 'SideSheet' && !variant.startsWith('Variant=')) return false;
    if (s.component === 'ListItem' && variant === 'State=Disabled') return false;
    if (s.component === 'TextAreaField' && !variant.includes('Density=form')) return false;
    if (s.component === 'PlainField' && variant === 'State=Open') return false;
    return true;
  });
  for (const s of output) {
    if (s.component === 'TooltipLayerExample' || s.component === 'ErrorBoundaryExample') {
      s.renderAs = s.component;
      s.component = s.component.replace('Example', '');
    }
    if (s.component === 'NumberInput') s.props.value = String(s.props.value);
    if (['DateField', 'DateInput', 'DateRange'].includes(s.component)) s.props.precision ??= 'day';
    if (['PlainField', 'SuggestField', 'SuggestBox', 'FieldErrorMark'].includes(s.component)) s.props.density ??= 'form';
    if (s.component === 'ModalPrimary') s.props.variant ??= 'filled';
    if (['ModalExit', 'ModalAction'].includes(s.component)) s.props.variant ??= 'text';
    if (s.component === 'ScopeIcon') s.props.size ??= 18;
    if (s.component === 'IconButton' && !s.variant.includes('State=Selected')) delete s.props.selected;
    if (s.component === 'SuggestField') delete s.props.source;
    if (s.component === 'Checkbox' && s.variant.includes('ReadOnly')) {
      delete s.props.readOnly; s.props['aria-readonly'] = true;
    }
    if (s.component === 'Card') {
      if (/State=(Static|Interactive|Hover|Focus)/.test(s.variant)) {
        delete s.props.dragged; delete s.props.dropTarget; delete s.props.invalid;
      }
    }
    if (s.component === 'DateRange') delete s.props.value;
    if (['PlainField', 'SuggestBox'].includes(s.component)) delete s.props.source;
    if (s.component === 'Select') delete s.props.error;
    if (s.component === 'MoveButtons') { delete s.props.canUp; delete s.props.canDown; }
    if (s.component === 'WindowStep') delete s.props.host;
    const only: Record<string, string[]> = {
      ArchivedMark: ['words', 'className'], EmptyState: ['icon', 'title', 'description', 'action', 'className'],
      FieldErrorMark: ['density', 'message'], FieldScanMark: ['open'], ValueIssueHint: ['messages', 'compact'], ValueIssueCount: ['count', 'className'],
      BugReportDialog: ['open', 'prefill', 'onClose'], ChangePasswordModal: ['open', 'onClose'],
      LeaveGuardDialog: ['open', 'saving', 'message', 'saveBlocked', 'onSave', 'onDiscard', 'onCancel'],
      LeaveQuestion: ['open', 'onStay', 'onLeave'], ConfirmDialog: ['open', 'onOpenChange', 'title', 'description', 'confirmLabel', 'confirmDanger', 'cancelLabel', 'requireCheckbox', 'errorTitle', 'blocked', 'onConfirm', 'onRefused'],
      SendMessageDialog: ['open', 'onClose', 'candidates', 'title', 'presetSubject', 'presetBody'], ShortcutsHelp: ['open', 'onOpenChange'],
    };
    if (only[s.component]) for (const key of Object.keys(s.props)) if (!only[s.component].includes(key)) delete s.props[key];
    const remove: Record<string, string[]> = {
      DateField: ['error'], DateInput: ['error', 'hint'], ModalExit: ['onClick'], NumberInput: ['readOnly', 'required'],
      PlainField: ['feed', 'options'], RichTooltip: ['content', 'open'], ScopeIcon: ['source'], SearchInput: ['density', 'disabled', 'readOnly', 'required'],
      SectionPanelDrawer: ['label'], SuggestBox: ['options'], SuggestField: ['feed'], TextAreaField: ['density'], TimeField: ['error', 'readOnly'],
      TimeInput: ['density', 'error', 'hint'], Tooltip: ['text', 'title'],
    };
    for (const key of remove[s.component] ?? []) delete s.props[key];
    if (['TextAreaField', 'TimeInput', 'SearchInput'].includes(s.component)) s.variant = s.variant.replace(/^Density=\w+, /, '');
    if (s.component === 'OutlinedField') s.state = s.variant.endsWith('-auto') ? 'Empty' : s.state;
    s.id = storyId(s.component, s.variant);
  }
  return [...new Map(output.map(story => [story.id, story])).values()];
}
