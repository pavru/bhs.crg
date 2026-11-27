package ru.bimhouse.crg.field.entry

import androidx.room.Embedded
import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.PrimaryKey
import kotlinx.serialization.Serializable
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID

data class ReportPlacement(
    val companyZone: ZoneId,
    val constructionId: UUID? = null,
    val siteZone: ZoneId? = null,
    val unlistedSiteName: String? = null,
) {
    init {
        require(constructionId != null || siteZone == null)
        require(constructionId == null || unlistedSiteName == null)
        require(companyZone.id in ZoneId.getAvailableZoneIds())
        require(siteZone == null || siteZone.id in ZoneId.getAvailableZoneIds())
    }
}

data class ReportInput(
    val placement: ReportPlacement,
    val date: LocalDate,
    val shift: Shift = Shift.DAY,
    val hours: String? = null,
    val note: String? = null,
    val lines: List<WorkLine> = emptyList(),
)

@Serializable
data class WorkMaterial(val nomenclatureId: String?, val note: String?)

@Serializable
data class WorkLine(
    val id: String,
    val workTypeId: String? = null,
    val variantId: String? = null,
    val volume: String? = null,
    val unitId: String? = null,
    val sectionId: String? = null,
    val location: String? = null,
    val comment: String? = null,
    val isCrew: Boolean = false,
    val crewRole: String? = null,
    val materials: List<WorkMaterial> = emptyList(),
    val participants: List<String> = emptyList(),
)

@Serializable
@Entity(
    tableName = "reports",
    indices = [Index(value = ["constructionId", "date", "shift"], unique = true)],
)
data class ReportRecord(
    @PrimaryKey val id: String,
    val constructionId: String?,
    val unlistedSiteName: String?,
    val companyZoneId: String,
    val siteZoneId: String?,
    val windowZoneId: String,
    val date: String,
    val shift: Shift,
    val windowEndsAt: String,
    val hours: String?,
    val note: String?,
    val status: ReportStatus,
    val submittedAt: String?,
    val revision: Long,
)

@Entity(
    tableName = "report_lines",
    primaryKeys = ["reportId", "id"],
    foreignKeys = [
        ForeignKey(
            entity = ReportRecord::class,
            parentColumns = ["id"],
            childColumns = ["reportId"],
            onDelete = ForeignKey.CASCADE,
        ),
    ],
)
internal data class ReportLineRecord(
    val reportId: String,
    val position: Int,
    @Embedded val value: WorkLine,
)

@Serializable
data class LocalReport(val report: ReportRecord, val lines: List<WorkLine>)
