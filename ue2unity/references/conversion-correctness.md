# 转换正确性：v13 根因、修复与实证

检索词：PolygonGroupID、Source PNG、BGRA8、OpacityMask、override、ORM、DXT5nm、gzip FNAME。

## 结构与材质几何

旧 `scene:[0]`、`images:["path.png"]` 能通过JSON解析，但不符合glTF schema。WriteGltf改为整数scene和uri对象，以官方validator验全部946模型。

旧primitive为每个材质槽重复整网格indices，索引虽合法却重叠绘制。旧格式实际读取 `{perimeterCount=0, PolygonGroupID}`，UE5使用Triangles.PolygonGroupIndex；按组收集三角形，为每组写独立index accessor，丢弃无引用材质，extras保留源槽/材质路径。八材质门片三角数162/6/26/8/16/6/2/288，五材质水塔是另一格式控制。Position是顶点键，UV/Normal是实例键，以viToVertex展开；这个索引修复在v7已存在，不重做。

## 面顺序与法线分别验证

glTF正面CCW，转换实际源winding。根节点-90°X/0.01为正行列式旋转缩放，不能代替winding修复。

NormalizeNormals归一化有效法线，缺失/零法线由三角形重建。CorrectOpposedCornerNormals只处理三个角均与面法线相反的面；必要时复制共享实例、翻局部法线，保持position/UV/材质槽。油漆桶16、手推车8、下水道盖2，共26面通过源对照。608182三角形中608106对齐、65混合平滑、7近垂直、4退化，全角反向0；不能将平均dot<0统一翻掉。

## 内部 Source PNG 的语义

ExtractTexturePng/RestoreSourcePng先读取Source.Format、SizeX/SizeY、bPNGCompressed及bulk路径。UE内部TSF_BGRA8 PNG保持原BGRA内存字节语义，标准PNG消费者会解释为RGBA，所以只在该边界交换R/B，G/A不变。G8及格式感知raw解码不重复换色。

实际502Source PNG=454BGRA8+48G8，另3raw路径。独立源bulk的RGBA/G/Alpha逐像素hash和尺寸是证据，不能用MR与原图相同替代源颜色验证。

旧bulk扫描最小载荷门槛漏掉小体积真实PNG，误选低mip或猜raw。FindLargestPng改为有界chunk长度/IEND检查并匹配Source尺寸；该扫描器本身不验证chunk CRC，最终PNG必须另外Pillow verify+完整decode。声明PNG却找不到完整Source图时明确失败。纠正Cracks2_M（2048）、ChairTable_M/Lamp_E/Water_Storage_Tank_BC（4096）和trafficLights_E04（4096且旧16777216像素全错）。V13保留已验564图字节，仅新增4合成和1ORM。

## 父材质、ORM和有效 override

ParseMiParams完整加载父材质并解包FScriptStruct/FLinearColor。override字段只有对应bOverride启用才生效，否则沿父链继承。

本包BaseMaterial实际R→AO、G→roughness、B→metallic，已符合glTF，CreateOrmCopy只做字节相同MR别名。Wet无实际metallic输入/MaterialAttributes连接，因此96槽metal=0，其他主材质metal保持源值。

本例base-property struct的bOverride_BlendMode=true但BlendMode未序列化，构造默认enum0是Opaque；按该结构已证明默认窄修，不把任意缺属性猜成Opaque。647源记录最终504Opaque/131Translucent/12Masked、未知0。其他显式未支持模式报错。

## 54 个 Masked 槽

源图链：MarkingRoad40、直接M_Leaves8、MI_Leaves_01四、Fence一、Bike一。ResolveBinding读取叶片父材质默认纹理、识别道路BC参数，并只接受已追图的mask profile。

四种mask为RGB灰度（所有像素R=G=B、A=255），与BC同尺寸和UV0。ComposeMaskBaseColor以Unpremul解码，不重采样：RGB=原纠正BC、Alpha=mask.R；非灰度/尺寸不等/未知图明确失败。源主材质clip=.3333，实例override=false就继承，不用glTF默认.5覆盖；MASK factor.alpha=1，避免无效VectorColorAlpha乘到遮罩。

