package dev.piroundtable.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.VerticalDivider
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import kotlin.math.roundToInt

private enum class RoleStatus(val label: String) {
    Speaking("发言中"),
    Thinking("思考中"),
    Tool("使用工具"),
    Idle("等待"),
}

private data class RoleUi(
    val id: String,
    val name: String,
    val specialty: String,
    val status: RoleStatus,
    val color: Color,
)

private data class TimelineItemUi(
    val id: String,
    val roleName: String,
    val time: String,
    val text: String,
    val isStreaming: Boolean = false,
    val annotation: String? = null,
)

private val sampleRoles = listOf(
    RoleUi("system", "系统策划", "规则与长期循环", RoleStatus.Speaking, Color(0xFF6D5BD0)),
    RoleUi("numbers", "数值策划", "经济与成长曲线", RoleStatus.Thinking, Color(0xFF1C7C6D)),
    RoleUi("gameplay", "玩法策划", "机制与手感", RoleStatus.Tool, Color(0xFFC25B3C)),
    RoleUi("moderator", "主持人", "议程与共识", RoleStatus.Idle, Color(0xFF52677A)),
)

private val sampleTimeline = listOf(
    TimelineItemUi(
        "t1",
        "数值策划",
        "14:31",
        "第一阶奖励应同时解释免费价值和付费增量，否则玩家只会看到两个互相冲突的锚点。",
    ),
    TimelineItemUi(
        "t2",
        "系统策划",
        "14:32",
        "打断一下。先冻结价格讨论：Lv1 必须让玩家在购买通行证之前就感到‘已经值回解锁成本’，再谈额外奖励。",
        isStreaming = true,
        annotation = "已打断数值策划 · generation 12",
    ),
)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RoundtableApp(modifier: Modifier = Modifier) {
    Scaffold(
        modifier = modifier.fillMaxSize(),
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("通行证定价评审", maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            "4 个角色 · Runtime 在线",
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                },
                actions = {
                    Surface(
                        color = MaterialTheme.colorScheme.secondaryContainer,
                        shape = RoundedCornerShape(999.dp),
                        modifier = Modifier.padding(end = 12.dp),
                    ) {
                        Text("LIVE", modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp))
                    }
                },
            )
        },
    ) { contentPadding ->
        BoxWithConstraints(
            modifier = Modifier
                .fillMaxSize()
                .padding(contentPadding),
        ) {
            when (classifyWidthDp(maxWidth.value.roundToInt())) {
                LayoutMode.Compact -> CompactLayout()
                LayoutMode.Medium -> MediumLayout()
                LayoutMode.Expanded -> ExpandedLayout()
            }
        }
    }
}

@Composable
private fun CompactLayout() {
    Column(Modifier.fillMaxSize()) {
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            items(sampleRoles, key = { it.id }) { role -> RoleChip(role) }
        }
        HorizontalDivider()
        TimelinePanel(Modifier.weight(1f))
    }
}

@Composable
private fun MediumLayout() {
    Row(Modifier.fillMaxSize()) {
        RolePanel(Modifier.width(224.dp).fillMaxHeight())
        VerticalDivider()
        TimelinePanel(Modifier.weight(1f))
    }
}

@Composable
private fun ExpandedLayout() {
    Row(Modifier.fillMaxSize()) {
        RolePanel(Modifier.width(264.dp).fillMaxHeight())
        VerticalDivider()
        TimelinePanel(Modifier.weight(1f))
        VerticalDivider()
        InspectorPanel(Modifier.width(320.dp).fillMaxHeight())
    }
}

@Composable
private fun RolePanel(modifier: Modifier = Modifier) {
    Column(modifier.padding(16.dp)) {
        Text("圆桌角色", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(12.dp))
        LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            items(sampleRoles, key = { it.id }) { role ->
                Card {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Surface(color = role.color, shape = CircleShape, modifier = Modifier.size(12.dp)) {}
                        Column(Modifier.weight(1f)) {
                            Text(role.name, fontWeight = FontWeight.Medium)
                            Text(
                                role.specialty,
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        Text(role.status.label, style = MaterialTheme.typography.labelSmall)
                    }
                }
            }
        }
    }
}

@Composable
private fun RoleChip(role: RoleUi) {
    Surface(
        color = if (role.status == RoleStatus.Speaking) {
            MaterialTheme.colorScheme.primaryContainer
        } else {
            MaterialTheme.colorScheme.surfaceContainer
        },
        shape = RoundedCornerShape(999.dp),
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Surface(color = role.color, shape = CircleShape, modifier = Modifier.size(9.dp)) {}
            Text(role.name, style = MaterialTheme.typography.labelLarge)
        }
    }
}

@Composable
private fun TimelinePanel(modifier: Modifier = Modifier) {
    var prompt by remember { mutableStateOf("") }
    Column(modifier.background(MaterialTheme.colorScheme.surface)) {
        LazyColumn(
            modifier = Modifier.weight(1f),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            item {
                Text(
                    "实时交流",
                    style = MaterialTheme.typography.titleLarge,
                    fontWeight = FontWeight.SemiBold,
                )
            }
            items(sampleTimeline, key = { it.id }) { item -> TimelineCard(item) }
        }
        HorizontalDivider()
        Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedTextField(
                value = prompt,
                onValueChange = { prompt = it },
                modifier = Modifier.weight(1f),
                placeholder = { Text("给主持人补充约束…") },
                maxLines = 3,
            )
            Button(onClick = {}, enabled = prompt.isNotBlank()) { Text("发送") }
        }
    }
}

@Composable
private fun TimelineCard(item: TimelineItemUi) {
    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(item.roleName, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                Text(item.time, style = MaterialTheme.typography.labelMedium)
            }
            Spacer(Modifier.height(8.dp))
            Text(item.text, style = MaterialTheme.typography.bodyLarge)
            item.annotation?.let { annotation ->
                Spacer(Modifier.height(10.dp))
                Text(
                    annotation,
                    style = MaterialTheme.typography.labelMedium,
                    color = MaterialTheme.colorScheme.primary,
                )
            }
            if (item.isStreaming) {
                Spacer(Modifier.height(12.dp))
                FilledTonalButton(onClick = {}) { Text("打断当前角色") }
            }
        }
    }
}

@Composable
private fun InspectorPanel(modifier: Modifier = Modifier) {
    Column(modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        Text("会议状态", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        StatusRow("Runtime Owner", "Windows · 本机")
        StatusRow("代次", "12")
        StatusRow("事件游标", "184")
        StatusRow("同步", "SSE 已连接")
        HorizontalDivider()
        Text("工具与 SubAgent", style = MaterialTheme.typography.titleSmall)
        Text(
            "玩法策划正在调用 economy-simulator；1 个 SubAgent 在核验竞品数据。",
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun StatusRow(label: String, value: String) {
    Column {
        Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
    }
}

@Preview(widthDp = 390, heightDp = 844, showBackground = true)
@Preview(widthDp = 1280, heightDp = 800, showBackground = true)
@Composable
private fun RoundtablePreview() {
    dev.piroundtable.android.ui.theme.PiRoundtableTheme { RoundtableApp() }
}
