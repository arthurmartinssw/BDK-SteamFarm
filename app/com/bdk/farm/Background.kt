package com.bdk.farm

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.time.Instant
import java.util.concurrent.TimeUnit

object PhoneNotify {
    const val CHANNEL = "farm"

    val types = listOf(
        "parou" to "Farm parou ou perdeu a conexão com a Steam",
        "voltou" to "Farm voltou a farmar",
        "outro_pc" to "Pausou porque você está jogando em outro PC",
        "login" to "Login da Steam perto de vencer",
        "vps" to "Não consegui falar com a VPS",
    )

    fun createChannel(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val channel = NotificationChannel(CHANNEL, "Avisos do farm", NotificationManager.IMPORTANCE_DEFAULT).apply {
            description = "Mudanças de estado do BDK Hour Farm"
        }
        context.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    fun allowed(context: Context): Boolean =
        Build.VERSION.SDK_INT < 33 ||
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    fun show(context: Context, id: Int, title: String, text: String) {
        if (!allowed(context)) return
        createChannel(context)

        val open = PendingIntent.getActivity(
            context, 0,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )

        val n = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_farm)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setContentIntent(open)
            .setAutoCancel(true)
            .build()

        try {
            NotificationManagerCompat.from(context).notify(id, n)
        } catch (e: SecurityException) {
        }
    }

    fun schedule(context: Context) {
        val settings = Settings(context)
        val wm = WorkManager.getInstance(context)

        if (!settings.phoneNotify && !FarmWidget.hasWidgets(context)) {
            wm.cancelUniqueWork(StatusWorker.NAME)
            return
        }

        val request = PeriodicWorkRequestBuilder<StatusWorker>(settings.phoneInterval.toLong().coerceAtLeast(15), TimeUnit.MINUTES)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build())
            .build()
        wm.enqueueUniquePeriodicWork(StatusWorker.NAME, ExistingPeriodicWorkPolicy.UPDATE, request)
    }
}

class StatusWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val ctx = applicationContext
        val settings = Settings(ctx)
        if (settings.baseUrl.isBlank() || settings.token.isBlank()) return Result.success()

        val status = try {
            Api(settings.baseUrl, settings.token).status()
        } catch (e: ApiException) {
            onFailure(ctx, settings, e)
            return Result.success()
        }

        val failedBefore = settings.unreachableCount >= 2
        settings.unreachableCount = 0
        if (failedBefore) notify(ctx, settings, "vps", 5, "Farm respondendo de novo", "A VPS voltou a responder.")

        settings.saveWidget(WidgetData.from(status))
        FarmWidget.updateAll(ctx)

        checkTransitions(ctx, settings, status)
        return Result.success()
    }

    private fun onFailure(ctx: Context, settings: Settings, e: ApiException) {
        val count = settings.unreachableCount + 1
        settings.unreachableCount = count

        settings.loadWidget()?.let { settings.saveWidget(it.copy(error = e.message, updatedAt = System.currentTimeMillis())) }
        FarmWidget.updateAll(ctx)

        if (count == 2) {
            notify(
                ctx, settings, "vps", 5,
                if (e.auth) "Token do farm recusado" else "Sem resposta do farm",
                e.message ?: "Não consegui falar com a VPS.",
            )
        }
    }

    private fun checkTransitions(ctx: Context, settings: Settings, s: Status) {
        val before = settings.lastState
        val now = s.state
        settings.lastState = now

        if (before.isNotEmpty() && before != now) {
            when (now) {
                "offline" -> notify(ctx, settings, "parou", 1, "Farm sem conexão", "O farm perdeu a conexão com a Steam e está tentando voltar.")
                "locked" -> notify(ctx, settings, "parou", 1, "Farm bloqueado", "Abra o app e digite a senha de proteção para o farm começar.")
                "paused_other" -> notify(ctx, settings, "outro_pc", 2, "Farm pausado", "Você está jogando em outro PC. O farm volta sozinho quando você fechar o jogo.")
                "farming" -> if (before == "offline" || before == "locked" || before == "paused_other") {
                    notify(ctx, settings, "voltou", 3, "Farm voltou", "Farmando ${s.games.size} jogo(s) de novo.")
                }
            }
        }

        val days = daysUntil(s.tokenExpiresAt) ?: return
        val today = Instant.now().epochSecond / 86400
        if (days < 14 && settings.lastLoginWarnDay != today) {
            settings.lastLoginWarnDay = today
            notify(ctx, settings, "login", 4, "Login perto de vencer", "O login da Steam vence em ${days.coerceAtLeast(0)} dia(s). Abra Ajustes › Renovar login.")
        }
    }

    private fun notify(ctx: Context, settings: Settings, type: String, id: Int, title: String, text: String) {
        if (settings.phoneNotify && settings.phoneType(type)) PhoneNotify.show(ctx, id, title, text)
    }

    companion object {
        const val NAME = "bdk-status"
    }
}