Fence有效TwoSided与Bike父材质双面恢复；三源MI有效true override带来额外6Opaque双面，总20。实际GPU三图同相机对照（强制Opaque/实际MASK/纯棋盘）：Fence/Leaves/Leaf/Arrow/Bike几何内部透背景像素307015/23657/25530/16206/51463，保留面非零，queue2450/_ALPHATEST_ON/cutoff.3333均检查。

## Unity importer ↔ shader

不完整TextureImporter曾把PNG导成Cubemap；改用Unity6真实完整meta模板，textureShape2D/max4096。

glTFast6.20 Built-in读RGB XYZ，NormalMap/DXT5nm将X移Alpha，RGB约(1,.5,.5)导致受光异常。同PNG/同shader对照DefaultLinear恢复约(.49,.50,.996)，316302像素变化；最终23GPU法线保持XYZ。其他shader先查消费端，不推广为所有Unity法线都Default，也不按UE名称擅自翻绿。

CityPacksColor/NormalXYZ/LinearData标记优先文件名；MASK_BASECOLOR仍sRGB，normal/data线性。Nl非标准名通过显式标记。最终66纹理=23Color/23NormalXYZ/20LinearData，122非nullTexture2D引用准确。

## gzip FNAME 的静默导入失败

Python默认把输出名嵌gzip FNAME；Unity6000.5遇内嵌*.unitypackage时Completed却无资源。nativeExport/Import为正控制，644/777×*.unitypackage/archtemp.tar/无FNAME六格仅unitypackage名失败；POSIX/GNU×有/无GUID目录四格保留tar逐字节，仅去FNAME全部真实落地，原本无目标父目录也可创建。

最小修复是 `gzip.GzipFile(filename='', fileobj=output, mode='wb', mtime=0)`。不盲改tar magic/目录/权限。流式写asset，原样保留sidecar/GUID，meta不作为独立资源。完整CRC和磁盘字节核对之后还要ImportPackage实际AssetDatabase加载。

## 实测结果与限制

946官方error0；1647 tangent warning涉及919模型、296unused-object INFO涉及241模型。569PNG=505源+60ORM+4MASK，基础绑定2002/2298只是至少一种标准图绑定，不是shader完整保真。

成品6149389693B、SHA256 fa3ad7bfebf80eb62bb313dca561101fd855214e81c6cd0df855cb51d9b4b3e3；2463entries、旧2457GUID、hash/meta/CRC全验。18模型+66图+脚本的原GUID103entry抽样真实Unity导入/GPU/渲染通过，127图126字节一致，一图2像素最大1色阶差异。不是全946Unity渲染验收。

8失败保留：fence_03、ShowcaseSnap、HotDogStand、sidewalk_6x4_simple缺当前路径可恢复的面连接；RoofModularPiece越界、table溢出、sidewalk_6x4_tree/WoodPole属性形态未支持。不能仅凭当前解析失败宣称UE也不能渲染。复杂shader、风动、粒子、Blueprint、演示城市未完整迁移。

## 代码与证据入口

- tools/CityExporter/Program.cs：ParseMeshDescription、ExtractFromBulkExports、ParseMiParams、ExtractTexturePng、RestoreSourcePng、FindLargestPng、ResolveBinding、ComposeMaskBaseColor、CreateOrmCopy、NormalizeNormals、CorrectOpposedCornerNormals、WriteGltf。
- 本地交付Report/Exporter：源码diff、FreezeV13、源graph dump、像素合同与8失败表；Report/IndependentValidation/FinalV13_IndependentAcceptance.md和Report/UnityValidation/FinalPackageV13/VisualAcceptance.md给出全量与实际GPU范围。
- [glTF规范](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html)、[Epic Texture.cpp源码公开镜像](https://raw.githubusercontent.com/folgerwang/UnrealEngine/release/Engine/Source/Runtime/Engine/Private/Texture.cpp)、[Material.cpp](https://raw.githubusercontent.com/folgerwang/UnrealEngine/release/Engine/Source/Runtime/Engine/Private/Materials/Material.cpp)、[MaterialShared.cpp](https://raw.githubusercontent.com/folgerwang/UnrealEngine/release/Engine/Source/Runtime/Engine/Private/Materials/MaterialShared.cpp)。

skill/GitHub不包含商业模型、原贴图、Oodle DLL或多GB交付包。
