package ru.bimhouse.crg.field.entry

import android.content.res.Configuration
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.DateRange
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Person
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.heading
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.delay
import java.time.Instant
import java.time.LocalDate
import java.time.format.DateTimeFormatter

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun FieldEntry(model: EntryModel, online: Boolean) {
    val configuration = LocalConfiguration.current
    val split = configuration.smallestScreenWidthDp >= 600 ||
        configuration.orientation == Configuration.ORIENTATION_LANDSCAPE
    val profile = model.session
    BackHandler(enabled = model.editor != null || model.showReport || model.page != EntryPage.REPORTS) {
        when {
            model.editor != null -> model.backFromLine()
            model.page != EntryPage.REPORTS -> model.navigate(EntryPage.REPORTS)
            else -> model.showList()
        }
    }
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(profile?.productName ?: "BHS.CRG") },
                actions = {
                    Text(
                        if (online) "В сети" else "Нет связи",
                        Modifier.padding(horizontal = 16.dp).testTag("connection-state"),
                        style = MaterialTheme.typography.labelLarge,
                    )
                },
            )
        },
        bottomBar = {
            if (profile != null) NavigationBar(windowInsets = WindowInsets.navigationBars) {
                NavigationBarItem(
                    selected = model.page == EntryPage.REPORTS,
                    onClick = { model.navigate(EntryPage.REPORTS) },
                    icon = { Icon(Icons.AutoMirrored.Filled.List, null) }, label = { Text("Отчёты") },
                )
                NavigationBarItem(
                    selected = model.page == EntryPage.CONNECTION,
                    onClick = { model.navigate(EntryPage.CONNECTION) },
                    icon = { Icon(Icons.Filled.Info, null) }, label = { Text("Связь") },
                )
                NavigationBarItem(
                    selected = model.page == EntryPage.PROFILE,
                    onClick = { model.navigate(EntryPage.PROFILE) },
                    icon = { Icon(Icons.Filled.Person, null) }, label = { Text("Профиль") },
                )
            }
        },
    ) { insets ->
        Box(Modifier.fillMaxSize().padding(insets)) {
            when {
                model.loading -> Text("Открываем сохранённые данные…", Modifier.padding(20.dp))
                profile == null -> Text(model.message ?: "Подключите устройство", Modifier.padding(20.dp))
                model.page == EntryPage.CONNECTION -> ConnectionPage(model, profile)
                model.page == EntryPage.PROFILE -> Column(
                    Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    EntryHeading("Профиль")
                    Text(profile.employeeName)
                    Text("Монтажник", color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                split -> Row(Modifier.fillMaxSize()) {
                    ReportList(model, profile, Modifier.weight(0.36f))
                    Box(Modifier.weight(0.64f).testTag("report-panel")) {
                        model.current?.let { ReportPanel(it, model, profile, split = true) }
                            ?: Text("Откройте отчёт", Modifier.padding(20.dp))
                    }
                }
                model.showReport -> model.current?.let { ReportPanel(it, model, profile, split = false) }
                else -> ReportList(model, profile, Modifier.fillMaxSize())
            }
        }
    }
    if (profile != null && model.creating) CreateReport(model, profile)
}

@Composable
private fun ReportList(model: EntryModel, profile: FieldSession, modifier: Modifier) {
    var now by rememberSaveable { mutableStateOf(Instant.now().epochSecond) }
    LaunchedEffect(Unit) {
        while (true) {
            now = Instant.now().epochSecond
            delay(15_000)
        }
    }
    Column(
        modifier = modifier.background(MaterialTheme.colorScheme.surfaceContainerLow)
            .verticalScroll(rememberScrollState()).padding(16.dp).testTag("report-list"),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        EntryHeading("Мои отчёты")
        model.night(Instant.ofEpochSecond(now))?.let { local ->
            OutlinedButton(onClick = model::openNight, modifier = Modifier.fillMaxWidth().testTag("continue-night")) {
                Text("Ночная смена ${shortDate(local.report.date)}")
            }
        }
        OutlinedButton(onClick = { model.startReport(true) }, modifier = Modifier.fillMaxWidth()) { Text("Новый отчёт") }
        val returned = model.reports.filter { it.report.status == ReportStatus.RETURNED }
        val others = model.reports.filter { it.report.status != ReportStatus.RETURNED }
        if (returned.isNotEmpty()) {
            Text("На доработке", style = MaterialTheme.typography.labelLarge)
            returned.forEach { local -> ReportListItem(local, model, profile) }
        }
        if (others.isNotEmpty()) {
            Text("Другие отчёты", style = MaterialTheme.typography.labelLarge)
            others.forEach { local -> ReportListItem(local, model, profile) }
        }
    }
}

@Composable
private fun ReportListItem(local: LocalReport, model: EntryModel, profile: FieldSession) {
    val report = local.report
    TextButton(
        onClick = { model.open(report.id) },
        modifier = Modifier.fillMaxWidth().testTag("report-row-${report.id}"),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            DeliveryIcon(report.id in model.pendingIds)
            Column(Modifier.weight(1f)) {
                Text(profile.siteName(report), color = MaterialTheme.colorScheme.onSurface)
                Text(shiftTitle(report), color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.labelMedium)
            }
            LifeStatus(report.status)
        }
    }
}

