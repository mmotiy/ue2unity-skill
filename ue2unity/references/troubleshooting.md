# 故障排除手册（错误消息 → 原因 → 修法）

按 `export_errors.log` 里的错误消息检索。全部为实战踩坑记录。

## 网格类

### `SourceModel bulk data marked invalid`
UE5 存档。走 `ExtractFromBulkExports` 路径：查导出表里 class 为
`StaticMeshDescriptionBulkData` 的对象 → FEditorBulkData 头 → FCompressedBuffer
→ Oodle → UE5 MeshDescription。若工具已内建仍报此错，检查：
- 导出对象是否存在（`--dump <uasset>` 看 export map）
- flags 是否带 0x200（包拖车变体，本方案未实现，需读 FPackageTrailer）

### `RenderData fallback failed` / 大量内存异常
说明走了错误的回退扫描路径（早期版本的方案）。确认新路径生效后此错误不应出现；
若出现，优先修 `ExtractFromBulkExports` 而不是恢复扫描。

### `unexpected attribute record shape for TextureCoordinate: seg=2/4 count2==count`
多 UV 通道，正常变体。校验应为：`elemType != 6 && count2 != count` 才抛错；
seg>1 时数据仍是 count×stride（通道 0）。

### `non-triangle polygons (VI=N, polys=M)`
不能仅凭实例/多边形数量推断变角或源损坏。检查多边形perimeter count、三角属性、实例映射和独立BulkData。确认当前格式缺可恢复拓扑时记录解析限制；未知对齐变体继续定位。

### `blob overrun` / `OverflowException` / 垃圾数值的 record shape
记录对齐漂移。用 `--mdtest <file> <hexOffset>` 配合 hexdump 定位；
一般是重同步逻辑漏了一种填充模式。

### 蓝图（Blueprint）怎么办
**不转换**。蓝图是 UE 运行时逻辑（程序化拼楼等），Unity 侧需重写。
materials.json 里的槽位/参数映射是重建程序化逻辑的数据基础。

## 贴图类

### `no mip bulk payload found in file tail`
按序尝试：① 文件内扫 `0x9E2A83C1` 旧版压缩块（UE5 贴图尾部）；
② 导出区尾部 FEditorBulkData 头 + FCompressedBuffer；
③ 确认没有误走"assembled 已找到"分支（见下）。

### `assembled mip payload N < expected M for PF_*`
先检查Source.bPNGCompressed。PNG声明路径不可猜低mip；必须读取真实bulk完整PNG并匹配Source尺寸。仅已证明raw有效mip路径允许按实际尺寸解码并报告。

### `legacy chunk short: N/M`
.NET `ZLibStream.Read` 短读——必须循环 `while (got < need)` 读满。

### Oodle 解压返回 0 / 失败
DLL 版本太老。必须 `oo2core_9_win64.dll`（v7 解不动 UE5.1 载荷）。

### PNG 内嵌但找不到
检查真实Source bulk路径，不用固定最小payload门槛排除小PNG。FindLargestPng匹配Source尺寸和有界chunk/IEND，CRC另以完整PNG验证检查；最大PNG可能只是错误候选。见conversion-correctness。

## 材质绑定类

### 全部 glTF 材质绑定率为 0，但 materials.json 有贴图参数
路径错位：贴图落盘目录与 `ToPng` 解析目录不一致（内容根目录名导致前缀差异）。
修法：ToPng 同时尝试"带/不带顶层内容目录名"两个候选路径。

### 材质实例贴图参数全空
两原因之一：① 参数名读错字段（UE4.25 在 `ParameterInfo.Index`）；
② MI 对象是 `FPackageIndex.Load()` 的占位实例（无属性）——改用
provider 按后缀匹配完整加载包。

## 解压/归档类

### `Truncated data in huffman tables`
libarchive（tar.exe）的 RAR5 缺陷。改用 7-Zip（系统安装版或 PeaZip 便携版
`res/bin/7z/7z.exe`）。**tar -t 通过不代表 tar -x 可用**，必须换工具。

### 下载中的 .rar.### 文件
传输工具的分块临时名。完成信号 = 改名为 .rar（最强信号）或大小+mtime 冻结
≥10 分钟且 7z 校验通过（见 scripts/monitor_transfer.sh 的多信号判定）。

## Unity 侧

- glTF 导入：Package Manager → `com.unity.cloud.gltfast`
- 法线受光异常 → 先查Source通道，再查shader读取XYZ还是UnityNormalMap/DXT5nm，不按引擎名直接翻绿。本例glTFast6.20 Built-in使用Default/linear。
- 色彩空间按实际glTF绑定及显式meta角色；MASK_BASECOLOR是sRGB颜色图，普通mask/ORM为线性数据。
- 拖 .gltf 进场景即可，材质已挂；prefab 从 Project 视图直接拖

## 补充（v7 实战）

### 全量导出后约一半网格 max(index) ≥ POSITION count
旧格式 position 顶点键 vs 索引实例键错位。修法：viToVertex 展开位置数组。
交付前必检：遍历全部 glTF 验证 `max(indices) < POSITION count`。

### `non-triangle topology (VI=N, polys=M): no face connectivity in source`
查看所有有效拓扑路径。上述特征说明本工具三连fallback不适用，当前源连接不可恢复时保留失败；不能扩大结论为所有解析路径或源UE渲染都失败。

## v13 正确性问题

- 各材质反复覆盖整模型：核对perimeterCount在前、groupID在后，并为每primitive分区indices；索引范围正确仍可能重复绘制。
- 红蓝颠倒/金属度异常：真实Source PNG BGRA8恢复R/B与shader ORM通道解释是两层问题；本包ORM为R=AO/G=roughness/B=metallic，不能固定重排。
- 叶片/栅栏/轮辐成为整面：追父链BlendMode和有效override，确认OpacityMask输入通道、UV与clip；本案例四灰度mask以R合成Alpha，MASK cutoff.3333/factorA1。
- PNG被导成Cubemap：使用完整真实TextureImporter meta并指定Texture2D。
- ImportPackage回调完成却没有资源：检查gzip FNAME。本次Unity6000.5中*.unitypackage内嵌名导致silent skip；禁用FNAME最小修复，tar布局无需猜改。
- 更新后引用断开：保留现有sidecar GUID，meta不能作为资源被单独打包；新资源稳定UUID5。

具体源字段、代码方法、控制实验及验证范围见 [conversion-correctness.md](conversion-correctness.md)。
