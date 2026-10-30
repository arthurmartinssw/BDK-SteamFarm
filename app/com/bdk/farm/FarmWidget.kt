package com.bdk.farm

import android.app.PendingIntent
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.widget.RemoteViews
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

class FarmWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) {
        render(context, manager, ids)
        refreshAsync(context, toggle = false)
    }

    override fun onEnabled(context: Context) {
        PhoneNotify.schedule(context)
    }

    override fun onDisabled(context: Context) {
        PhoneNotify.schedule(context)
    }

    override fun onReceive(context: Context, intent: Intent) {
        super.onReceive(context, intent)
        when (intent.action) {
            ACTION_TOGGLE -> refreshAsync(context, toggle = true)
            ACTION_REFRESH -> refreshAsync(context, toggle = false)
        }
    }

    private fun refreshAsync(context: Context, toggle: Boolean) {
        val pending = goAsync()
        val app = context.applicationContext
        scope.launch {
            try {
                val settings = Settings(app)
                if (settings.baseUrl.isBlank() || settings.token.isBlank()) return@launch

                val api = Api(settings.baseUrl, settings.token)
                val result = withTimeoutOrNull(8_000L) {
                    try {
                        val current = api.status()
                        val s = if (!toggle) current
                        else if (current.manualPause) api.resume()
                        else api.pause()
                        settings.saveWidget(WidgetData.from(s))
                        settings.lastState = s.state
                        ""
                    } catch (e: ApiException) {
                        e.message ?: "Erro"
                    }
                } ?: "Sem resposta do farm."

                if (result.isNotEmpty()) {
                    settings.loadWidget()?.let { settings.saveWidget(it.copy(error = result, updatedAt = System.currentTimeMillis())) }
                }
                updateAll(app)
            } finally {
                pending.finish()
            }
        }
    }

    companion object {
        const val ACTION_TOGGLE = "com.bdk.farm.WIDGET_TOGGLE"
        const val ACTION_REFRESH = "com.bdk.farm.WIDGET_REFRESH"

        private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

        fun hasWidgets(context: Context): Boolean =
            AppWidgetManager.getInstance(context)
                .getAppWidgetIds(ComponentName(context, FarmWidget::class.java)).isNotEmpty()

        fun updateAll(context: Context) {
            val manager = AppWidgetManager.getInstance(context)
            val ids = manager.getAppWidgetIds(ComponentName(context, FarmWidget::class.java))
            if (ids.isNotEmpty()) render(context, manager, ids)
        }

        fun push(context: Context, status: Status) {
            Settings(context).saveWidget(WidgetData.from(status))
            updateAll(context)
        }

        fun requestPin(context: Context): Boolean {
            val manager = AppWidgetManager.getInstance(context)
            if (android.os.Build.VERSION.SDK_INT < 26 || !manager.isRequestPinAppWidgetSupported) return false
            return manager.requestPinAppWidget(ComponentName(context, FarmWidget::class.java), null, null)
        }

        private fun broadcast(context: Context, action: String, code: Int): PendingIntent =
            PendingIntent.getBroadcast(
                context, code,
                Intent(context, FarmWidget::class.java).setAction(action),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )

        private fun render(context: Context, manager: AppWidgetManager, ids: IntArray) {
            val settings = Settings(context)
            val data = settings.loadWidget()
            val views = RemoteViews(context.packageName, R.layout.widget_farm)

            if (settings.token.isBlank()) {
                views.setTextViewText(R.id.w_state, "Abra o app para conectar")
                views.setTextViewText(R.id.w_detail, "")
                views.setTextViewText(R.id.w_updated, "")
                views.setTextViewText(R.id.w_toggle, "Abrir")
                views.setInt(R.id.w_dot, "setColorFilter", Color.GRAY)
            } else if (data == null) {
                views.setTextViewText(R.id.w_state, "Carregando…")
                views.setTextViewText(R.id.w_detail, "")
                views.setTextViewText(R.id.w_updated, "")
                views.setTextViewText(R.id.w_toggle, "Pausar")
                views.setInt(R.id.w_dot, "setColorFilter", Color.GRAY)
            } else {
                views.setTextViewText(R.id.w_state, data.stateText.ifBlank { "—" })
                views.setTextViewText(
                    R.id.w_detail,
                    "${fmtHours(data.totalSeconds)} farmadas · ${data.games} jogo(s)" +
                        if (data.personaText.isNotBlank()) " · ${data.personaText}" else "",
                )
                views.setTextViewText(
                    R.id.w_updated,
                    data.error?.let { "⚠ $it" } ?: "Atualizado às ${fmtClock(data.updatedAt / 1000)}",
                )
                views.setTextViewText(R.id.w_toggle, if (data.manualPause) "Retomar" else "Pausar")
                views.setInt(R.id.w_dot, "setColorFilter", dotColor(data.state))
            }

            val open = PendingIntent.getActivity(
                context, 10,
                Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            views.setOnClickPendingIntent(R.id.w_root, open)
            views.setOnClickPendingIntent(R.id.w_refresh, broadcast(context, ACTION_REFRESH, 11))
            views.setOnClickPendingIntent(
                R.id.w_toggle,
                if (settings.token.isBlank()) open else broadcast(context, ACTION_TOGGLE, 12),
            )

            manager.updateAppWidget(ids, views)
        }

        private fun dotColor(state: String): Int = when (state) {
            "farming" -> Color.parseColor("#22C55E")
            "starting" -> Color.parseColor("#3B82F6")
            "offline", "locked" -> Color.parseColor("#EF4444")
            else -> Color.parseColor("#F59E0B")
        }
    }
}
