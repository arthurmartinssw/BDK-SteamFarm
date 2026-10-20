package com.bdk.farm

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.RoundRect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.time.LocalDate
import java.time.ZoneId

data class Block(val lo: Long, val hi: Long, val s: FarmSession)

data class DayAgg(
    val date: LocalDate,
    val start: Long,
    val end: Long,
    val secs: Long,
    val gameSecs: Long,
    val count: Int,
    val blocks: List<Block>,
)

/** Para cada dia local, soma o tempo farmando (cortando sessões que viram a meia-noite). */
fun aggregate(sessions: List<FarmSession>, days: Int, nowSec: Long): List<DayAgg> {
    val zone = ZoneId.systemDefault()
    val today = LocalDate.now(zone)

    return (days - 1 downTo 0).map { k ->
        val d = today.minusDays(k.toLong())
        val a = d.atStartOfDay(zone).toEpochSecond()
        val b = d.plusDays(1).atStartOfDay(zone).toEpochSecond()

        var secs = 0L
        var gameSecs = 0L
        var count = 0
        val blocks = mutableListOf<Block>()

        for (s in sessions) {
            val lo = maxOf(s.start, a)
            val hi = minOf(s.end ?: nowSec, b)
            if (hi > lo) {
                secs += hi - lo
                gameSecs += (hi - lo) * s.games
                count++
                blocks.add(Block(lo, hi, s))
            }
        }
        DayAgg(d, a, b, secs, gameSecs, count, blocks)
    }
}

private fun niceMax(hours: Double): Int {
    for (m in listOf(1, 2, 3, 4, 6, 8, 12, 24)) if (hours * 1.05 <= m) return m
    return 24
}

@Composable
private fun seriesColor(): Color = if (isSystemInDarkTheme()) Color(0xFF3987E5) else Color(0xFF2A78D6)

val REASONS = mapOf(
    "outro_pc" to "Pausou: você jogou em outro PC",
    "painel" to "Pausado pelo painel",
    "desconexao" to "A conexão com a Steam caiu",
    "lista" to "Lista de jogos alterada",
    "encerrado" to "Programa reiniciado ou parado",
    "queda" to "O servidor desligou ou caiu",
    "agenda" to "Pausado pela agenda",
)

