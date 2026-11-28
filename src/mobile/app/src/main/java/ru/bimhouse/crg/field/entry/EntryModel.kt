package ru.bimhouse.crg.field.entry

import android.app.Application
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.math.BigDecimal
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID

internal enum class EntryPage { REPORTS, CONNECTION, PROFILE }
internal enum class WorkStep { PICK, DETAILS, EXTRAS }
internal enum class WorkSource { RECENT, SITE, ALL }

internal data class LineEditor(
    val line: WorkLine,
    val existing: Boolean = false,
    val step: WorkStep = WorkStep.PICK,
    val query: String = "",
    val source: WorkSource = WorkSource.RECENT,
    val errors: List<String> = emptyList(),
)

internal class EntryModel(application: Application) : AndroidViewModel(application) {
    var session by mutableStateOf<FieldSession?>(null)
        private set
    var loading by mutableStateOf(true)
        private set
    var reports by mutableStateOf(emptyList<LocalReport>())
        private set
    var pendingIds by mutableStateOf(emptySet<String>())
        private set
    var selected by mutableStateOf<String?>(null)
        private set
    var showReport by mutableStateOf(false)
        private set
    var page by mutableStateOf(EntryPage.REPORTS)
        private set
    var editor by mutableStateOf<LineEditor?>(null)
        private set
    var creating by mutableStateOf(false)
        private set
    var message by mutableStateOf<String?>(null)
        private set
    var saving by mutableStateOf(false)
        private set
    var hoursText by mutableStateOf("")
        private set

    private var store: OfflineReportStore? = null
    private val writes = Mutex()
    val current: LocalReport? get() = reports.find { it.report.id == selected }
    fun night(now: Instant): LocalReport? = reports.firstOrNull {
        val report = it.report
        val today = now.atZone(ZoneId.of(report.siteZoneId ?: report.companyZoneId)).toLocalDate()
        report.shift == Shift.NIGHT && report.date == today.minusDays(1).toString() &&
            report.status.keyEditable && now < Instant.parse(report.windowEndsAt)
    }

    init {
        viewModelScope.launch {
            try {
                val profile = EntryBootstrap.load(application)
                if (profile != null) {
                    val opened = OfflineReportStore.open(application, profile.owner)
                    store = opened
                    refresh(opened)
                    reports.firstOrNull()?.let {
                        selected = it.report.id
                        hoursText = it.report.hours.orEmpty()
                    }
                    session = profile
                }
            } catch (error: CancellationException) {
                throw error
            } catch (_: Exception) {
                message = "Не удалось открыть данные на телефоне."
            } finally {
                loading = false
            }
        }
    }

    fun openNight() {
        night(Instant.now())?.let { open(it.report.id) }
    }

    fun navigate(destination: EntryPage) {
        page = destination
        if (destination == EntryPage.REPORTS) showReport = false
    }

    fun open(id: String) {
        if (selected != id) {
            hoursText = reports.find { it.report.id == id }?.report?.hours.orEmpty()
            editor = null
        }
        selected = id
        page = EntryPage.REPORTS
        showReport = true
        message = null
    }

    fun showList() { showReport = false }
    fun startReport(open: Boolean) { creating = open }

    fun create(site: EntrySite?, name: String) {
        val profile = session ?: return
        val placement = ReportPlacement(profile.companyZone, site?.id, site?.zone, if (site == null) name.trim() else null)
        val date = Instant.now().atZone(site?.zone ?: profile.companyZone).toLocalDate()
        runWrite {
            val existing = reports.firstOrNull {
                it.report.constructionId == site?.id?.toString() &&
                    it.report.unlistedSiteName == placement.unlistedSiteName &&
                    it.report.date == date.toString() && it.report.shift == Shift.DAY
            }
            val saved = existing ?: create(UUID.randomUUID(), ReportInput(placement, date), action())
            refresh(this)
            creating = false
            open(saved.report.id)
        }
    }

    fun changeKey(date: LocalDate, shift: Shift) {
        val id = selected ?: return
        runWrite { rewrite(id) { it.copy(date = date, shift = shift) } }
    }

    fun changeHours(value: String) {
        val id = selected ?: return
        hoursText = value
        val amount = value.decimalOrNull()
        if (value.isNotBlank() && (amount == null || amount !in BigDecimal.ZERO..BigDecimal(24))) return
        runWrite(quiet = true) {
            rewrite(id) { it.copy(hours = value.trim().ifEmpty { null }) }
        }
    }

    fun beginLine(row: WorkLine? = null) {
        showReport = true
        editor = if (row == null) LineEditor(WorkLine(id = UUID.randomUUID().toString()))
        else LineEditor(row, existing = true, step = WorkStep.DETAILS)
    }

