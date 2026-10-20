package com.bdk.farm

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels

class MainActivity : ComponentActivity() {
    private val vm: FarmViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            FarmTheme {
                FarmApp(vm)
            }
        }
    }

    // Só atualiza enquanto o app está na tela (economiza bateria e dados).
    override fun onStart() {
        super.onStart()
        vm.startPolling()
    }

    override fun onStop() {
        super.onStop()
        vm.stopPolling()
    }
}