@Composable
fun DaysChart(days: List<DayAgg>) {
    val color = seriesColor()
    val grid = MaterialTheme.colorScheme.outline
    var sel by remember(days.size) { mutableStateOf(-1) }

    val maxHours = days.maxOf { it.secs } / 3600.0
    val top = niceMax(maxHours)

    val picked = days.getOrNull(sel)
    Text(
        text = if (picked == null) "Toque numa barra para ver o dia."
        else "${fmtDay(picked.date)} · ${if (picked.secs > 0) fmtDuration(picked.secs) else "sem farm"}" +
            if (picked.count > 0) " · ${picked.count} ${if (picked.count == 1) "sessão" else "sessões"}" else "",
        style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Spacer(Modifier.height(6.dp))

    Box {
        Canvas(
            Modifier
                .fillMaxWidth()
                .height(150.dp)
                .pointerInput(days.size) {
                    detectTapGestures { off ->
                        val slot = size.width.toFloat() / days.size
                        sel = (off.x / slot).toInt().coerceIn(0, days.size - 1)
                    }
                }
        ) {
            val slot = size.width / days.size
            val barW = minOf(24.dp.toPx(), slot * 0.62f)
            val line = 1.dp.toPx()

            for (f in listOf(0f, 0.5f, 1f)) {
                val y = size.height - f * (size.height - line)
                drawLine(grid, Offset(0f, y), Offset(size.width, y), strokeWidth = line)
            }

            days.forEachIndexed { i, d ->
                val x = i * slot + (slot - barW) / 2
                if (i == sel) {
                    drawRect(color.copy(alpha = 0.12f), Offset(i * slot, 0f), Size(slot, size.height))
                }
                val h = (d.secs / 3600.0 / top * size.height).toFloat()
                if (h > 1f) {
                    val r = minOf(4.dp.toPx(), h, barW / 2)
                    val path = Path().apply {
                        addRoundRect(
                            RoundRect(
                                rect = Rect(x, size.height - h, x + barW, size.height),
                                topLeft = CornerRadius(r),
                                topRight = CornerRadius(r),
                                bottomRight = CornerRadius.Zero,
                                bottomLeft = CornerRadius.Zero,
                            )
                        )
                    }
                    drawPath(path, color)
                }
            }
        }
        Text(
            "$top h",
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.align(Alignment.TopStart),
        )
    }

    Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.SpaceBetween) {
        Text(fmtDay(days.first().date), fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text("hoje", fontSize = 11.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

@Composable
fun Timeline(days: List<DayAgg>) {
    val color = seriesColor()
    val track = MaterialTheme.colorScheme.surfaceVariant
    val grid = MaterialTheme.colorScheme.outline
    val rowH = 22.dp
    val muted = MaterialTheme.colorScheme.onSurfaceVariant

    Row(Modifier.fillMaxWidth()) {
        Spacer(Modifier.width(52.dp))
        Row(Modifier.weight(1f), horizontalArrangement = Arrangement.SpaceBetween) {
            for (h in listOf("0h", "6h", "12h", "18h", "24h")) Text(h, fontSize = 11.sp, color = muted)
        }
    }
    Spacer(Modifier.height(4.dp))

    Row(Modifier.fillMaxWidth()) {
        Column(Modifier.width(52.dp)) {
            days.forEachIndexed { i, d ->
                Box(Modifier.height(rowH), contentAlignment = Alignment.CenterStart) {
                    Text(
                        if (i == days.lastIndex) "hoje" else fmtShortDay(d.date) + " " + d.date.dayOfMonth,
                        fontSize = 11.sp,
                        color = if (i == days.lastIndex) MaterialTheme.colorScheme.onSurface else muted,
                        fontWeight = if (i == days.lastIndex) FontWeight.SemiBold else FontWeight.Normal,
                    )
                }
            }
        }
        Canvas(Modifier.weight(1f).height(rowH * days.size)) {
            val rh = size.height / days.size
            for (hour in listOf(6, 12, 18)) {
                val x = hour / 24f * size.width
                drawLine(grid, Offset(x, 0f), Offset(x, size.height), strokeWidth = 1.dp.toPx())
            }
            days.forEachIndexed { i, d ->
                val y0 = i * rh
                drawRoundRect(
                    track, Offset(0f, y0 + 4.dp.toPx()), Size(size.width, rh - 8.dp.toPx()),
                    CornerRadius(4.dp.toPx()),
                )
                for (b in d.blocks) {
                    val x = (b.lo - d.start).toFloat() / (d.end - d.start) * size.width
                    val w = maxOf(2.dp.toPx(), (b.hi - b.lo).toFloat() / (d.end - d.start) * size.width)
                    drawRoundRect(
                        color, Offset(x, y0 + 6.dp.toPx()), Size(w, rh - 12.dp.toPx()),
                        CornerRadius(3.dp.toPx()),
                    )
                }
            }
        }
    }
}

@Composable
fun HistoryTab(vm: FarmViewModel) {
    val h = vm.history
    var range by rememberSaveable { mutableStateOf(7) }
    var shown by rememberSaveable { mutableStateOf(8) }

    androidx.compose.foundation.lazy.LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Histórico", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
                for (n in listOf(7, 30)) {
                    if (range == n) Button(onClick = { range = n }) { Text("$n dias") }
                    else OutlinedButton(onClick = { range = n }) { Text("$n dias") }
                    Spacer(Modifier.width(6.dp))
                }
            }
        }

        if (h == null) {
            item { Text("Carregando…", color = MaterialTheme.colorScheme.onSurfaceVariant) }
            return@LazyColumn
        }

        val days = aggregate(h.sessions, range, h.now)
        val secs = days.sumOf { it.secs }
        val gameSecs = days.sumOf { it.gameSecs }
        val since = days.first().start
        val count = h.sessions.count { (it.end ?: h.now) >= since }

        item {
            Card(colors = cardColors()) {
                Column(Modifier.padding(16.dp)) {
                    Row(Modifier.fillMaxWidth()) {
                        Summary("Farmando", fmtHours(secs), Modifier.weight(1f))
                        Summary("Soma nos jogos", fmtHours(gameSecs), Modifier.weight(1f))
                        Summary("Sessões", count.toString(), Modifier.weight(1f))
                    }
                    Spacer(Modifier.height(16.dp))
                    Text("Tempo farmando por dia", fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.height(4.dp))
                    if (secs == 0L) EmptyNote("Ainda não há sessões neste período.") else DaysChart(days)

                    Spacer(Modifier.height(20.dp))
                    Text("Linha do tempo (7 dias)", fontWeight = FontWeight.SemiBold)
                    Text(
                        "Os espaços vazios são pausas ou paradas.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Spacer(Modifier.height(8.dp))
                    Timeline(aggregate(h.sessions, 7, h.now))
                }
            }
        }

        item { Text("Sessões", style = MaterialTheme.typography.titleMedium) }

        val items = h.sessions.reversed()
        if (items.isEmpty()) item { EmptyNote("Nenhuma sessão registrada ainda.") }

        sessionItems(items.take(shown), h.now)

        if (items.size > shown) {
            item { OutlinedButton(onClick = { shown += 10 }, modifier = Modifier.fillMaxWidth()) { Text("Mostrar mais") } }
        }
    }
}

private fun LazyListScope.sessionItems(list: List<FarmSession>, now: Long) {
    items(list.size) { i ->
        val s = list[i]
        val open = s.end == null
        val endTs = s.end ?: now
        val sameDay = fmtDayOf(s.start) == fmtDayOf(endTs)

        Card(colors = cardColors()) {
            Column(Modifier.padding(12.dp)) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(
                        "${fmtDayOf(s.start)} · ${fmtClock(s.start)} → " +
                            (if (open) "agora" else fmtClock(endTs) + if (sameDay) "" else " (+1 dia)"),
                        fontWeight = FontWeight.SemiBold,
                        modifier = Modifier.weight(1f),
                    )
                    Text(fmtDuration(endTs - s.start), fontWeight = FontWeight.SemiBold)
                }
                Text(
                    (if (open) "Em andamento" else REASONS[s.reason] ?: "Sessão encerrada") +
                        " · ${s.games} ${if (s.games == 1) "jogo" else "jogos"}",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun Summary(label: String, value: String, modifier: Modifier) {
    Column(modifier) {
        Text(label, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
fun EmptyNote(text: String) {
    Text(
        text,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.fillMaxWidth().padding(vertical = 16.dp),
    )
}
