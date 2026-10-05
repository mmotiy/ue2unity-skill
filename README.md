# ue2unity — UE 资产批量转换 Unity 的 ZCode/Claude 技能

把 Unreal Engine 资产包（市场包、工程 Content 目录）批量转换为 Unity 6 可直接导入的资源。

实测战绩：单个混存 5 种存储格式的市场包（963 网格 / 524 贴图 / 514 材质实例）达到
**98.2% 网格 + 96.4% 贴图**覆盖，材质绑定率 86-89%，交付 `Assets/` 目录 + `.unitypackage`。

## 这个技能解决什么问题

UE 市场包没有"导出 FBX"按钮，直接买来的资产 Unity 用不了。市面工具（umodel 2022
构建、FModel）都不支持 UE5 的未烹饪虚拟化格式。本技能自带一个自研导出器
（C# / CUE4Parse + 逆向的 UE5 格式解析 + Oodle P/Invoke），支持：

| 格式 | 说明 |
|------|------|
| UE4.25 内嵌 MeshDescription | 块状 zlib 压缩的源网格 |
| UE4.25 贴图源 | PNG 内嵌 / BC 原始块（多 mip 链） |
| **UE5 虚拟化网格** | `UStaticMeshDescriptionBulkData` 导出对象 → `FEditorBulkData` 头 → `FCompressedBuffer`（Oodle）→ UE5 名字键控 MeshDescription |
| **UE5 贴图尾部载荷** | 旧版压缩块格式（`0x9E2A83C1` + zlib 流序列） |
| 多 UV 通道网格 | `seg>1` 属性记录 |

附带完整材质管线：网格槽位 → 材质实例参数（含 UE4.25 `ParameterInfo.Index` 坑）
→ BaseColor/Normal/MetallicRoughness（含 UE `_M` 打包图 R/G/B 通道重排）/Occlusion
自动绑定进 glTF，Y-up/米制根变换内建，Unity glTFast 导入即用。

## 安装

```bash
git clone https://github.com/<you>/ue2unity-skill.git
# ZCode / Claude Code 用户级技能目录：
cp -r ue2unity-skill/ue2unity ~/.agents/skills/
```

## 使用

对 ZCode/Claude 说：

> 把 D:\Downloads\XXX City Pack 4.25-5.0.rar 转成 Unity 6 能用的资源

技能会自动执行：归档校验解压 → 构建导出器（自动下载 oo2core_9 dll）→ 抽样验收 →
全量导出 → 失败分析迭代 → 组装 `Assets/` + `.unitypackage` + 使用说明。

## 目录结构

```
ue2unity/
├── SKILL.md                      技能主文档（工作流 + 触发条件）
├── references/
│   ├── formats.md                5 种存储格式的字节级布局（逆向成果）
│   └── troubleshooting.md        错误消息 → 原因 → 修法（实战踩坑全集）
├── tools/CityExporter/           C# 导出器源码（net10.0，CUE4Parse 1.2.2）
├── scripts/
│   ├── assemble_unity.sh         Unity 交付组装
│   ├── pack_unitypackage.py      .unitypackage 打包器（纯 Python）
│   └── monitor_transfer.sh       下载完成监控（多信号判定）
└── unity/
    ├── TextureImportPostprocessor.cs   贴图自动导入配置
    └── README_Unity6.template.md       交付说明模板
```

## 依赖

- Windows + .NET 10 SDK
- `oo2core_9_win64.dll`（首次构建时自动从
  [nw-tools](https://github.com/themixednuts/nw-tools) 下载；Epic 授权物故不入库）
- 7-Zip（系统版或 PeaZip 便携版）
- Python 3 + Pillow

## 逆向笔记

想了解 UE5 虚拟化格式怎么破的：`ue2unity/references/formats.md` 有完整字节布局，
包括三个在引擎源码里看不出来的坑（字符串含 null 计数、set 级元素数字段、通道
stride 字段）。

## License

MIT（CityExporter 部分依赖 CUE4Parse，遵循其 License；oo2core 归 Epic/Oodle 所有，
运行时下载、不分发）
