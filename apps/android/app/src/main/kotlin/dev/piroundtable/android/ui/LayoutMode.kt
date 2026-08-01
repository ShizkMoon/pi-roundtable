package dev.piroundtable.android.ui

enum class LayoutMode {
    Compact,
    Medium,
    Expanded,
}

fun classifyWidthDp(widthDp: Int): LayoutMode = when {
    widthDp >= 840 -> LayoutMode.Expanded
    widthDp >= 600 -> LayoutMode.Medium
    else -> LayoutMode.Compact
}
