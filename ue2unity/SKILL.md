---
name: ue2unity
description: 把 Unreal Engine 资产包（市场包/工程目录/.uasset/.umap）批量转换为 Unity 可用资源（glTF 2.0 + PNG + 材质绑定 + .unitypackage）。支持 UE4.25 内嵌格式与 UE5 虚拟化格式（StaticMeshDescriptionBulkData/FEditorBulkData/FCompressedBuffer/Oodle）、多 UV、多 mip 贴图变体。当用户要求把 UE 资源/资产/模型/贴图转成 Unity、导入 Unity、迁移 UE 工程到 Unity、或给出 .uasset/.umap/UE marketplace 包需要提取时使用——即使用户只说"转成 Unity"或"这个包 Unity 能用吗"也应触发。
---

# UE 资产 → Unity 转换技能

把一个 UE 资产目录（典型：Epic 商城包解压后的 `Content/<PackName>/`）批量转换为
Unity 6 可直接导入的 **glTF 2.0 + PNG**，含材质槽→材质实例→贴图参数的完整绑定，
并组装成 `Assets/` 目录与 `.unitypackage`。

实测：单个市场包（963 网格/524 贴图/混存 5 种存储格式）达到 98% 网格 + 96% 贴图覆盖。

## 环境要求（Windows）

| 依赖 | 获取方式 |
|------|----------|
| .NET 10 SDK | `dotnet --list-sdks` 检查；缺则 winget/dotnet.com 安装 |
| **oo2core_9_win64.dll**（UE5 格式必需） | `curl -LO https://raw.githubusercontent.com/themixednuts/nw-tools/main/resources/oodle/oo2core_9_win64.dll`，放到 `tools/CityExporter/` 下。版本必须是 9（oo2core_7 解不动 UE5.1 载荷）。Epic 授权物，不入库 |
| 7-Zip 引擎（RAR 用） | 系统 7z 或 PeaZip 便携版内置引擎：`res/bin/7z/7z.exe` |
| Python + Pillow | 打 .unitypackage 用 |

## 工作流（严格按序）

### 1. 归档验证与解压
- 用 7z 先 `t` 校验再 `x` 解压。**禁用 libarchive（tar.exe）解 RAR5**——它的
  RAR5 解码器有缺陷，会报"Truncated data in huffman tables"假错并产出半截文件。
- 解压目标选剩余空间 ≥ 归档 2 倍的盘。

### 2. 构建导出器
```
cd tools/CityExporter
cp <oo2core_9_win64.dll> .
dotnet build
```

### 3. 抽样验收（先小后大，勿直接全量）
从解压目录抽 2-3 个网格 + 2-3 张贴图（含不同子目录）复制到临时目录，跑：
```
dotnet bin/Debug/net10.0/CityExporter.dll --content <样本目录> --out <out> --meshes --textures
```
检查：`[mesh]`/`[texture]` 行的数量、三角形数/顶点数 > 0、PNG 尺寸正确。
抽出的 glTF 用 python json.load 验证合法、images URI 指向的文件存在。

### 4. 全量导出
```
dotnet bin/Debug/net10.0/CityExporter.dll --content <Content/PackName> --out <out> --meshes --textures --materials
```
产出：`out/Meshes/**/*.gltf(+.bin)`、`out/Textures/**/*.png`、`out/materials.json`、
`out/export_errors.log`。

### 5. 分析失败清单并迭代
读 `export_errors.log`，按 `references/troubleshooting.md` 对照修复（该文件收录了
本项目踩过的全部坑及修法）。修复后只对失败子集回归，再全量重跑。
失败率 < 2% 或剩余为退化几何时停止迭代。

### 6. 组装 Unity 交付
```
bash scripts/assemble_unity.sh <导出目录> <输出目录>
```
产出 `Assets/CityPacks/{Meshes,Textures,Editor}` + `CityPacks_Unity6.unitypackage` +
`Report/{materials.json,export_errors.log}`。使用说明模板在 `unity/README_Unity6.template.md`。

### 7. 最终验证（必做）
- 随机抽 ≥60 个 glTF：JSON 合法、images 的相对 URI 全部存在（0 断链）、
  材质绑定率抽样统计
- 抽 1 个网格核对包围盒数值是否符合物体常识（如桶 ≈ ±30cm×100cm）
- `.unitypackage` 用 tar -tzf 验证结构（每文件 asset/asset.meta/pathname 三件套）

## 输出契约（给用户交付时）

| 项 | 内容 |
|----|------|
| `Assets/<Pack>/` | Meshes（glTF+bin，Y-up/米制根节点变换已内建）、Textures、Editor/TextureImportPostprocessor.cs |
| `.unitypackage` | 一键导入包 |
| Report | materials.json（全量槽位→材质→贴图映射）+ export_errors.log |
| README | 安装步骤（glTFast 包）+ 覆盖率表 + 缺口补齐路径 |

## 关键技术事实（为什么这样做）

1. **一个市场包可能混存多个引擎版本的存档格式**（如 "4.25-5.0" 包里 UE4.25 与
   UE5 存档各半）。必须双路径都支持，不能按包名猜单一版本。
2. 未烹饪 .uasset 的网格几何**不在渲染数据里**（uncooked 不序列化 RenderData），
   在 SourceModel 的 MeshDescription 或（UE5）独立的 BulkData 导出对象里。
3. UE5 虚拟化链路：`UStaticMeshDescriptionBulkData` 导出（88 字节
   `FEditorBulkData` 头）→ `FCompressedBuffer`（大端，Oodle 压缩）→
   UE5 名字键控 MeshDescription。详见 `references/formats.md`。
4. 法线/金属度约定：UE 与 glTF 同为 OpenGL 约定，直接绑定即可；UE 打包图
   `_M` 为 R=金属 G=粗糙 B=AO，需重排为 glTF 的 G=粗糙 B=金属（工具已内建
   `_MR.png` 生成）。个别资产凹凸视觉相反时在 Unity 侧翻绿通道。
5. glTF 根节点带 `-90° X 旋转 + 0.01 缩放`，UE Z-up/厘米 → glTF Y-up/米，
   Unity glTFast 导入即正确，无需手工调。

## 工具清单

| 文件 | 用途 |
|------|------|
| `tools/CityExporter/` | C# 导出器（CUE4Parse + 自研 UE5 格式解析 + Oodle P/Invoke） |
| `scripts/assemble_unity.sh` | 组装 Assets 目录 + .unitypackage |
| `scripts/pack_unitypackage.py` | .unitypackage 打包器（纯 python/tar） |
| `scripts/monitor_transfer.sh` | 下载完成监控（改名事件 + 大小冻结 + CRC 校验） |
| `unity/TextureImportPostprocessor.cs` | Unity 自动导入配置（法线/线性空间） |
| `unity/README_Unity6.template.md` | 交付说明模板 |

CityExporter 调试模式：`--dump <uasset>` 打印导出表/属性、`--mdtest <file> <hexOff>`
试解析 MeshDescription——逆向新格式时的主力工具。

## 参考文档（按需读取）

- 遇到导出失败/新格式变体 → 读 `references/troubleshooting.md`
- 需要字节级格式细节（头布局、魔数、通道表） → 读 `references/formats.md`
