# UE 未烹饪资产存储格式速查（本项目逆向成果）

全部布局已在真实市场包上字节级验证。LE=小端，BE=大端。
"dump-verified" 指用 `--dump` + hexdump 对照引擎源码实证过。

## 1. UE4.25 内嵌 MeshDescription（老格式）

网格导出内：属性表 → ObjectGuid → `FStripDataFlags(2B) + bCooked(bool32) + BodySetup(4B)
+ NavCollision(4B) + FName 字符串(高分辨率源名) + CRC(4B) + LightingGuid(16B) + Sockets`
→ 每 SourceModel：`bIsValid(int32)`；有效时 `FByteBulkData`（UE5 分块 zlib）+ GUID(16B)。

解出的 MeshDescription 二进制：位置式布局
`TBitArray{int32 numBits, words} × N（顶点/实例/边/面/组）` + 命名属性记录
`{typecode, nameLen, name, elemType, count, seg, stride, count2, data}`。
- **seg>1 = 多 UV 通道**，此时 count2==count，数据仍是 count×stride（通道 0）——直接接受
- elemType: 0=color 1=float3 2=float2 3=float 5=bool 6=string
- 记录间有零填充，需按"看起来像记录头"重同步

## 2. UE4.25 贴图源

纹理导出属性含 Source 结构（SizeX/SizeY/Format 从属性 blob 裸字节取）。
载荷两种形态：
- `bPNGCompressed`：整张 PNG 内嵌在文件里 → 扫 PNG 魔数直接抠
- 原始 BC 块：FByteBulkData（分块 zlib）→ 解出后按
  `blocksX×blocksY×BlockBytes` 推断像素格式再解码

## 3. UE5 虚拟化网格（StaticMeshDescriptionBulkData）

网格的 SourceModel 全部 `bIsValid=0` 时，几何在**独立的导出对象**里
（导出表 class 名 = `StaticMeshDescriptionBulkData`，每 LOD 一个 + HiRes 一个）。

**关键：这些导出对象没有 UObject 头，二进制从 SerialOffset 直接开始。**

```
FEditorBulkData（共 68B，LE）:
  u32  flags            // 0x69 常见；bit9(0x200)=载荷在包拖车（本方案未遇）
  16B  BulkDataId
  32B  PayloadContentId (SHA)
  i64  payloadSize      // 解压后字节数
  i64  offsetInFile     // 指向同文件内 FCompressedBuffer
  16B  Guid + 4B bGuidIsHash  ← 属外层 FMeshDescriptionBulkData，共 88B
```

### FCompressedBuffer（BE！）
```
u32 magic = 0xb7756362     （磁盘字节 b7 75 63 62）
u32 crc32
u8  method   // 0=未压缩 3=Oodle 4=LZ4
u8  compressor, u8 level
u8  blockSizeExponent     // 块大小 = 1<<exp（常见 18 = 256KB）
u32 blockCount
u64 totalRawSize
u64 totalCompressedSize
32B rawHash
u32 blockSizes[blockCount]（BE）
块数据（Oodle 流，末块 rawLen = totalRaw - (n-1)*blockSize）
```
Oodle 解压用 `oo2core_9_win64.dll` P/Invoke
`OodleLZ_Decompress(comp, compLen, raw, rawLen, 1,0,0, 0,0, 0,0, 0,0, 3)` cdecl。
**版本必须 ≥9**（oo2core_7 对 UE5.1 载荷返回 0）。

### UE5 MeshDescription（名字键控，"FNameAsStringProxyArchive"）
```
i32 容器数
每容器: { FString name(含\0，len 含\0)
          i32 通道数（FMeshElementChannels 的 TArray count，通常 1）
          每通道: { TBitArray 分配位图, i32 numHoles,
                    属性集: { i32 numElements, i32 attrCount,
                      每属性: { FString name, u32 typeIdx, u32 extent,
                                i32 setNumElements,      ← 易漏！
                                i32 chCount,
                                每通道: { i32 extent2, i32 stride, ← 元素字节数，易漏！
                                          i32 n, n×stride 数据 },
                                DefaultValue(按类型), u32 flags } } } }
```
typeIdx: 0=FVector4f(16B) 1=FVector3f(12B) 2=FVector2f(8B) 3=float 4=int32 5=bool(stride=1) 6=FName(字符串)
注意属性名可能带尾随空格（如 "Position "），查找时 Trim。

