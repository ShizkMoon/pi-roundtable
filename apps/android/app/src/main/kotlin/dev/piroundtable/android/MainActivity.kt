package dev.piroundtable.android

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import dev.piroundtable.android.ui.RoundtableApp
import dev.piroundtable.android.ui.theme.PiRoundtableTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            PiRoundtableTheme {
                RoundtableApp()
            }
        }
    }
}
