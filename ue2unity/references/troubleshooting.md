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
变角多边形（2 角退化面等）。需要旧格式的逐面角数解析，成本高收益低，
通常直接记录放弃（市场包中占比 <1%）。

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
手里是低分辨率 mip。按 scale=1,2,4,8 降分辨率解码（N≈M/4 → 2048²）。

### `legacy chunk short: N/M`
.NET `ZLibStream.Read` 短读——必须循环 `while (got < need)` 读满。

### Oodle 解压返回 0 / 失败
DLL 版本太老。必须 `oo2core_9_win64.dll`（v7 解不动 UE5.1 载荷）。

### PNG 内嵌但找不到
`FindLargestPng` 的扫描起点用 `exp.SerialOffset`；跨多个 PNG 时取最大那个。

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
- 法线凹凸相反 → 对应 `_N.png` 翻绿通道（大概率不需要）
- `_M/_MR/_Mask` 类贴图必须线性空间（Editor 脚本已自动处理）
- 拖 .gltf 进场景即可，材质已挂；prefab 从 Project 视图直接拖
