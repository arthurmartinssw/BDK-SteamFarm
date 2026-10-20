package com.bdk.farm

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.IOException
import java.net.URL

// ---------- tema ----------

@Composable
fun FarmTheme(content: @Composable () -> Unit) {
    val scheme = if (isSystemInDarkTheme()) {
        darkColorScheme(
            primary = Color(0xFF22C55E), onPrimary = Color(0xFF06210F),
            background = Color(0xFF0E1116), onBackground = Color(0xFFE7EBF0),
            surface = Color(0xFF161B22), onSurface = Color(0xFFE7EBF0),
            surfaceVariant = Color(0xFF1D232C), onSurfaceVariant = Color(0xFF8B95A3),
            outline = Color(0xFF262D37), error = Color(0xFFF87171),
        )
    } else {
        lightColorScheme(
            primary = Color(0xFF16A34A), onPrimary = Color.White,
            background = Color(0xFFF4F5F7), onBackground = Color(0xFF12161C),
            surface = Color.White, onSurface = Color(0xFF12161C),
            surfaceVariant = Color(0xFFEEF0F3), onSurfaceVariant = Color(0xFF5D6673),
            outline = Color(0xFFDDE1E6), error = Color(0xFFDC2626),
        )
    }
    MaterialTheme(colorScheme = scheme, content = content)
}

@Composable
fun cardColors() = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surface)

// ---------- capas dos jogos (sem biblioteca de imagens) ----------

private object Covers {
    private val cache = LruCache<Long, Bitmap>(80)

    suspend fun load(appId: Long): Bitmap? {
        cache.get(appId)?.let { return it }
        val bmp = withContext(Dispatchers.IO) {
            try {
                URL("https://cdn.akamai.steamstatic.com/steam/apps/$appId/capsule_184x69.jpg")
                    .openStream().use { BitmapFactory.decodeStream(it) }
            } catch (e: IOException) {
                null
            }
        }
        if (bmp != null) cache.put(appId, bmp)
        return bmp
    }
}

@Composable
fun Cover(appId: Long, modifier: Modifier = Modifier) {
    var bmp by remember(appId) { mutableStateOf<Bitmap?>(null) }
    LaunchedEffect(appId) { bmp = Covers.load(appId) }

    Box(modifier.clip(RoundedCornerShape(6.dp)).background(MaterialTheme.colorScheme.surfaceVariant)) {
        bmp?.let {
            Image(it.asImageBitmap(), null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        }
    }
}

// ---------- raiz ----------

@Composable
fun FarmApp(vm: FarmViewModel) {
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        when {
            !vm.connected -> ConnectScreen(vm)
            vm.showPicker -> PickerScreen(vm)
            else -> MainScreen(vm)
        }
    }
}

@Composable
fun ConnectScreen(vm: FarmViewModel) {
    var link by rememberSaveable { mutableStateOf("") }
    var token by rememberSaveable { mutableStateOf("") }
    var problem by remember { mutableStateOf<String?>(null) }

    Column(
        Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().imePadding().padding(24.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text("BDK Hour Farm", style = MaterialTheme.typography.headlineMedium, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(8.dp))
        Text(
            "Cole o link completo do painel (o que termina com #token=…). Se preferir, preencha o endereço e o token separados.",
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
        Spacer(Modifier.height(20.dp))
        OutlinedTextField(
            value = link, onValueChange = { link = it }, singleLine = true,
            label = { Text("Link ou endereço") }, modifier = Modifier.fillMaxWidth(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            value = token, onValueChange = { token = it }, singleLine = true,
            label = { Text("Token (se não estiver no link)") }, modifier = Modifier.fillMaxWidth(),
            visualTransformation = PasswordVisualTransformation(),
        )
        problem?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, color = MaterialTheme.colorScheme.error)
        }
        Spacer(Modifier.height(20.dp))
        Button(onClick = { problem = vm.connect(link, token) }, modifier = Modifier.fillMaxWidth().height(52.dp)) {
            Text("Conectar")
        }
    }
}

@Composable
fun MainScreen(vm: FarmViewModel) {
    var tab by rememberSaveable { mutableStateOf(0) }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surface) {
                NavigationBarItem(selected = tab == 0, onClick = { tab = 0 }, icon = { Text("◉") }, label = { Text("Início") })
                NavigationBarItem(selected = tab == 1, onClick = { tab = 1 }, icon = { Text("▤") }, label = { Text("Histórico") })
            }
        },
    ) { pad ->
        Box(Modifier.padding(pad).fillMaxSize()) {
            if (tab == 0) HomeTab(vm) else HistoryTab(vm)
        }
    }
}