    fun search(query: String) { editor = editor?.copy(query = query) }
    fun source(source: WorkSource) { editor = editor?.copy(source = source) }

    fun chooseWork(kind: EntryWork?) {
        val active = editor ?: return
        editor = active.copy(
            line = if (kind?.id == active.line.workTypeId) active.line
            else WorkLine(id = active.line.id, workTypeId = kind?.id, unitId = kind?.unitId),
            step = WorkStep.DETAILS,
            errors = emptyList(),
        )
    }

    fun changeLine(line: WorkLine) { editor = editor?.copy(line = line, errors = emptyList()) }

    fun backFromLine() {
        val active = editor ?: return
        editor = when {
            active.step == WorkStep.EXTRAS -> active.copy(step = WorkStep.DETAILS)
            active.step == WorkStep.DETAILS && !active.existing -> active.copy(step = WorkStep.PICK)
            else -> null
        }
    }

    private fun lineErrors(line: WorkLine, local: LocalReport): List<String> = buildList {
        val profile = session ?: return@buildList
        addAll(profile.missing(line))
        if (profile.site(local.report.constructionId)?.sections?.isNotEmpty() == true && line.sectionId == null) {
            add("Раздел стройки")
        }
    }

    fun nextLine() {
        val active = editor ?: return
        val local = current ?: return
        val missing = lineErrors(active.line, local)
        if (missing.isNotEmpty()) {
            editor = active.copy(errors = missing)
            return
        }
        if (active.step == WorkStep.DETAILS) {
            editor = active.copy(step = WorkStep.EXTRAS)
            return
        }
        val line = active.line.copy(
            customName = active.line.customName?.trim(),
            materials = active.line.materials.filter { it.nomenclatureId != null || !it.note.isNullOrBlank() },
        )
        runWrite {
            rewrite(local.report.id) { input ->
                val lines = if (active.existing) input.lines.map { if (it.id == line.id) line else it }
                else input.lines + line
                input.copy(lines = lines)
            }
            editor = null
        }
    }

    fun submit() {
        val report = current ?: return
        val enteredHours = hoursText.trim()
        val hours = enteredHours.decimalOrNull()
        if (hours == null || hours !in BigDecimal.ZERO..BigDecimal(24)) {
            message = "Укажите часы смены от 0 до 24."
            return
        }
        if (report.lines.isEmpty()) {
            message = "Добавьте хотя бы одну работу."
            return
        }
        val incomplete = report.lines.firstOrNull { lineErrors(it, report).isNotEmpty() }
        if (incomplete != null) {
            message = "Проверьте работу «${session?.lineName(incomplete)}»: ${lineErrors(incomplete, report).joinToString()}."
            return
        }
        runWrite {
            val saved = rewrite(report.report.id) { it.copy(hours = enteredHours) }
            submit(UUID.fromString(saved.report.id), saved.report.revision, action())
        }
    }

    private fun runWrite(quiet: Boolean = false, block: suspend OfflineReportStore.() -> Unit) {
        val openStore = store ?: return
        if (!quiet && saving) return
        if (!quiet) saving = true
        viewModelScope.launch {
            writes.withLock {
                try {
                    if (!quiet) message = null
                    openStore.block()
                    refresh(openStore)
                } catch (error: CancellationException) {
                    throw error
                } catch (error: IllegalStateException) {
                    message = error.message ?: "Не удалось сохранить отчёт."
                } catch (_: Exception) {
                    message = "Не удалось сохранить отчёт на телефоне. Повторите действие."
                } finally {
                    if (!quiet) saving = false
                }
            }
        }
    }

    private suspend fun OfflineReportStore.rewrite(id: String, transform: (ReportInput) -> ReportInput): LocalReport {
        val uuid = UUID.fromString(id)
        val current = requireNotNull(report(uuid))
        val record = current.report
        val placement = ReportPlacement(
            ZoneId.of(record.companyZoneId),
            record.constructionId?.let(UUID::fromString),
            record.siteZoneId?.let(ZoneId::of), record.unlistedSiteName,
        )
        val old = ReportInput(placement, LocalDate.parse(record.date), record.shift, record.hours, record.note, current.lines)
        val next = transform(old)
        return if (old == next) current else edit(uuid, record.revision, next, action())
    }

    private suspend fun refresh(currentStore: OfflineReportStore) {
        reports = currentStore.reports()
        pendingIds = currentStore.pendingActions().mapTo(mutableSetOf()) { it.reportId }
    }

    private fun action() = LocalAction(UUID.randomUUID(), Instant.now())

    override fun onCleared() { store?.close() }
}
