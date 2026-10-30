package com.bdk.farm

import android.Manifest
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val PersonaOptions = listOf(
    "Online" to "Online",
    "Away" to "Ausente",
    "Busy" to "Ocupado",
    "Invisible" to "Invisível",
    "Offline" to "Offline",
)

private val DiscordEvents = listOf(
    "inicio" to "Farm iniciou ou está parando",
    "conexao" to "Conexão com a Steam caiu e voltou",
    "painel" to "Pausei ou retomei pelo painel/app",
    "outro_pc" to "Pausou/voltou por jogar em outro PC",
    "agenda" to "A agenda pausou ou retomou",
    "login" to "Login perto de vencer, renovado ou bloqueado",
    "seguranca" to "Segurança: tokens inválidos, token trocado",
    "status" to "Troquei o status da Steam",
)

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun PersonaCard(vm: FarmViewModel, s: Status) {
    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Status na Steam", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                "As horas contam em qualquer status. Invisível e Offline escondem você dos amigos.",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            FlowRow(
                Modifier.padding(top = 8.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                PersonaOptions.forEach { (key, label) ->
                    FilterChip(
                        selected = s.persona == key,
                        onClick = { vm.setPersona(key) },
                        enabled = !vm.personaBusy,
                        label = { Text(label) },
                    )
                }
            }
        }
    }
}

@Composable
fun SettingsTab(vm: FarmViewModel) {
    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { Text("Ajustes", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold) }
        item { AppearanceCard(vm) }
        item { PhoneNotifyCard(vm) }
        item { WidgetCard() }
        item { NotifyCard(vm) }
        item { DiscordOptionsCard(vm) }
        item { ReloginCard(vm) }
        item { TokenCard(vm) }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AppearanceCard(vm: FarmViewModel) {
    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Aparência", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text("Tema", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 8.dp))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("system" to "Sistema", "light" to "Claro", "dark" to "Escuro", "amoled" to "Preto (AMOLED)").forEach { (key, label) ->
                    FilterChip(selected = vm.themeMode == key, onClick = { vm.setTheme(key) }, label = { Text(label) })
                }
            }
            Text("Cor de destaque", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 12.dp))
            Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                val dark = MaterialTheme.colorScheme.background.luminanceIsDark()
                Accents.forEach { (key, light, darkColor) ->
                    val selected = vm.accent == key
                    Box(
                        Modifier
                            .size(36.dp)
                            .clip(CircleShape)
                            .background(if (dark) darkColor else light)
                            .border(
                                width = if (selected) 3.dp else 0.dp,
                                color = MaterialTheme.colorScheme.onSurface,
                                shape = CircleShape,
                            )
                            .clickable { vm.setAccentColor(key) },
                        contentAlignment = Alignment.Center,
                    ) {
                        if (selected) Text("✓", color = MaterialTheme.colorScheme.onPrimary, fontWeight = FontWeight.Bold)
                    }
                }
            }
        }
    }
}

private fun androidx.compose.ui.graphics.Color.luminanceIsDark(): Boolean = (red * 0.299f + green * 0.587f + blue * 0.114f) < 0.5f

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PhoneNotifyCard(vm: FarmViewModel) {
    val context = LocalContext.current
    var denied by remember { mutableStateOf(false) }
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        denied = !granted
        vm.setPhoneNotifyEnabled(granted)
    }

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(
                    "Notificações no celular", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.weight(1f),
                )
                Switch(
                    checked = vm.phoneNotify,
                    onCheckedChange = { on ->
                        if (on && Build.VERSION.SDK_INT >= 33 && !PhoneNotify.allowed(context)) {
                            launcher.launch(Manifest.permission.POST_NOTIFICATIONS)
                        } else {
                            denied = false
                            vm.setPhoneNotifyEnabled(on)
                        }
                    },
                )
            }
            Text(
                "O app confere o farm de tempos em tempos, mesmo fechado, e avisa quando algo muda. " +
                    "O Android pode atrasar a checagem para poupar bateria.",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (denied) {
                Text(
                    "Permissão de notificação negada. Libere em Configurações do Android › Apps › BDK Farm › Notificações.",
                    fontSize = 13.sp, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 6.dp),
                )
            }

            if (vm.phoneNotify) {
                Text("Conferir a cada", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 10.dp))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf(15, 30, 60).forEach { m ->
                        FilterChip(
                            selected = vm.phoneInterval == m,
                            onClick = { vm.setPhoneNotifyInterval(m) },
                            label = { Text(if (m < 60) "$m min" else "1 hora") },
                        )
                    }
                }
                Text("Avisar quando", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 10.dp))
                PhoneNotify.types.forEach { (key, label) ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(checked = vm.phoneTypes[key] != false, onCheckedChange = { vm.setPhoneType(key, it) })
                        Text(label, fontSize = 14.sp)
                    }
                }
            }
        }
    }
}

