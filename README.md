# ue2unity — UE 编辑器资产转换到 Unity

将未烹饪UE Content资源转换为静态glTF、PNG、材质映射和unitypackage。
支持已实测的UE4.25内嵌与UE5虚拟化格式；其他资源包先抽样验证。

本案例v13：946 glTF、946 BIN、569 PNG（505源+60 ORM+4 MASK），官方946模型error0。
Unity6000.5.0f1 Built-in/glTFast6.20下18代表模型实际导入、GPU及渲染通过；不是全部946的Unity渲染验收。
8源网格仍未转换，标准贴图槽绑定2002/2298；复杂shader、风动、Blueprint、粒子和演示城市未完整迁移。

## 安装

```bash
git clone https://github.com/mmotiy/ue2unity-skill.git
cp -r ue2unity-skill/ue2unity ~/.agents/skills/
```

依赖.NET10、Python3/Pillow、7-Zip及隔离Unity官方glTFast。
Oodle载荷需合法取得oo2core_9_win64.dll并自行放到tools/CityExporter；仓库不分发、不自动下载。
缺DLL仍可编译并跑PNG合成测试，实际Oodle解压才需要DLL。

## 使用

对Codex/ZCode/Claude提出“把这个UE资源包转成Unity”或调用 `$ue2unity`。
执行细节见 [SKILL.md](ue2unity/SKILL.md)：抽样、全量导出、独立验证、空目录组装、成品包真实导入、备份交付。

```powershell
dotnet build ue2unity/tools/CityExporter/CityExporter.csproj
python ue2unity/scripts/test_source_png.py ue2unity/tools/CityExporter/bin/Debug/net10.0/CityExporter.dll
python ue2unity/scripts/test_delivery.py
```

## 技术分析与文件

- [v13根因与修复分析](ue2unity/references/conversion-correctness.md)：材质组、面角法线、Source BGRA8、父材质/ORM、Masked、GPU法线及gzip FNAME控制实验。
- [formats.md](ue2unity/references/formats.md)：UE字节布局，包括修正后的 `{0, PolygonGroupID}`。
- [troubleshooting.md](ue2unity/references/troubleshooting.md)：错误定位与边界，不能将解析失败等同源损坏。
- `tools/CityExporter/`：自研CUE4Parse导出器及版本格式解析。
- `scripts/assemble_unity.py`：语义metadata、完整TextureImporter模板、GUID保留；bash入口转发。
- `scripts/pack_unitypackage.py`：流式打包、原样sidecar、稳定GUID、无gzip FNAME。
- `scripts/check_export.py`：可重复二进制/结构检查；官方validator与实际Unity/GPU验收另做。
- `unity/TextureImportPostprocessor.cs`：颜色/法线/数据图显式角色。本案例glTFast直接读RGB XYZ，使用Default/linear法线。

源码沿用原依赖版本；构建仍有既有编译/依赖警告，未宣称零警告或完成依赖升级。
不包含商业模型、原始贴图、多GB交付包及Oodle二进制。

## License

本仓库MIT；CUE4Parse及其他依赖遵循各自许可证。Oodle归其权利人所有，使用者自行取得合法运行时。
