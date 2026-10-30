package com.bdk.farm

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import java.text.DateFormat
import java.util.Date
import java.util.Locale

@Composable
fun AchievementsTab(vm: FarmViewModel) {
    val game = vm.achGame
    if (game != null) {
        BackHandler { vm.closeAchievements() }
        AchievementDetail(vm, game)
    } else {
        AchievementHome(vm)
    }
}

@Composable
private fun AchievementHome(vm: FarmViewModel) {
    var query by rememberSaveable { mutableStateOf("") }
    val farmed = vm.status?.games.orEmpty()

    LaunchedEffect(Unit) {
        vm.loadAi()
        vm.loadAchLibrary()
    }

    val q = query.trim().lowercase()
    val found = if (q.length < 2) emptyList() else vm.achLibrary.orEmpty().filter { it.name.lowercase().contains(q) }.take(8)

    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item { Text("Conquistas", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold) }

        vm.achError?.let { msg ->
            item {
                Card(colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.error.copy(alpha = 0.12f))) {
                    Text(msg, Modifier.padding(12.dp), color = MaterialTheme.colorScheme.error)
                }
            }
        }

        if (farmed.isNotEmpty()) {
            item { Text("Jogos do farm", style = MaterialTheme.typography.titleMedium) }
            items(farmed, key = { it.appId }) { g ->
                Card(colors = cardColors()) {
                    Row(Modifier.padding(8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Cover(g.appId, Modifier.width(92.dp).height(35.dp))
                        Spacer(Modifier.width(12.dp))
                        Text(g.name, Modifier.weight(1f), fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        TextButton(onClick = { vm.openAchievements(OwnedGame(g.appId, g.name, g.steamMinutes ?: 0)) }) { Text("Ver") }
                    }
                }
            }
        }

        item {
            Card(colors = cardColors()) {
                Column(Modifier.padding(16.dp)) {
                    Text("Outro jogo da biblioteca", fontWeight = FontWeight.SemiBold)
                    OutlinedTextField(
                        value = query, onValueChange = { query = it }, singleLine = true,
                        label = { Text("Buscar pelo nome") }, modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                    )
                    if (q.length >= 2 && found.isEmpty()) {
                        Text(
                            if (vm.achLibrary == null) "Biblioteca indisponível: cole a chave da Steam em Escolher jogos."
                            else "Nenhum jogo com esse nome.",
                            fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.padding(top = 8.dp),
                        )
                    }
                    found.forEach { g ->
                        TextButton(onClick = { vm.openAchievements(g) }, modifier = Modifier.fillMaxWidth()) {
                            Text(g.name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(fmtHours(g.minutes * 60), fontSize = 12.sp)
                        }
                    }
                    Text(
                        "Precisa da chave da Web API da Steam e dos detalhes de jogos do perfil como Público.",
                        fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 8.dp),
                    )
                }
            }
        }

        item { AiCard(vm) }
    }
}

@Composable
private fun AiCard(vm: FarmViewModel) {
    var key by remember { mutableStateOf("") }
    val on = vm.ai?.configured == true

    Card(colors = cardColors()) {
        Column(Modifier.padding(16.dp)) {
            Text("Ajuda da IA", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
            Text(
                when {
                    vm.ai == null -> "Carregando…"
                    on -> "IA ligada (modelo ${vm.ai?.model})."
                    else -> "IA desligada. Cole uma chave para ligar."
                },
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            OutlinedTextField(
                value = key, onValueChange = { key = it }, singleLine = true,
                label = { Text("Chave da API da Anthropic (sk-ant-…)") },
                visualTransformation = PasswordVisualTransformation(),
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
            )
            Text(
                "Crie em console.anthropic.com. Fica criptografada na VPS e cada pergunta custa centavos. " +
                    "Só o nome do jogo, o texto da conquista e a sua pergunta são enviados. A IA pode errar.",
                fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 6.dp),
            )
            Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(onClick = { vm.saveAiKey(key); key = "" }, enabled = !vm.busy && key.isNotBlank()) { Text("Salvar") }
                OutlinedButton(onClick = { vm.saveAiKey("") }, enabled = !vm.busy && on) { Text("Remover") }
            }
            vm.aiMessage?.let {
                Text(it, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp))
            }
        }
    }
}

private fun rarity(p: Double?): String {
    if (p == null) return ""
    val text = String.format(Locale("pt", "BR"), "%.1f%% dos jogadores", p)
    return when {
        p < 5 -> "Raríssima · $text"
        p < 15 -> "Rara · $text"
        else -> text
    }
}

