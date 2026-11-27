package ru.bimhouse.crg.field.entry

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZoneOffset

@Composable
internal fun ReportPanel(local: LocalReport, model: EntryModel, profile: FieldSession, split: Boolean) {
    val report = local.report
    val date = LocalDate.parse(report.date)
    val site = profile.site(report.constructionId)
    val editable = report.status.keyEditable
    val linesEditable = report.status != ReportStatus.ACCEPTED
    val sections = local.lines.mapNotNull { line -> site?.sections?.find { it.id == line.sectionId }?.name }.distinct()
    val editor = model.editor
    var keyOptions by rememberSaveable(report.id) { mutableStateOf(false) }
    var selectingDate by rememberSaveable(report.id) { mutableStateOf(false) }
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (!split && editor == null) TextButton(onClick = model::showList) {
            Icon(Icons.AutoMirrored.Filled.ArrowBack, null)
            Text("Мои отчёты", Modifier.padding(start = 8.dp))
        }
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (editable && editor == null) TextButton(onClick = { keyOptions = !keyOptions }, modifier = Modifier.weight(1f)) {
                Text(shiftTitle(report))
            } else Text(shiftTitle(report), Modifier.weight(1f))
            DeliveryIcon(report.id in model.pendingIds)
            LifeStatus(report.status)
        }
        EntryHeading(profile.siteName(report))
        if (sections.isNotEmpty()) Text(sections.joinToString(), color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (report.constructionId == null) Text("Объекта нет в списке", color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (editable && keyOptions && editor == null) {
            OutlinedButton(onClick = { selectingDate = true }, enabled = !model.saving) { Text("Дата начала смены") }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Shift.entries.forEach { shift ->
                    val label = if (shift == Shift.DAY) "День" else "Ночь"
                    if (shift == report.shift) FilledTonalButton(onClick = {}, enabled = false) { Text(label) }
                    else OutlinedButton(onClick = { model.changeKey(date, shift) }, enabled = !model.saving) { Text(label) }
                }
            }
        }
        HorizontalDivider()
        ReturnComment(report)
        if (editor != null) {
            LineWizard(local, profile, model, editor)
        } else {
            Text("Работы", style = MaterialTheme.typography.titleMedium)
            if (local.lines.isEmpty()) Text("Добавьте первую работу смены.")
            local.lines.forEach { line -> SavedLine(line, site, profile, linesEditable) { model.beginLine(line) } }
            if (editable) {
                OutlinedButton(onClick = { model.beginLine() }) { Text("Выбрать работу") }
                OutlinedTextField(
                    value = model.hoursText, onValueChange = model::changeHours, enabled = !model.saving,
                    label = { Text("Часы смены") },
                    singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth().testTag("report-hours"),
                )
            } else {
                report.hours?.let { Text("$it ч за смену") }
                if (report.status == ReportStatus.SUBMITTED && report.id in model.pendingIds) {
                    Text("Отчёт ожидает доставки.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            model.message?.let { EntryError(it) }
            if (editable) Button(onClick = model::submit, enabled = !model.saving, modifier = Modifier.fillMaxWidth().testTag("submit-report")) {
                Text(if (report.status == ReportStatus.RETURNED) "Подать снова" else "Подать отчёт")
            }
        }
    }
    if (selectingDate) DateChoice(date, onClose = { selectingDate = false }) {
        model.changeKey(it, report.shift)
        selectingDate = false
    }
}

@Composable
private fun ReturnComment(report: ReportRecord) {
    val note = report.returnNote ?: return
    val active = report.status == ReportStatus.RETURNED
    var expanded by rememberSaveable(report.id, active) { mutableStateOf(false) }
    Surface(
        shape = MaterialTheme.shapes.medium,
        color = if (active) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.surfaceContainerLow,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
            Text(
                if (active) "Комментарий к возврату"
                else report.returnedAt?.let {
                    val date = Instant.parse(it).atZone(ZoneId.of(report.windowZoneId)).toLocalDate()
                    "Был возврат ${shortDate(date.toString())}"
                } ?: "Был возврат",
                style = MaterialTheme.typography.labelLarge,
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(note, Modifier.weight(1f), maxLines = if (expanded) Int.MAX_VALUE else 1, overflow = TextOverflow.Ellipsis)
                TextButton(onClick = { expanded = !expanded }, modifier = Modifier.testTag("return-comment-toggle")) {
                    Text(if (expanded) "Свернуть" else "Ещё")
                }
            }
        }
    }
}

@Composable
private fun SavedLine(line: WorkLine, site: EntrySite?, profile: FieldSession, editable: Boolean, onEdit: () -> Unit) {
    val kind = profile.work(line.workTypeId)
    val variant = kind?.variants?.find { it.id == line.variantId }?.name
    val section = site?.sections?.find { it.id == line.sectionId }?.name
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(profile.lineName(line), Modifier.weight(1f), style = MaterialTheme.typography.titleSmall)
            Text(listOfNotNull(line.volume, profile.unit(line.unitId)).joinToString(" "))
        }
        val details = listOfNotNull(section, variant, line.location).filter(String::isNotBlank)
        if (details.isNotEmpty()) Text(details.joinToString(" · "), color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (kind?.hasWebFields == true) Text("Есть поля для веба", color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (editable) TextButton(onClick = onEdit, modifier = Modifier.testTag("edit-line-${line.id}")) {
            Text("Изменить · ${profile.lineName(line)}")
        }
        HorizontalDivider()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun DateChoice(date: LocalDate, onClose: () -> Unit, onSelect: (LocalDate) -> Unit) {
    val datePicker = rememberDatePickerState(initialSelectedDateMillis = date.atStartOfDay().toInstant(ZoneOffset.UTC).toEpochMilli())
    DatePickerDialog(
        onDismissRequest = onClose,
        confirmButton = {
            TextButton(onClick = {
                datePicker.selectedDateMillis?.let { onSelect(Instant.ofEpochMilli(it).atOffset(ZoneOffset.UTC).toLocalDate()) }
            }, enabled = datePicker.selectedDateMillis != null) { Text("Готово") }
        },
        dismissButton = { TextButton(onClick = onClose) { Text("Отмена") } },
    ) { DatePicker(datePicker, showModeToggle = false) }
}

@Composable
internal fun EntryError(message: String) {
    Text(message, Modifier.semantics { liveRegion = LiveRegionMode.Polite }, color = MaterialTheme.colorScheme.error)
}
