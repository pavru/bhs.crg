package ru.bimhouse.crg.field.entry

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp

private val phases = listOf("Работа", "Параметры", "Бригада и заметка")

@OptIn(ExperimentalLayoutApi::class)
@Composable
internal fun LineWizard(local: LocalReport, profile: FieldSession, model: EntryModel, editor: LineEditor) {
    val site = profile.site(local.report.constructionId)
    val kind = profile.work(editor.line.workTypeId)
    TextButton(onClick = model::backFromLine, enabled = !model.saving) {
        Icon(Icons.AutoMirrored.Filled.ArrowBack, null)
        Text(
            if (editor.step == WorkStep.PICK || editor.existing && editor.step == WorkStep.DETAILS) "К отчёту" else "Назад",
            Modifier.padding(start = 8.dp),
        )
    }
    EntryHeading(if (editor.step == WorkStep.PICK) "Выберите работу" else kind?.name ?: "Своя работа")
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        phases.forEachIndexed { index, label ->
            val active = index == editor.step.ordinal
            Surface(
                shape = MaterialTheme.shapes.large,
                color = if (active) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerLow,
                modifier = Modifier.semantics { selected = active },
            ) { Text(label, Modifier.padding(horizontal = 8.dp, vertical = 6.dp), style = MaterialTheme.typography.labelMedium) }
        }
    }
    if (editor.errors.isNotEmpty()) EntryError("Заполните поля: ${editor.errors.joinToString()}.")
    model.message?.let { EntryError(it) }
    when (editor.step) {
        WorkStep.PICK -> WorkPicker(model, profile, site, editor)
        WorkStep.DETAILS -> {
            LineDetails(editor.line, profile, site, kind, editor.errors, model::changeLine)
            Button(onClick = model::nextLine, modifier = Modifier.fillMaxWidth().testTag("line-next")) { Text("Далее") }
        }
        WorkStep.EXTRAS -> {
            LineExtras(editor.line, site, model::changeLine)
            Button(onClick = model::nextLine, enabled = !model.saving, modifier = Modifier.fillMaxWidth().testTag("line-save")) {
                Text("Сохранить работу")
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun WorkPicker(model: EntryModel, profile: FieldSession, site: EntrySite?, editor: LineEditor) {
    OutlinedTextField(
        value = editor.query, onValueChange = model::search, label = { Text("Поиск работы") },
        modifier = Modifier.fillMaxWidth().testTag("work-search"), singleLine = true,
    )
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        mapOf(WorkSource.RECENT to "Недавние", WorkSource.SITE to "На стройке", WorkSource.ALL to "Все работы")
            .filterKeys { it != WorkSource.SITE || site != null }
            .forEach { (source, label) ->
                FilterChip(selected = editor.source == source, onClick = { model.source(source) }, label = { Text(label) })
            }
    }
    val recent = model.reports.asSequence().flatMap { it.lines.asSequence() }.mapNotNull { it.workTypeId }
        .distinct().mapNotNull(profile::work).take(4).toList()
    val pool = when {
        editor.source == WorkSource.SITE -> profile.works.filter { it.id in site?.works.orEmpty() }
        editor.source == WorkSource.RECENT && editor.query.isBlank() && recent.isNotEmpty() -> recent
        editor.source == WorkSource.RECENT && editor.query.isBlank() -> profile.works.take(4)
        else -> profile.works
    }
    val matches = pool.filter { it.name.contains(editor.query.trim(), ignoreCase = true) }
    if (matches.isEmpty()) Text("Работы не найдены. Измените запрос или укажите свою работу.")
    matches.forEach { work ->
        TextButton(onClick = { model.chooseWork(work) }, modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp)) {
            Text(work.name, Modifier.weight(1f), color = MaterialTheme.colorScheme.onSurface)
            profile.unit(work.unitId)?.let { Text(it) }
        }
    }
    OutlinedButton(onClick = { model.chooseWork(null) }) { Text("Своя работа") }
}

@Composable
private fun LineDetails(
    line: WorkLine, profile: FieldSession, site: EntrySite?, work: EntryWork?,
    errors: List<String>, onChange: (WorkLine) -> Unit,
) {
    if (line.workTypeId == null) OutlinedTextField(
        line.customName.orEmpty(), { onChange(line.copy(customName = it)) }, label = { Text("Название работы") },
        isError = "Название работы" in errors, modifier = Modifier.fillMaxWidth().testTag("custom-work-name"),
    )
    if (work != null && work.variants.isNotEmpty()) {
        EntrySelect("Вариант вида работы", line.variantId, work.variants, { onChange(line.copy(variantId = it)) }, "line-variant")
    }
    if (site != null && site.sections.isNotEmpty()) {
        EntrySelect("Раздел стройки", line.sectionId, site.sections, { onChange(line.copy(sectionId = it)) }, "line-section")
    }
    if (work == null) {
        EntrySelect("Единица", line.unitId, profile.units, { onChange(line.copy(unitId = it)) }, "line-unit")
    } else {
        Text("Единица · ${profile.unit(line.unitId.orEmpty()).orEmpty()}")
    }
    OutlinedTextField(
        line.volume.orEmpty(), { onChange(line.copy(volume = it)) }, label = { Text("Объём") },
        isError = "Объём" in errors, singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
        modifier = Modifier.fillMaxWidth().testTag("line-volume"),
    )
    if (work?.requiresPlace == true || !line.location.isNullOrBlank()) {
        OutlinedTextField(
            line.location.orEmpty(), { onChange(line.copy(location = it)) }, label = { Text("Место") },
            placeholder = { Text("Этаж, помещение или оси") },
            isError = "Место" in errors, modifier = Modifier.fillMaxWidth().testTag("line-place"),
        )
    }
    if (work?.requiresMaterial == true || line.materials.isNotEmpty()) {
        val materials = line.materials.ifEmpty { listOf(WorkMaterial(null, null)) }
        materials.forEachIndexed { index, material ->
            OutlinedTextField(
                material.note.orEmpty(), { text ->
                    onChange(line.copy(materials = materials.mapIndexed { n, item ->
                        if (index == n) item.copy(note = text) else item
                    }))
                }, label = { Text(if (index == 0) "Материалы" else "Материал ${index + 1}") },
                isError = "Материалы" in errors && index == 0, modifier = Modifier.fillMaxWidth().testTag("line-material-$index"),
            )
        }
        if (materials.any { it.nomenclatureId != null || !it.note.isNullOrBlank() }) {
            TextButton(onClick = { onChange(line.copy(materials = materials + WorkMaterial(null, null))) }) {
                Text("Добавить материал")
            }
        }
    }
    if (work?.hasWebFields == true) Text("Поля заказчика заполняются в вебе", color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun LineExtras(line: WorkLine, site: EntrySite?, onChange: (WorkLine) -> Unit) {
    var crewOpen by rememberSaveable(line.id) { mutableStateOf(line.participants.isNotEmpty()) }
    if (site != null && site.crew.isNotEmpty()) {
        OutlinedButton(onClick = { crewOpen = !crewOpen }) {
            Text(if (line.participants.isEmpty()) "Указать бригаду" else "Бригада · ${line.participants.size}")
        }
        if (crewOpen) site.crew.forEach { member ->
            val checked = member.id in line.participants
            Row(
                Modifier.fillMaxWidth().heightIn(min = 48.dp).toggleable(checked, role = Role.Checkbox, onValueChange = { on ->
                    val next = if (on) line.participants + member.id else line.participants - member.id
                    onChange(line.copy(participants = next, isCrew = next.isNotEmpty()))
                }),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) { Checkbox(checked, onCheckedChange = null); Text(member.name) }
        }
    }
    OutlinedTextField(line.comment.orEmpty(), { onChange(line.copy(comment = it)) },
        label = { Text("Комментарий") }, modifier = Modifier.fillMaxWidth().testTag("line-comment"))
}

@Composable
internal fun EntrySelect(
    label: String, selected: String?, options: List<EntryChoice>, onSelect: (String) -> Unit,
    tag: String = "select-$label",
) {
    var open by rememberSaveable { mutableStateOf(false) }
    Column {
        OutlinedButton(
            onClick = { open = true },
            modifier = Modifier.fillMaxWidth().heightIn(min = 48.dp).testTag(tag),
        ) {
            val choice = options.find { it.id == selected }?.name
            Text(if (choice == null) label else "$label · $choice", Modifier.weight(1f))
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            options.forEach { item ->
                DropdownMenuItem(text = { Text(item.name) }, onClick = {
                    onSelect(item.id)
                    open = false
                })
            }
        }
    }
}