@Composable
fun HomeTab(vm: FarmViewModel) {
    val s = vm.status
    var confirmExit by remember { mutableStateOf(false) }

    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("BDK Hour Farm", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                    Text(
                        s?.account ?: "Conectando…", fontSize = 13.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                TextButton(onClick = { vm.refresh() }) { Text("Atualizar") }
                TextButton(onClick = { confirmExit = true }) { Text("Sair") }
            }
        }

        vm.error?.let { msg ->
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.error.copy(alpha = 0.12f))) {
                    Text(msg, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error)
                }
            }
        }

        if (s == null) {
            item { EmptyNote("Carregando…") }
            return@LazyColumn
        }

        item { StatusCard(vm, s) }
        if (s.state == "locked") item { LockCard(vm) }

        item {
            val days = daysUntil(s.tokenExpiresAt)
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Stat("Programa ligado há", fmtDuration(s.uptimeSeconds), Modifier.weight(1f))
                    Stat("Total farmado", fmtHours(s.totalFarmedSeconds), Modifier.weight(1f))
                }
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Stat("Jogos", s.games.size.toString(), Modifier.weight(1f))
                    Stat("Pausas (outro PC)", s.pausesByOtherPc.toString(), Modifier.weight(1f))
                }
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Stat("Reconexões", s.reconnects.toString(), Modifier.weight(1f))
                    Stat(
                        "Login vence em",
                        if (days == null) "—" else "$days ${if (days == 1L) "dia" else "dias"}",
                        Modifier.weight(1f),
                        warn = days != null && days < 14,
                    )
                }
            }
        }

        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Jogos", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                OutlinedButton(onClick = { vm.openPicker() }) { Text("Escolher jogos") }
            }
        }

        val sorted = s.games.sortedByDescending { it.farmedSeconds }
        items(sorted.size) { i -> GameRow(sorted[i]) }

        item { ScheduleCard(vm) }
        item { NotifyCard(vm) }
    }

    if (confirmExit) {
        AlertDialog(
            onDismissRequest = { confirmExit = false },
            title = { Text("Sair do painel?") },
            text = { Text("O token salvo neste celular será apagado. O farm na VPS continua rodando.") },
            confirmButton = { TextButton(onClick = { confirmExit = false; vm.disconnect() }) { Text("Sair") } },
            dismissButton = { TextButton(onClick = { confirmExit = false }) { Text("Cancelar") } },
        )
    }
}

@Composable
private fun StatusCard(vm: FarmViewModel, s: Status) {
    val dot = when (s.state) {
        "farming" -> MaterialTheme.colorScheme.primary
        "paused_other", "paused_manual", "paused_schedule", "locked" -> Color(0xFFF59E0B)
        "offline" -> MaterialTheme.colorScheme.error
        else -> Color(0xFF60A5FA)
    }
    val detail = when (s.state) {
        "farming" -> "Sessão atual: ${fmtDuration(s.sessionSeconds)}"
        "paused_other" -> "Volta sozinho quando você fechar o jogo"
        "paused_manual" -> "Toque em Retomar para voltar a farmar"
        "paused_schedule" -> "Volta sozinho quando o próximo horário da agenda começar"
        "offline" -> "Tentando reconectar…"
        "locked" -> "Digite a senha abaixo para começar"
        else -> "Começa em alguns segundos"
    }

    Card(colors = cardColors()) {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(14.dp).clip(CircleShape).background(dot))
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(s.stateText, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                Text(detail, fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            val enabled = !vm.busy && s.state != "offline" && s.state != "locked"
            if (s.manualPause) Button(onClick = { vm.toggle() }, enabled = enabled) { Text("Retomar") }
            else OutlinedButton(onClick = { vm.toggle() }, enabled = enabled) { Text("Pausar") }
        }
    }
}