@Composable
private fun AchievementDetail(vm: FarmViewModel, game: OwnedGame) {
    var filter by rememberSaveable { mutableStateOf(0) }
    val list = vm.achievements
    val aiOn = vm.ai?.configured == true
    val dateFmt = remember { DateFormat.getDateInstance(DateFormat.SHORT) }

    LazyColumn(
        Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { vm.closeAchievements() }) { Text("Voltar") }
                Text(
                    list?.name?.ifBlank { null } ?: game.name, Modifier.weight(1f),
                    style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold,
                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                )
                TextButton(onClick = { vm.openAchievements(game, refresh = true) }, enabled = !vm.achLoading) { Text("Atualizar") }
            }
        }

        vm.achError?.let { msg ->
            item { Text(msg, color = MaterialTheme.colorScheme.error) }
        }

        if (list == null) {
            if (vm.achError == null) item { EmptyNote("Carregando…") }
            return@LazyColumn
        }

        if (list.error != null) {
            item { Text(list.error, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            return@LazyColumn
        }

        item {
            Column {
                Text("${list.unlocked}/${list.total} desbloqueadas", fontWeight = FontWeight.SemiBold)
                LinearProgressIndicator(
                    progress = { if (list.total == 0) 0f else list.unlocked.toFloat() / list.total },
                    modifier = Modifier.fillMaxWidth().padding(top = 6.dp),
                )
                Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("Faltam", "Desbloqueadas", "Todas").forEachIndexed { i, label ->
                        FilterChip(selected = filter == i, onClick = { filter = i }, label = { Text(label) })
                    }
                }
                if (!aiOn) {
                    Text(
                        "Quer dicas de como conseguir cada conquista? Ative a ajuda da IA na lista de jogos.",
                        fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(top = 8.dp),
                    )
                }
            }
        }

        val shown = list.items
            .filter { filter == 2 || (filter == 1) == it.achieved }
            .let { l -> if (filter == 1) l.sortedByDescending { it.unlockTime } else l.sortedByDescending { it.percent ?: -1.0 } }

        if (shown.isEmpty()) {
            item { EmptyNote(if (filter == 1) "Nenhuma conquista desbloqueada ainda." else "Nada faltando. Parabéns!") }
        }

        items(shown, key = { it.id }) { a ->
            AchievementRow(vm, game.appId, a, aiOn, dateFmt)
        }
    }
}

@Composable
private fun AchievementRow(vm: FarmViewModel, appId: Long, a: Achievement, aiOn: Boolean, dateFmt: DateFormat) {
    var asking by rememberSaveable(a.id) { mutableStateOf(false) }
    var question by rememberSaveable(a.id) { mutableStateOf("") }
    val answer = vm.aiAnswers[a.id]
    val loading = vm.aiLoading[a.id] == true

    Card(colors = cardColors()) {
        Column(Modifier.padding(12.dp)) {
            Row(verticalAlignment = Alignment.Top) {
                Text(if (a.achieved) "✓" else "🔒", Modifier.width(28.dp), color = if (a.achieved) Color(0xFF16A34A) else MaterialTheme.colorScheme.onSurfaceVariant)
                Column(Modifier.weight(1f)) {
                    Text(a.name, fontWeight = FontWeight.SemiBold)
                    val desc = a.description.ifBlank { if (a.hidden && !a.achieved) "Conquista oculta" else "" }
                    if (desc.isNotBlank()) {
                        Text(desc, fontSize = 13.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    val meta = buildString {
                        if (a.achieved) {
                            append("Desbloqueada")
                            if (a.unlockTime > 0) append(" em ").append(dateFmt.format(Date(a.unlockTime * 1000)))
                            val r = rarity(a.percent)
                            if (r.isNotEmpty()) append(" · ").append(r)
                        } else append(rarity(a.percent))
                    }
                    if (meta.isNotEmpty()) {
                        Text(meta, fontSize = 12.sp, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 4.dp))
                    }
                }
            }

            if (aiOn) {
                TextButton(
                    onClick = {
                        asking = !asking
                        if (asking && answer == null && !loading) vm.askAi(appId, a.id, question)
                    },
                ) { Text(if (a.achieved) "Perguntar à IA" else "Como conseguir?") }

                if (asking) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedTextField(
                            value = question, onValueChange = { if (it.length <= 300) question = it },
                            singleLine = true, label = { Text("Pergunta (opcional)") }, modifier = Modifier.weight(1f),
                        )
                        Button(onClick = { vm.askAi(appId, a.id, question) }, enabled = !loading) { Text("Perguntar") }
                    }
                    if (loading) {
                        Text("Pensando…", fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
                    } else if (answer != null) {
                        Surface(
                            color = MaterialTheme.colorScheme.surfaceVariant, shape = MaterialTheme.shapes.small,
                            modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                        ) {
                            Text(answer, Modifier.padding(10.dp), fontSize = 14.sp)
                        }
                    }
                }
            }
        }
    }
}