@Composable
private fun ConnectionPage(model: EntryModel, profile: FieldSession) {
    val queue = model.reports.filter { it.report.id in model.pendingIds && it.report.status == ReportStatus.SUBMITTED }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        EntryHeading("Связь")
        Text("Ожидают доставки · ${queue.size}")
        queue.forEach { ReportListItem(it, model, profile) }
    }
}

@Composable
private fun CreateReport(model: EntryModel, profile: FieldSession) {
    var choice by rememberSaveable { mutableStateOf<String?>(null) }
    var name by rememberSaveable { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = { if (!model.saving) model.startReport(false) },
        title = { Text("Новый отчёт") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                EntrySelect(
                    "Стройка", choice,
                    profile.sites.map { EntryChoice(it.id.toString(), it.name) } + EntryChoice("unlisted", "Объекта нет в списке"),
                    { choice = it },
                )
                if (choice == "unlisted") OutlinedTextField(
                    value = name, onValueChange = { name = it }, label = { Text("Название объекта") },
                    singleLine = true, modifier = Modifier.fillMaxWidth().testTag("unlisted-name"),
                )
                model.message?.let { EntryError(it) }
            }
        },
        confirmButton = {
            Button(
                onClick = { model.create(profile.site(choice), name) },
                enabled = !model.saving && (profile.site(choice) != null || choice == "unlisted" && name.isNotBlank()),
            ) { Text("Открыть отчёт") }
        },
        dismissButton = { TextButton(onClick = { model.startReport(false) }, enabled = !model.saving) { Text("Отмена") } },
    )
}

@Composable
internal fun EntryHeading(text: String) {
    Text(text, Modifier.semantics { heading() }, style = MaterialTheme.typography.headlineSmall)
}

@Composable
internal fun DeliveryIcon(pending: Boolean) {
    Icon(
        if (pending) Icons.Filled.DateRange else Icons.Filled.Check,
        contentDescription = if (pending) "На телефоне" else "Доставлен",
        modifier = Modifier.testTag("delivery-status"),
        tint = if (pending) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.primary,
    )
}

@Composable
internal fun LifeStatus(status: ReportStatus) {
    val label = when (status) {
        ReportStatus.DRAFT -> "Черновик"
        ReportStatus.RETURNED -> "Возвращён"
        ReportStatus.SUBMITTED -> "Подан"
        ReportStatus.ACCEPTED -> "Принят"
    }
    Text(label, Modifier.testTag("life-status"), style = MaterialTheme.typography.labelMedium,
        color = if (status == ReportStatus.RETURNED) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.primary)
}

private val dayPattern = DateTimeFormatter.ofPattern("dd.MM")

internal fun shortDate(date: String): String = LocalDate.parse(date).format(dayPattern)
internal fun shiftTitle(report: ReportRecord): String =
    shortDate(report.date) + if (report.shift == Shift.NIGHT) " · Ночь" else " · День"