@Composable
private fun LockCard(vm: FarmViewModel) {
    var pass by remember { mutableStateOf("") }

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Farm bloqueado", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                "A sessão da conta está protegida por senha. Digite a senha de proteção para o farm começar.",
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(12.dp))
            OutlinedTextField(
                value = pass, onValueChange = { pass = it }, singleLine = true,
                label = { Text("Senha de proteção") }, modifier = Modifier.fillMaxWidth(),
                visualTransformation = PasswordVisualTransformation(),
            )
            vm.unlockError?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 6.dp)) }
            Spacer(Modifier.height(12.dp))
            Button(onClick = { vm.unlock(pass); pass = "" }, enabled = !vm.busy && pass.isNotEmpty()) { Text("Desbloquear") }
        }
    }
}

@Composable
private fun Stat(label: String, value: String, modifier: Modifier, warn: Boolean = false) {
    Card(modifier, colors = cardColors()) {
        Column(Modifier.padding(horizontal = 14.dp, vertical = 12.dp)) {
            Text(label, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(
                value, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold,
                color = if (warn) Color(0xFFF59E0B) else MaterialTheme.colorScheme.onSurface,
            )
        }
    }
}

@Composable
private fun GameRow(g: Game) {
    Card(colors = cardColors()) {
        Row(Modifier.padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
            Cover(g.appId, Modifier.width(92.dp).height(35.dp))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(g.name, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    "AppID ${g.appId}" + (g.steamMinutes?.let { " · ${fmtHours(it * 60)} na Steam" } ?: ""),
                    fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
            }
            Spacer(Modifier.width(8.dp))
            Column(horizontalAlignment = Alignment.End) {
                Text(fmtHours(g.farmedSeconds), fontWeight = FontWeight.SemiBold)
                Text("pelo farm", fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

@Composable
private fun NotifyCard(vm: FarmViewModel) {
    var url by remember { mutableStateOf("") }
    val on = vm.notifyConfigured == true

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Avisos no Discord", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                when (vm.notifyConfigured) {
                    true -> "Avisos ligados."
                    false -> "Avisos desligados. Cole um webhook para ligar."
                    null -> "Carregando…"
                },
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Spacer(Modifier.height(8.dp))
            OutlinedTextField(
                value = url, onValueChange = { url = it }, singleLine = true,
                label = { Text("Webhook do Discord") }, modifier = Modifier.fillMaxWidth(),
                visualTransformation = PasswordVisualTransformation(),
            )
            Text(
                "Discord: Configurações do servidor › Integrações › Webhooks › Novo webhook › Copiar URL.",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 4.dp),
            )
            vm.notifyMessage?.let {
                Text(it, modifier = Modifier.padding(top = 6.dp), color = MaterialTheme.colorScheme.onSurface)
            }
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { vm.saveWebhook(url); url = "" }, enabled = url.isNotBlank() && !vm.busy) { Text("Salvar") }
                OutlinedButton(onClick = { vm.testWebhook() }, enabled = on && !vm.busy) { Text("Teste") }
                TextButton(onClick = { vm.removeWebhook() }, enabled = on && !vm.busy) { Text("Remover") }
            }
        }
    }
}

@Composable
private fun ScheduleCard(vm: FarmViewModel) {
    val sch = vm.schedule

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "Agenda", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f),
                )
                if (sch != null) {
                    Switch(checked = sch.enabled, onCheckedChange = { vm.setScheduleEnabled(it) }, enabled = !vm.busy)
                }
            }

            if (sch == null) {
                Text("Carregando…", color = MaterialTheme.colorScheme.onSurfaceVariant)
            } else {
                val text = when {
                    !sch.enabled -> "Desligada: o farm roda o tempo todo."
                    else -> (if (sch.active) "Agora: dentro do horário." else "Agora: fora do horário.") +
                        (sch.nextChange?.let { " Próxima mudança: ${describeTime(it, sch.timeZone)}." } ?: "")
                }
                Text(text, color = MaterialTheme.colorScheme.onSurfaceVariant)

                sch.windows.forEach {
                    Text(it, modifier = Modifier.padding(top = 4.dp), fontWeight = FontWeight.SemiBold)
                }
                Text(
                    "Os horários são editados no painel web.",
                    fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(top = 6.dp),
                )
            }

            vm.scheduleMessage?.let {
                Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 6.dp))
            }
        }
    }
}
