# CityPacks → Unity 使用说明

静态glTF/PNG转换资源；具体数量、失败项和验证范围见Report。

1. 隔离Unity项目安装官方 `com.unity.cloud.gltfast`。
2. 将完整 `CityPacks` 及meta放入Assets，或导入unitypackage；保留Meshes/Textures相对路径。
3. 等待编译/导入结束，再拖glTF到场景或组装Prefab。根变换为Y-up/米。

纹理按明确语义标记：颜色图sRGB，normal和数据图线性，均Texture2D。
本例glTFast6.20 Built-in直接读RGB XYZ法线，因此使用Default/linear，而不是UnityNormalMap的DXT5nm。其他shader/管线需检查消费端并重新验证；不要仅按引擎名称翻绿。
MASK基础色名字中含MASK也仍是颜色图，不能自动改线性。

更新现有资源时保留GUID；修改布局、meta、导出器后重新验证。已验证文件与代表样本GPU检查是不同范围，参考Report/Assembly.json与交付审查记录。

Blueprint、演示umap、粒子、复杂分层shader、假室内视差、折射和风动不等同于已迁移。
源解析失败清单保留在Report/export_errors.log；不把失败资产宣称为损坏，也不把当前示例数量推广到其他包。

参考案例v13：946模型、569图（505源+60ORM+4镂空合成），8网格仍未转换，标准glTF贴图绑定2002/2298。Unity6000.5.0f1 Built-in/glTFast6.20下18代表模型实际验收通过，非全946Unity渲染验收。
