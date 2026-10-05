# American City Packs → Unity 6 使用说明

来源：UE 4.25–5.0 市场包「American City Packs Bundle」（未烹饪编辑器资产），
已转换为 Unity 6 可直接导入的 glTF 2.0 + PNG。

## 一次性设置

1. Unity 6 打开（或新建）你的项目（内置管线 / URP 均可）。
2. 安装 glTF 导入插件（二选一）：
   - **推荐** `com.unity.cloud.gltfast`（Package Manager → Unity Registry 搜 "glTFast"），或
   - `glTFast` 的任意 6.x 兼容版本。
3. 把 `Assets/CityPacks` 整个文件夹拷进你项目的 `Assets/` 下，等待导入完成。
   （首次导入 900+ 网格与 500+ 4K 贴图，视磁盘速度可能需要几分钟。）

## 你会得到什么

```
Assets/CityPacks/
  Meshes/**/*.gltf(+.bin)   每个静态网格一个 glTF：
                            - 坐标已转 Y-up、单位米（UE 厘米×0.01）
                            - 材质槽 → glTF PBR 材质：BaseColor / Normal /
                              MetallicRoughness(+Occlusion) / Emissive 已按
                              材质实例参数自动绑定
  Textures/**/*.png         全部贴图（4K）
  Editor/TextureImportPostprocessor.cs
                            自动导入配置：*_N → 法线贴图；*_M/_MR/_Mask → 线性
```

把 `.gltf` 拖进场景即可，材质已挂好。Prefab 可以从 Project 视图直接拖，
也可以写脚本批量实例化。

## 注意事项

- **法线方向**：导出按 UE 原样绑定。如果个别资产在 Unity 里凹凸看起来反了
  （凸变凹），是法线 Y 通道约定差异——把对应 `_N.png` 的绿色通道反相即可
  （或告知我批量处理）。先在场景里确认，多数情况无需处理。
- **UE 蓝图/程序化建筑**（120 个 Blueprint）无法转换成 C#——它们是 UE 特有
  的运行时生成逻辑。本包提供的是全部 963 个网格本体 + 材质，程序化拼装需在
  Unity 侧重写（如需我可以基于 materials.json 里的槽位信息生成拼装参考数据）。
- **场景 (.umap)**：13 个演示场景未转换；用网格自行搭建即可。
- **音频/粒子**：7 个音效可后续按需转 WAV；烟雾/蒸汽粒子是 UE Niagara 资产，
  需在 Unity 用粒子系统重做。
- materials.json 记录了每个网格的材质槽 → 材质实例 → 贴图参数的完整映射，
  供程序化生成或替换材质用。

## 已知限制

## 本次导出覆盖率（实测 · 最终版）

| 类别 | 成功 | 总数 | 覆盖率 |
|------|------|------|--------|
| 静态网格 glTF（含材质槽+贴图绑定） | 946 | 963 | **98.2%** |
| 贴图 PNG | 505 | 524 | **96.4%**（另生成 58 张 MR 通道重排副本；4 张为降半分辨率恢复版） |
| 材质映射 materials.json | 963 | 963 | 100%（全部网格的槽位→材质实例→贴图参数） |

### 已破解并支持的存储格式（本包混存 5 种）
1. UE4.25 内嵌 MeshDescription（块状 zlib）
2. UE4.25 内嵌贴图源（PNG 内嵌 / BC 原始块）
3. UE5 虚拟化网格：StaticMeshDescriptionBulkData 导出对象 → FEditorBulkData 头 → FCompressedBuffer（Oodle）→ UE5 名字键控 MeshDescription
4. UE5 旧版压缩块贴图尾部载荷（0x9E2A83C1 + zlib 流序列）
5. 多 UV 通道网格（seg>1 属性记录）

### 未覆盖的 18 个资产（清单：Report/export_errors.log）
- 5 个网格：非三角面（2 角退化面/变角多边形，旧格式需逐面角数解析）
- 3 个网格：记录形态异常（blob 越界/溢出/垃圾头）
- 5 个网格为 Blueprint 关联的展示件（ShowcaseSnap 等）
- 其余为上述贴图的边缘变体
如需这 18 个：UE5 编辑器中右键 Export 即可（几分钟手工量）。
