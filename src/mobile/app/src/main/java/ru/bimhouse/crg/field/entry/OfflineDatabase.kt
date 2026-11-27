package ru.bimhouse.crg.field.entry

import android.content.Context
import androidx.room.AutoMigration
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.ForeignKey
import androidx.room.Index
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.TypeConverter
import androidx.room.TypeConverters
import androidx.room.Update
import kotlinx.serialization.json.Json
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID

enum class ReportAction { CREATE, EDIT, SUBMIT }

@Entity(
    tableName = "report_outbox",
    indices = [Index("actionId", unique = true), Index("reportId")],
    foreignKeys = [
        ForeignKey(
            entity = ReportRecord::class,
            parentColumns = ["id"],
            childColumns = ["reportId"],
        ),
    ],
)
data class PendingReportAction(
    @PrimaryKey(autoGenerate = true) val sequence: Long = 0,
    val actionId: String,
    val reportId: String,
    val action: ReportAction,
    val deviceTime: String,
    // Снимок действия сохраняется, даже когда сам отчёт уже отредактирован или привязан.
    val snapshotJson: String,
) {
    fun snapshot(): LocalReport = Json.decodeFromString(snapshotJson)
}

@Dao
internal interface OfflineDao {
    @Query("SELECT * FROM reports WHERE id = :id")
    suspend fun report(id: String): ReportRecord?

    @Query("SELECT * FROM report_lines WHERE reportId = :id ORDER BY position")
    suspend fun lines(id: String): List<ReportLineRecord>

    @Query("""
        SELECT EXISTS (
            SELECT 1 FROM reports
            WHERE constructionId IS :constructionId AND unlistedSiteName IS :unlistedSiteName
              AND date = :date AND shift = 'NIGHT'
        )
    """)
    suspend fun hasNight(constructionId: String?, unlistedSiteName: String?, date: String): Boolean

    @Query("SELECT * FROM report_outbox WHERE actionId = :id")
    suspend fun action(id: String): PendingReportAction?

    @Query("SELECT * FROM report_outbox ORDER BY sequence")
    suspend fun pending(): List<PendingReportAction>

    @Insert
    suspend fun insert(report: ReportRecord)

    @Update
    suspend fun update(report: ReportRecord)

    @Query("DELETE FROM report_lines WHERE reportId = :id")
    suspend fun deleteLines(id: String)

    @Insert
    suspend fun insertLines(lines: List<ReportLineRecord>)

    @Insert
    suspend fun enqueue(action: PendingReportAction)
}

internal class LineConverters {
    @TypeConverter
    fun readMaterials(json: String): List<WorkMaterial> = Json.decodeFromString(json)

    @TypeConverter
    fun writeMaterials(materials: List<WorkMaterial>): String = Json.encodeToString(materials)

    @TypeConverter
    fun readParticipants(json: String): List<String> = Json.decodeFromString(json)

    @TypeConverter
    fun writeParticipants(participants: List<String>): String = Json.encodeToString(participants)
}

/** Ключ экземпляра сохраняется подключением. Смена сотрудника или экземпляра открывает свою базу. */
data class ReportOwner(val instanceKey: String, val employeeId: UUID) {
    init {
        require(instanceKey.isNotBlank())
    }

    internal fun databaseName(): String {
        val key = "$instanceKey:$employeeId".encodeToByteArray()
        val hash = MessageDigest.getInstance("SHA-256").digest(key)
        return "offline-${Base64.getUrlEncoder().withoutPadding().encodeToString(hash)}.db"
    }
}

@Database(
    entities = [ReportRecord::class, ReportLineRecord::class, PendingReportAction::class],
    version = 2,
    autoMigrations = [AutoMigration(from = 1, to = 2)],
)
@TypeConverters(LineConverters::class)
internal abstract class OfflineDatabase : RoomDatabase() {
    abstract fun dao(): OfflineDao

    companion object {
        fun open(context: Context, owner: ReportOwner): OfflineDatabase =
            Room.databaseBuilder(context.applicationContext, OfflineDatabase::class.java, owner.databaseName())
                .build()
    }
}
