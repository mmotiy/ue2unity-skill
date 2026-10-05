---
name: ue2unity
description: 将 Unreal Engine 未烹饪编辑器资产（Content目录、.uasset/.umap、市场资源包）转换为 Unity 的静态glTF、PNG和unitypackage。用于UE到Unity的资产提取、迁移、导入错误及材质失真诊断；不把Blueprint、粒子或复杂UE shader宣称为已完整迁移。
---

# UE 编辑器资产 → Unity

提供 CUE4Parse/.NET 导出器、Unity importer 和交付脚本。支持已实测的 UE4.25 内嵌及 UE5 虚拟化变体；其他包先抽样验证。遇到已有交付先核对版本、文件哈希和当前故障，不重复修复过时报告中的问题。

## 环境

- .NET10 SDK、Python3、Pillow；官方Khronos glTF Validator用于全量规范检查。
- UE5 Oodle需要合法取得的 `oo2core_9_win64.dll`，自行置于 `tools/CityExporter/`；仓库不分发，不宣称自动下载。
- RAR用7-Zip校验和解压。本案例Windows libarchive/RAR5解码曾出错，参考troubleshooting。
- 隔离Unity项目安装官方 `com.unity.cloud.gltfast`。本次实测Unity6000.5.0f1、glTFast6.20.0、Built-in/D3D11；其他版本/管线需复验。

## 工作流

1. 校验归档并解压到有足够空间的独立目录，保留已有工程和交付。
2. `dotnet build tools/CityExporter/CityExporter.csproj`；查 [formats.md](references/formats.md) 选择真实源路径，不按包名猜版本。
3. 抽样覆盖旧格式、多材质UE5网格、法线/灰度/颜色图、透明和镂空材质；核对schema、索引、材质组和源像素。
4. 全量导出到新目录：

   ```powershell
   dotnet tools/CityExporter/bin/Debug/net10.0/CityExporter.dll --content SOURCE_CONTENT --out NEW_EXPORT --meshes --textures --materials
   ```

5. 按 [troubleshooting.md](references/troubleshooting.md) 定位失败。修代码后先针对根因回归，再全量检查；失败率低不能替代正确性，`VI != 3*polys`也不能单独证明源损坏。
6. 组装到空候选目录，更新已有交付时保留旧GUID：

   ```powershell
   python scripts/assemble_unity.py NEW_EXPORT NEW_DELIVERY --previous-assets OLD_DELIVERY/Assets/CityPacks
   python scripts/pack_unitypackage.py NEW_DELIVERY/Assets/CityPacks NEW_DELIVERY/CityPacks_Unity6.unitypackage
   ```

   首次交付省略 `--previous-assets`。bash入口转发到Python。
7. 全量检查官方schema、accessor/count/minmax、索引范围、finite/unitnormal、URI、材质三角形分区、PNG CRC/Source尺寸及像素合同；冻结哈希。负dot区分全角反向、混合平滑、近零和退化，不整批翻面。
8. 隔离Unity实际导入代表模型，核对Texture2D、shader、语义色彩空间、GPU法线、透明/镂空和原材质截图。使用实际成品包的原GUID/asset/meta抽样，等待ImportPackage落地、AssetDatabase加载和编译完成；不能只看回调或exit0。
9. 整包对照磁盘asset/meta/path/GUID，读完gzip校验CRC；备份旧交付后替换。报告区分全量文件检查与Unity抽样，并列出未转换项和shader近似。

## 易错合同

- 本案例旧多边形记录为 `{empty perimeter count=0, PolygonGroupID}`，组号在第二int。Position按顶点，UV/Normal按实例，索引引用实例，需viToVertex展开。
- 每材质primitive只用自己的三角形，不能重复整网格indices；glTF scene为整数，images为对象数组。
- 真实BGRA8 Source PNG在源读取边界恢复R/B、保留G/A；raw解码和G8不重复换色。声明PNG时匹配Source尺寸和完整载荷，不猜低mip。
- ORM从实际shader图解释。本案例R=AO/G=roughness/B=metallic，MR是字节相同别名，不能对所有UE `_M` 固定重排。
- 材质沿父链继承；override字段存在不代表启用。Masked追实际OpacityMask通道、UV、阈值、双面，未知图显式报错。
- 本案例glTFast读RGB XYZ法线，使用Default/linear；UnityNormalMap的DXT5nm重打包不匹配。颜色、法线、数据图以meta的 `CityPacksColor/CityPacksNormalXYZ/CityPacksLinearData` 为准，不能仅凭MASK文件名设置线性。
- Python gzip禁用可选FNAME，避免Unity6000.5静默跳过内嵌名为unitypackage的载荷；保留sidecar/GUID，不将meta作为独立资源打包。

## 按需资源

- [formats.md](references/formats.md)：字节布局与变体。
- [troubleshooting.md](references/troubleshooting.md)：错误与定位步骤。
- [conversion-correctness.md](references/conversion-correctness.md)：v13根因、源合同、代码位置及实测证据；修材质/颜色/法线/包时读取。
- `scripts/check_export.py`：可重复二进制/结构检查，不替代官方validator或GPU验收。
- `scripts/test_source_png.py`：无商业素材的源PNG通道/尺寸/完整性回归。
- `scripts/test_delivery.py`：组装语义metadata、GUID、gzip和sidecar回归。

本案例v13为946模型、569PNG（505源+60ORM+4MASK），全946官方error0，18模型实际Unity验收通过；仍有8解析失败、65混合法线诊断，基础贴图槽覆盖2002/2298。这些案例数字不是其他包的预期值。
