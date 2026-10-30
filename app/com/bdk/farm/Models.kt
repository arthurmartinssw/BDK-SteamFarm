package com.bdk.farm

data class Game(
    val appId: Long,
    val name: String,
    val farmedSeconds: Long,
    val steamMinutes: Long?,
)

data class Status(
    val account: String,
    val steamId: String?,
    val state: String,
    val stateText: String,
    val uptimeSeconds: Long,
    val sessionSeconds: Long?,
    val totalFarmedSeconds: Long,
    val tokenExpiresAt: String?,
    val reconnects: Int,
    val pausesByOtherPc: Int,
    val manualPause: Boolean,
    val games: List<Game>,
    val persona: String,
    val personaText: String,
)

data class OwnedGame(val appId: Long, val name: String, val minutes: Long)

data class Library(
    val configured: Boolean,
    val available: Boolean,
    val error: String?,
    val games: List<OwnedGame>,
    val selected: List<Long>,
)

data class FarmSession(val start: Long, val end: Long?, val reason: String, val games: Int)

data class History(val now: Long, val sessions: List<FarmSession>)

data class ScheduleInfo(
    val enabled: Boolean,
    val timeZone: String,
    val windows: List<String>,
    val active: Boolean,
    val nextChange: Long?,
    val raw: org.json.JSONObject,
)

data class Relogin(
    val state: String,
    val email: String?,
    val wrongCode: Boolean,
    val error: String?,
    val needsPassphrase: Boolean,
    val account: String?,
    val newExpiry: String?,
) {
    val active: Boolean
        get() = state == "connecting" || state == "need_device_code" ||
            state == "need_email_code" || state == "need_confirmation"
}

data class Achievement(
    val id: String,
    val name: String,
    val description: String,
    val hidden: Boolean,
    val achieved: Boolean,
    val unlockTime: Long,
    val percent: Double?,
)

data class AchievementList(
    val appId: Long,
    val name: String,
    val total: Int,
    val unlocked: Int,
    val items: List<Achievement>,
    val error: String?,
)

data class NotifyOptions(
    val mentionType: String,
    val mentionId: String,
    val mentionMode: String,
    val username: String,
    val summaryHour: Int,
)

data class NotifyInfo(
    val configured: Boolean,
    val events: Map<String, Boolean>,
    val options: NotifyOptions,
)

data class AiInfo(val configured: Boolean, val model: String)

data class WidgetData(
    val state: String,
    val stateText: String,
    val totalSeconds: Long,
    val games: Int,
    val manualPause: Boolean,
    val personaText: String,
    val updatedAt: Long,
    val error: String?,
) {
    companion object {
        fun from(s: Status) = WidgetData(
            state = s.state,
            stateText = s.stateText,
            totalSeconds = s.totalFarmedSeconds,
            games = s.games.size,
            manualPause = s.manualPause,
            personaText = s.personaText,
            updatedAt = System.currentTimeMillis(),
            error = null,
        )
    }
}
