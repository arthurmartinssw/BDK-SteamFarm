package com.bdk.farm

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.clickable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private const val MAX_GAMES = 32

/** Tela "Escolher jogos": marca os jogos da biblioteca que o farm deve rodar. */
@Composable
fun PickerScreen(vm: FarmViewModel) {
    val lib = vm.library
    val current = vm.status?.games.orEmpty()

    var picked by remember { mutableStateOf(current.map { it.appId }.toSet()) }
    var extraNames by remember { mutableStateOf(current.associate { it.appId to it.name }) }
    var query by rememberSaveable { mutableStateOf("") }
    var manual by rememberSaveable { mutableStateOf("") }
    var localMsg by remember { mutableStateOf<String?>(null) }

    BackHandler { vm.closePicker() }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        bottomBar = {
            Surface(color = MaterialTheme.colorScheme.surface) {
                Row(
                    Modifier.navigationBarsPadding().padding(12.dp).fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        "${picked.size}/$MAX_GAMES",
                        color = if (picked.size >= MAX_GAMES) Color(0xFFF59E0B) else MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.weight(1f),
                    )
                    OutlinedButton(onClick = { vm.closePicker() }) { Text("Cancelar") }
                    Button(
                        onClick = { vm.saveGames(picked.toList()) },
                        enabled = picked.isNotEmpty() && !vm.busy,
                    ) { Text("Salvar") }
                }
            }
        },
    ) { pad ->
        LazyColumn(
            Modifier.padding(pad).fillMaxSize().statusBarsPadding().imePadding(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            item { Text("Escolher jogos", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold) }

            (vm.pickerError ?: localMsg)?.let { msg ->
                item { Text(msg, color = MaterialTheme.colorScheme.error) }
            }

            if (lib == null) {
                item { EmptyNote("Carregando a biblioteca…") }
            } else {
                if (!lib.configured || (lib.error != null && !lib.available)) item { KeyForm(vm, lib) }

                if (lib.available) {
                    item {
                        OutlinedTextField(
                            value = query, onValueChange = { query = it }, singleLine = true,
                            label = { Text("Buscar entre ${lib.games.size} jogos") },
                            modifier = Modifier.fillMaxWidth(),
                        )
                    }
                }

                // jogos marcados que não estão na biblioteca (adicionados por AppID)
                val known = lib.games.map { it.appId }.toSet()
                val all = lib.games + picked.filter { it !in known }
                    .map { OwnedGame(it, extraNames[it] ?: "AppID $it", -1) }

                val q = query.trim().lowercase()
                val shown = all
                    .filter { q.isEmpty() || it.name.lowercase().contains(q) || it.appId.toString() == q }
                    .sortedWith(compareByDescending<OwnedGame> { it.appId in picked }.thenBy { it.name.lowercase() })

                items(shown.take(150), key = { it.appId }) { g ->
                    val checked = g.appId in picked
                    Row(
                        Modifier.fillMaxWidth().clickable {
                            localMsg = null
                            if (checked) picked = picked - g.appId
                            else if (picked.size >= MAX_GAMES) localMsg = "O máximo é $MAX_GAMES jogos ao mesmo tempo."
                            else picked = picked + g.appId
                        },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Checkbox(checked = checked, onCheckedChange = null)
                        Spacer(Modifier.width(10.dp))
                        Cover(g.appId, Modifier.width(70.dp).height(26.dp))
                        Spacer(Modifier.width(10.dp))
                        Column(Modifier.weight(1f).padding(vertical = 8.dp)) {
                            Text(g.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                if (g.minutes < 0) "AppID ${g.appId}" else "${fmtHours(g.minutes * 60)} na Steam",
                                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }

                if (shown.size > 150) item { EmptyNote("Mostrando 150 de ${shown.size}. Use a busca.") }
                else if (shown.isEmpty() && lib.available) item { EmptyNote("Nenhum jogo encontrado.") }

                item {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(
                            value = manual, onValueChange = { manual = it.filter(Char::isDigit) }, singleLine = true,
                            label = { Text("Adicionar por AppID") }, modifier = Modifier.weight(1f),
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                        )
                        Spacer(Modifier.width(8.dp))
                        OutlinedButton(onClick = {
                            val id = manual.toLongOrNull()
                            when {
                                id == null || id < 1 -> localMsg = "AppID inválido."
                                id !in picked && picked.size >= MAX_GAMES -> localMsg = "O máximo é $MAX_GAMES jogos."
                                else -> {
                                    picked = picked + id
                                    extraNames = extraNames + (id to (extraNames[id] ?: "AppID $id"))
                                    manual = ""
                                    localMsg = null
                                }
                            }
                        }) { Text("Adicionar") }
                    }
                }

                item {
                    Text(
                        "O farm só funciona com jogos que estão na sua biblioteca. Se a lista vier vazia, deixe os “detalhes dos jogos” do seu perfil da Steam como Públicos.",
                        fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

@Composable
private fun KeyForm(vm: FarmViewModel, lib: Library) {
    var key by remember { mutableStateOf("") }
    val uri = LocalUriHandler.current

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            if (!lib.configured) {
                Text(
                    "Para listar a sua biblioteca, o farm precisa de uma chave da Steam (grátis, 1 minuto). " +
                        "No site, escreva qualquer coisa no campo de domínio (ex.: localhost), aceite os termos e cole a chave abaixo. " +
                        "Ela fica guardada criptografada na VPS.",
                )
                TextButton(onClick = { uri.openUri("https://steamcommunity.com/dev/apikey") }) { Text("Abrir página da chave") }
            } else {
                Text(lib.error ?: "A biblioteca não carregou.")
            }
            OutlinedTextField(
                value = key, onValueChange = { key = it }, singleLine = true,
                label = { Text(if (lib.configured) "Trocar a chave da Steam" else "Chave da Steam") },
                modifier = Modifier.fillMaxWidth(),
                visualTransformation = PasswordVisualTransformation(),
            )
            Spacer(Modifier.height(8.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { vm.saveKey(key) }, enabled = key.isNotBlank() && !vm.busy) { Text("Salvar chave") }
                if (lib.configured) OutlinedButton(onClick = { vm.retryLibrary() }, enabled = !vm.busy) { Text("Tentar de novo") }
            }
        }
    }
}