装配所需容器/属性：
- `Vertices.Position`（每顶点 float3）
- `VertexInstances.VertexIndex / TextureCoordinate / Normal`
- `Triangles.VertexInstanceIndex`（extent=3）`/ PolygonGroupIndex`（每三角形材质组）
- `PolygonGroups.ImportedMaterialSlotName`（type 6，尾随解析越界可容忍）

做法：每实例一个 glTF 顶点（位置取其顶点，UV/法线取实例自己的），索引用实例号。

## 4. UE5 旧版压缩块（贴图尾部）

文件尾（最后一个导出结束之后）直接是：
```
u32 magic = 0x9E2A83C1（= UE PACKAGE_FILE_TAG，磁盘 c1 83 2a 9e）
u32 method/flags
i64 chunkSize          // 128KB
i64 totalCompressed
i64 totalUncompressed
每块 { i64 comp, i64 uncomp }   // 块数 = ceil(totalUncomp/chunkSize)
zlib 流序列（每块一个，紧排到文件尾）
```
文件最后 4 字节又是 0x9E2A83C1（zlib 数据正好在它之前结束——可用来自校验）。
解压输出 = 完整源图（通常是 PNG，开头 89 50 4E 47）。
.NET 注意：`ZLibStream.Read` 单次会短读，必须循环读到满。

## 5. 降分辨率恢复

"assembled N < expected M" 的贴图：手里是低 mip（N≈M/4 → 半分辨率）。
按 scale=1,2,4,8 找 `expected/scale² ≤ N`，用 `w/scale × h/scale` 解码。

## 布局陷阱汇总

| 陷阱 | 说明 |
|------|------|
| bool 宽度 | 二进制序列化里 bool 是 **int32**（4B）；但属性数组元素 stride 里 bool=1B |
| UE5 FCompressedBuffer | 全部多字节字段**大端**；其余 UE 结构均小端 |
| FString | len 含 \0；消费 len 字节（len-1 字符 + 1 个 \0） |
| 独立 BulkData 导出对象 | 无 UObject 头（无控制字节/属性表/guid 位） |
| libarchive RAR5 | 解码器缺陷，禁止用于 RAR 解压 |
| 材质参数名 | UE4.25 在 `ParameterInfo.Index`（不是 .Name） |
| MI 跨包引用 | `FPackageIndex.Load()` 返回无属性占位对象，需 provider 完整加载包 |

## 6. 旧格式多边形元素布局（修正版，重要）

每个多边形元素恰好 **2 个 int：{ PolygonGroupID, 0 }**——周长数组**不序列化**
（勿按 FMeshPolygon_Legacy 源码读变长 TArray，会把非零组 ID 的网格全部带偏）。

三角形拓扑 = 实例按创建顺序三连（导入器行为）。**焊接网格不变量**：
`numVI == 3 × numPolys` 必须成立；不成立 = 文件里根本没有面连接数据
（实证：SM_fence_03 的 96 个多边形元素全零，只有 88 顶点/180 边/192 实例，
无三角形属性记录——源包残缺，UE 编辑器同样无法渲染）。

## 7. 关键正确性陷阱：position 按顶点存、索引引用实例

旧格式 Position 属性是**顶点键**（count == numVerts），而 TextureCoordinate/Normal
是**实例键**（count == numVI）。索引引用实例 → **必须用 viToVertex 把 position
展开到实例维**，否则焊接网格产生越界索引的非法 glTF（症状：max(index) ≥
POSITION accessor count）。全量交付前务必跑索引一致性抽检。
