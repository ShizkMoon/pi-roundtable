package dev.piroundtable.android.ui

import org.junit.Assert.assertEquals
import org.junit.Test

class LayoutModeTest {
    @Test
    fun widthBreakpointsCoverPhoneAndTabletLayouts() {
        assertEquals(LayoutMode.Compact, classifyWidthDp(599))
        assertEquals(LayoutMode.Medium, classifyWidthDp(600))
        assertEquals(LayoutMode.Medium, classifyWidthDp(839))
        assertEquals(LayoutMode.Expanded, classifyWidthDp(840))
    }
}