@Composable
private fun WidgetCard() {
    val context = LocalContext.current
    var msg by remember { mutableStateOf<String?>(null) }

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Widget", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                "Mostra o estado do farm, as horas e um botão de Pausar/Retomar na tela inicial. " +
                    "Atualiza junto com as checagens do app (15 min no mínimo) e quando você toca em ↻.",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            OutlinedButton(
                onClick = {
                    msg = if (FarmWidget.requestPin(context)) null
                    else "Seu launcher não deixa adicionar por aqui. Segure num espaço vazio da tela inicial › Widgets › BDK Farm."
                },
                modifier = Modifier.padding(top = 8.dp),
            ) { Text("Adicionar à tela inicial") }
            msg?.let { Text(it, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp)) }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun DiscordOptionsCard(vm: FarmViewModel) {
    val info = vm.notifyInfo
    val o = info?.options

    var name by remember(o) { mutableStateOf(o?.username.orEmpty()) }
    var hour by remember(o) { mutableStateOf(o?.summaryHour ?: -1) }
    var mode by remember(o) { mutableStateOf(o?.mentionMode ?: "off") }
    var type by remember(o) { mutableStateOf(o?.mentionType ?: "user") }
    var id by remember(o) { mutableStateOf(o?.mentionId.orEmpty()) }
    var hourMenu by remember { mutableStateOf(false) }

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Discord: o que avisar", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            if (info == null) {
                Text("Carregando…", color = MaterialTheme.colorScheme.onSurfaceVariant)
                return@Column
            }
            if (!info.configured) {
                Text(
                    "Cole o webhook no card acima para os avisos chegarem.",
                    fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            DiscordEvents.forEach { (key, label) ->
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Checkbox(
                        checked = info.events[key] == true,
                        onCheckedChange = { vm.setNotifyEvent(key, it) },
                        enabled = !vm.busy,
                    )
                    Text(label, fontSize = 14.sp)
                }
            }
            Text(
                "Login recusado (farm parado) é sempre avisado.",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
            )

            OutlinedTextField(
                value = name, onValueChange = { if (it.length <= 32) name = it }, singleLine = true,
                label = { Text("Nome do bot") }, modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
            )

            Text("Resumo diário", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 12.dp))
            Box {
                OutlinedButton(onClick = { hourMenu = true }) {
                    Text(if (hour < 0) "Desligado" else "Todo dia às ${hour.toString().padStart(2, '0')}:00")
                }
                DropdownMenu(expanded = hourMenu, onDismissRequest = { hourMenu = false }) {
                    DropdownMenuItem(text = { Text("Desligado") }, onClick = { hour = -1; hourMenu = false })
                    (0..23).forEach { h ->
                        DropdownMenuItem(
                            text = { Text("${h.toString().padStart(2, '0')}:00") },
                            onClick = { hour = h; hourMenu = false },
                        )
                    }
                }
            }

            Text("Mencionar", fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 12.dp))
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf("off" to "Nunca", "critical" to "Só avisos vermelhos", "all" to "Sempre").forEach { (k, l) ->
                    FilterChip(selected = mode == k, onClick = { mode = k }, label = { Text(l) })
                }
            }
            if (mode != "off") {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("user" to "Usuário", "role" to "Cargo").forEach { (k, l) ->
                        FilterChip(selected = type == k, onClick = { type = k }, label = { Text(l) })
                    }
                }
                OutlinedTextField(
                    value = id, onValueChange = { v -> id = v.filter { it.isDigit() }.take(20) }, singleLine = true,
                    label = { Text(if (type == "role") "ID do cargo" else "ID do usuário") },
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.fillMaxWidth(),
                )
                Text(
                    "Discord › Configurações › Avançado › Modo desenvolvedor. Depois toque e segure no nome › Copiar ID.",
                    fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            vm.notifyMessage?.let { Text(it, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp)) }

            Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { vm.saveNotifyOptions(NotifyOptions(type, id, mode, name.trim(), hour)) },
                    enabled = !vm.busy,
                ) { Text("Salvar opções") }
                OutlinedButton(onClick = { vm.sendSummary() }, enabled = !vm.busy && info.configured) { Text("Resumo agora") }
            }
        }
    }
}
