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
        PhoneNotify.createChannel(this)
        PhoneNotify.schedule(this)
        setContent {
            FarmTheme(vm.themeMode, vm.accent) {
                FarmApp(vm)
            }
        }
    }

    override fun onStart() {
        super.onStart()
        vm.startPolling()
    }

    override fun onStop() {
        super.onStop()
        vm.stopPolling()
    }
}
