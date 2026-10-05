// CityExporter v2 — batch-exports Unreal Engine UNCOOKED EDITOR PACKAGES
// (.uasset, no .pak) from a marketplace-style Content tree to glTF 2.0 (static
// meshes) and PNG (textures) for later import into Unity 6 (glTFast).
//
// COORDINATES / UNITS
// ===================
// Mesh vertex data is written exactly as stored in UE: Z-up, X-forward,
// centimeters. v2 adds a spec-compliant glTF root node per file:
//   rotation = (-0.7071068, 0, 0, 0.7071068)  (= -90 deg about X)
//   scale    = (0.01, 0.01, 0.01)
// Sanity check of the quaternion: q is a -90 deg X rotation, so
//   (x,y,z) -> (x, z, -y);  UE (0,0,1) -> glTF (0,1,0)  [Z-up -> Y-up] OK,
// and scale 0.01 converts cm -> m. The Unity side therefore needs no manual
// conversion; the raw buffers remain untouched UE data.
//
// NORMAL MAPS: UE normals are DirectX-style (-Y). They are bound as-is; the
// convention flip is handled on the Unity side (documented, not applied here).
//
// HOW MESH EXTRACTION WORKS
// =========================
// Uncooked editor packages store static mesh geometry NOT as cooked render data
// (CUE4Parse's UStaticMesh.RenderData is null for bCooked=false) but inside
// FStaticMeshSourceModel bulk data (FMeshDescriptionBulkData), which CUE4Parse
// skips. We re-open the raw .uasset bytes with our own FAssetArchive, seek to
// the StaticMesh export's SerialOffset, replay the exact property/guid/static-
// mesh reads CUE4Parse makes, land on the SourceModel bulk entry and let
// CUE4Parse's FByteBulkData decode UE's chunked-zlib payload, then parse the
// FMeshDescription: element bit-arrays, per-VI->vertex map (VIs are created in
// corner order, so consecutive triples = triangles; validated against the
// serialized edge list), per-poly group ids, and named attribute records
// ("Position", "TextureCoordinate", "Normal", "ImportedMaterialSlotName", ...).
//
// HOW MATERIAL BINDING WORKS (v2)
// ===============================
// Mesh StaticMaterials entries carry MaterialInterface (an import of a
// UMaterialInstanceConstant package, resolved to a /Game/... path),
// MaterialSlotName and ImportedMaterialSlotName. Material-instance packages
// (UMaterialInstanceConstant) store overrides as tagged properties:
// TextureParameterValues / ScalarParameterValues / VectorParameterValues, with
// the parameter name inside ParameterInfo.Name (this UE version) or a flat
// ParameterName. Texture params whose target PNG exists in the exported
// Textures tree are bound into the glTF (baseColor / normal / emissive /
// metallicRoughness + occlusion via a channel-swizzled _MR copy); see
// MatchCategory for the recognized parameter names.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;

// ---------------------------------------------------------------------------
// CLI
// ---------------------------------------------------------------------------

string content = "";
string outDir = "";
bool doMeshes = false, doTextures = false, doMaterials = false, doDump = false;

// --dump <file.uasset>: debug — print package summary / export map / properties
// --mdtest <file> <hexOffset>: run ParseMeshDescription on file[offset..EOF]
if (args.Length >= 3 && args[0] == "--mdtest")
{
    var path = args[1];
    var off = Convert.ToInt64(args[2], 16);
    var blob = File.ReadAllBytes(path)[(int)off..];
    var mdVersions = new VersionContainer(EGame.GAME_UE5_0);
    var mesh = new UStaticMesh();
    try
    {
        var (pos, uv, nrm, idx, slots, corner) = ParseMeshDescription(blob, mesh, mdVersions);
        Console.WriteLine($"OK: verts={pos.Length / 3} tris={idx.Length / 3} slots={slots.Length}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL at offset 0x{off:X}: {ex.Message}");
    }
    return 0;
}

if (args.Length >= 2 && args[0] == "--dump")
{
    var dumpPath = Path.GetFullPath(args[1]);
    var dumpDir = Path.GetDirectoryName(dumpPath)!;
    var dumpVersions = new VersionContainer(EGame.GAME_UE5_0);
    var dumpProvider = new DefaultFileProvider(dumpDir, SearchOption.AllDirectories, dumpVersions, StringComparer.OrdinalIgnoreCase);
    dumpProvider.Initialize();
    var gf = dumpProvider.Files.Values.FirstOrDefault(f => Path.GetFullPath(f.Path) == dumpPath || f.Name == Path.GetFileName(dumpPath));
    if (gf == null) { Console.Error.WriteLine("file not found in provider"); return 1; }
    if (!dumpProvider.TryLoadPackage(gf, out var dp) || dp == null) { Console.Error.WriteLine("load failed"); return 1; }
    Console.WriteLine($"== {gf.Path} ==");
    if (dp is CUE4Parse.UE4.Assets.Package p)
    {
        var s = p.Summary;
        foreach (var prop in s.GetType().GetProperties())
        {
            if (!prop.Name.Contains("Version", StringComparison.OrdinalIgnoreCase)) continue;
            object? v = null;
            try { v = prop.GetValue(s); } catch { }
            if (v != null) Console.WriteLine($"    summary.{prop.Name} = {v}");
        }
        Console.WriteLine($"-- export map ({p.ExportMap.Length}) --");
        foreach (var e in p.ExportMap)
            Console.WriteLine($"  {e.ObjectName}  class={e.ClassIndex?.ResolvedObject?.Name.Text ?? e.ClassIndex.ToString()}  serial=0x{e.SerialOffset:X} len={e.SerialSize}");
    }
    Console.WriteLine("-- exports --");
    foreach (var ex in dp.ExportsLazy)
    {
        var obj = ex.Value;
        Console.WriteLine($"### {obj.Name}  ({obj.GetType().Name})");
        // probe StaticMeshDescriptionBulkData exports: try FByteBulkData at several
        // offsets inside the export's serial region and report header fields
        if (dp is CUE4Parse.UE4.Assets.Package pk && obj.Name.Contains("MeshDescription"))
        {
            var e2 = Array.Find(pk.ExportMap, e => e.ObjectName.Text == obj.Name);
            if (e2 != null)
            {
                var rb = File.ReadAllBytes(gf is CUE4Parse.FileProvider.Objects.OsGameFile osf2 ? osf2.ActualFile.FullName : dumpPath);
                for (long probe = 0; probe <= 40; probe += 4)
                {
                    var pAr = new FAssetArchive(new FByteArchive("probe", rb, dumpVersions), pk)
                    {
                        Position = (int)(e2.SerialOffset + probe)
                    };
                    try
                    {
                        var bd = new FByteBulkData(pAr);
                        var h = bd.Header;
                        Console.WriteLine($"    bulkProbe+{probe}: flags=0x{(uint)h.BulkDataFlags:X} ec={h.ElementCount} sod={h.SizeOnDisk} off=0x{h.OffsetInFile:X} | after=0x{pAr.Position:X}");
                    }
                    catch (Exception exx) { Console.WriteLine($"    bulkProbe+{probe}: {exx.Message}"); }
                }
            }
        }
        if (obj is CUE4Parse.UE4.Assets.Exports.UObject uo)
        {
            foreach (var prop in uo.Properties)
            {
                var v = prop.Tag?.GenericValue;
                if (v is UScriptArray arr)
                {
                    Console.WriteLine($"    {prop.Name.Text}: array[{arr.Properties.Count}]");
                    foreach (var el in arr.Properties.Take(6))
                    {
                        if (el.GenericValue is FScriptStruct fss && fss.StructType is FStructFallback fb)
                            foreach (var fp in fb.Properties)
                                Console.WriteLine($"        . {fp.Name.Text} = {fp.Tag?.GenericValue}");
                        else
                            Console.WriteLine($"        . {el.GenericValue} ({el.GenericValue?.GetType().Name})");
                    }
                }
                else if (v is FStructFallback sfb)
                {
                    Console.WriteLine($"    {prop.Name.Text}: struct");
                    foreach (var fp in sfb.Properties)
                        Console.WriteLine($"        . {fp.Name.Text} = {fp.Tag?.GenericValue}");
                }
                else
                    Console.WriteLine($"    {prop.Name.Text} = {v}");
            }
            if (uo.Properties.Count == 0) Console.WriteLine("    <no properties loaded>");
        }
    }
    return 0;
}

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--content": content = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--meshes": doMeshes = true; break;
        case "--textures": doTextures = true; break;
        case "--materials": doMaterials = true; break;
        case "-h" or "--help":
            Console.WriteLine("Usage: CityExporter --content <dir> --out <dir> [--meshes] [--textures] [--materials]");
            return 0;
    }
}
if (content.Length == 0 || outDir.Length == 0 || (!doMeshes && !doTextures && !doMaterials))
{
    Console.Error.WriteLine("Usage: CityExporter --content <dir> --out <dir> [--meshes] [--textures] [--materials]");
    return 1;
}

Directory.CreateDirectory(outDir);
var errorLogPath = Path.Combine(outDir, "export_errors.log");
int exportedMeshes = 0, exportedTextures = 0, skipped = 0, failed = 0;
var errors = new List<string>();

var versions = new VersionContainer(EGame.GAME_UE5_0);
var provider = new DefaultFileProvider(content, SearchOption.AllDirectories, versions, StringComparer.OrdinalIgnoreCase);
provider.Initialize();

var uassetFiles = provider.Files.Values
    .Where(f => f.Extension == "uasset")
    .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
    .ToArray();
Console.WriteLine($"Found {uassetFiles.Length} .uasset files under {content}");

var miCache = new Dictionary<string, MaterialParams>(StringComparer.OrdinalIgnoreCase); // /Game/ pkg path -> params
var meshSlotInfos = new List<(string MeshName, string PkgPath, List<SlotInfo> Slots)>();

string MountPrefix() => uassetFiles.Length > 0
    ? uassetFiles[0].Path[..uassetFiles[0].Path.IndexOf('/')]
    : Path.GetFileName(content.TrimEnd('\\', '/'));

string mount = MountPrefix();

// ---- pass 1: textures (so the Textures tree is complete before any glTF
//      binding resolves image URIs / writes swizzled _MR copies) ----
if (doTextures)
{
    foreach (var file in uassetFiles)
    {
        try
        {
            if (!provider.TryLoadPackage(file, out var package) || package == null) throw new Exception("TryLoadPackage failed");
            var tex = package.ExportsLazy.Select(l => l.Value).OfType<UTexture2D>().FirstOrDefault();
            if (tex == null) continue;
            var relPath = file.Path.Contains('/') ? file.Path[(file.Path.IndexOf('/') + 1)..] : file.Path;
            var baseName = relPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? relPath[..^7] : relPath;
            var (png, w, h) = ExtractTexturePng(package, tex, file, versions);
            var outPath = Path.Combine(outDir, "Textures", baseName.Replace('/', Path.DirectorySeparatorChar) + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllBytes(outPath, png);
            exportedTextures++;
            Console.WriteLine($"[texture] {baseName}: {w}x{h}, {png.Length} bytes");
        }
        catch (Exception ex)
        {
            failed++;
            var line = $"{file.Path}: {ex.GetType().Name}: {ex.Message}";
            errors.Add(line);
            Console.Error.WriteLine($"[FAILED]  {line}");
        }
    }
}

// ---- pass 2: meshes (geometry + slots + material binding) ----
if (doMeshes || doMaterials)
{
    foreach (var file in uassetFiles)
    {
        try
        {
            if (!provider.TryLoadPackage(file, out var package) || package == null) continue;
            var mesh = package.ExportsLazy.Select(l => l.Value).OfType<UStaticMesh>().FirstOrDefault();
            if (mesh == null) continue;
            var relPath = file.Path.Contains('/') ? file.Path[(file.Path.IndexOf('/') + 1)..] : file.Path;
            var baseName = relPath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? relPath[..^7] : relPath;

            // per-slot info from StaticMaterials (property-only; no bulk parse)
            var slots = GetMeshSlots(mesh);

            // load + parse the referenced material instances.
            // NOTE: s.MaterialIndex.Load() returns a hollow import object with
            // no properties in this CUE4Parse build — load the MI package via
            // the provider instead (dump-verified).
            var slotParams = new List<MaterialParams?>();
            foreach (var s in slots)
            {
                MaterialParams? mp = null;
                if (s.MaterialPath != null)
                {
                    var key = s.MaterialPath.SubstringBefore2('.');
                    if (!miCache.TryGetValue(key, out mp))
                    {
                        mp = null;
                        var relGame = (key.StartsWith("/Game/") ? key[6..] : key).Replace('\\', '/') + ".uasset";
                        var gf = provider.Files.Values
                            .FirstOrDefault(f => f.Path.Replace('\\', '/').EndsWith(relGame, StringComparison.OrdinalIgnoreCase));
                        if (gf != null && provider.TryLoadPackage(gf, out var mpkg) && mpkg != null)
                        {
                            foreach (var lz in mpkg.ExportsLazy)
                            {
                                if (lz.Value is UMaterialInstanceConstant mic)
                                {
                                    mp = ParseMiParams(mic, key);
                                    break;
                                }
                            }
                        }
                        miCache[key] = mp ?? new MaterialParams { Path = key, Name = key.After2('/') };
                    }
                    else mp = miCache[key];
                }
                slotParams.Add(mp);
            }
            meshSlotInfos.Add((mesh.Name, relPath, slots));

            if (!doMeshes) continue;

            var (positions, uvs, normals, indices, slotNames, cornerSlots) = ExtractStaticMesh(package, mesh, file, versions);
            var gltfPath = Path.Combine(outDir, "Meshes", baseName.Replace('/', Path.DirectorySeparatorChar) + ".gltf");
            var bindings = slots
                .Select((s, i) => ResolveBinding(slotParams[i], outDir, Path.GetDirectoryName(gltfPath)!))
                .ToArray();
            WriteGltf(gltfPath, mesh.Name, positions, uvs, normals, indices, slotNames, cornerSlots, bindings);
            exportedMeshes++;
            var bound = string.Join(",", bindings.Select(bnd => bnd == null ? "-" :
                (bnd.BaseColorPng != null ? "BC" : "") + (bnd.NormalPng != null ? "+N" : "") +
                (bnd.MetallicRoughnessPng != null ? "+MR" : "") + (bnd.EmissivePng != null ? "+E" : "")));
            Console.WriteLine($"[mesh]    {baseName}: {indices.Length / 3} tris, {positions.Length / 3} verts, {slotNames.Length} slot(s), bindings=[{bound}]");
        }
        catch (Exception ex)
        {
            failed++;
            var line = $"{file.Path}: {ex.GetType().Name}: {ex.Message}";
            errors.Add(line);
            Console.Error.WriteLine($"[FAILED]  {line}");
        }
    }
}

// ---- pass 3: every remaining material-instance package ----
if (doMaterials)
{
    foreach (var file in uassetFiles)
    {
        try
        {
            var rel = file.Path.Contains('/') ? file.Path[(file.Path.IndexOf('/') + 1)..] : file.Path;
            if (!rel.Contains("Material", StringComparison.OrdinalIgnoreCase)) continue;
            if (!provider.TryLoadPackage(file, out var package) || package == null) continue;
            foreach (var lazy in package.ExportsLazy)
            {
                if (lazy.Value is not UMaterialInstanceConstant mic) continue;
                var key = "/Game/" + (rel.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? rel[..^7] : rel);
                if (!miCache.ContainsKey(key))
                    miCache[key] = ParseMiParams(mic, key);
            }
        }
        catch (Exception ex)
        {
            failed++;
            var line = $"{file.Path}: {ex.GetType().Name}: {ex.Message}";
            errors.Add(line);
            Console.Error.WriteLine($"[FAILED]  {line}");
        }
    }

    // ---- materials.json ----
    var mjPath = Path.Combine(outDir, "materials.json");
    WriteMaterialsJson(mjPath, meshSlotInfos, miCache.Values.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList());
    Console.WriteLine($"[materials.json] {miCache.Count} material(s), {meshSlotInfos.Count} mesh(es) with slots");
}

if (errors.Count > 0)
    File.WriteAllLines(errorLogPath, errors);

Console.WriteLine();
Console.WriteLine($"Done. meshes={exportedMeshes} textures={exportedTextures} skipped={skipped} failed={failed}");
if (errors.Count > 0) Console.WriteLine($"Errors written to {errorLogPath}");
return 0;

// ---------------------------------------------------------------------------
// Static mesh extraction from uncooked editor packages
// ---------------------------------------------------------------------------

static (float[] pos, float[] uv, float[] nrm, uint[] idx, string[] slots, int[] cornerSlots)
    ExtractStaticMesh(CUE4Parse.UE4.Assets.IPackage package, UStaticMesh mesh, GameFile file, VersionContainer versions)
{
    if (package is not CUE4Parse.UE4.Assets.Package pkg)
        throw new Exception("package is not a CUE4Parse.UE4.Assets.Package");

    var expIdx = Array.FindIndex(pkg.ExportMap, e => e.ObjectName.Text == mesh.Name);
    if (expIdx < 0) throw new Exception("static mesh export not found in export map");
    var exp = pkg.ExportMap[expIdx];

    var rawBytes = file is CUE4Parse.FileProvider.Objects.OsGameFile osf
        ? File.ReadAllBytes(osf.ActualFile.FullName)
        : file.Read();
    var baseAr = new FByteArchive(file.Path, rawBytes, versions);
    var ar = new FAssetArchive(baseAr, pkg);
    ar.Position = exp.SerialOffset;
    long serialEnd = exp.SerialOffset + exp.SerialSize;

    // ---- replicate UObject.Deserialize (tagged properties) ----
    if (ar.Ver >= EUnrealEngineObjectUE5Version.PROPERTY_TAG_EXTENSION_AND_OVERRIDABLE_SERIALIZATION)
    {
        var ctrl = ar.Read<EClassSerializationControlExtension>();
        if (ctrl.HasFlag(EClassSerializationControlExtension.OverridableSerializationInformation))
            ar.Position += 1;
    }
    while (true)
    {
        var tag = new FPropertyTag(ar, false);
        if (tag.Name.IsNone) break;
        ar.Position = ar.Position + tag.Size; // skip property data
        if (ar.Position > serialEnd) throw new Exception("property parse overran export bounds");
    }

    // ---- ObjectGuid ----
    if (ar.Game >= EGame.GAME_UE4_0 && (exp.ObjectFlags & (uint)EObjectFlags.RF_ClassDefaultObject) == 0)
    {
        if (ar.ReadBoolean()) ar.Position += 16;
    }

    // ---- replicate UStaticMesh.Deserialize up to its editor early-return ----
    var strip = new FStripDataFlags(ar);
    bool bCooked = ar.ReadBoolean(); // VER_UE4_STATIC_MESH_REFACTOR+
    ar.Position += 4; // BodySetup (FPackageIndex)
    if (ar.Versions["StaticMesh.HasNavCollision"]) ar.Position += 4; // NavCollision
    if (!strip.IsEditorDataStripped())
    {
        if (ar.Ver < EUnrealEngineObjectUE4Version.DEPRECATED_STATIC_MESH_THUMBNAIL_PROPERTIES_REMOVED)
        {
            ar.Position += 12; // FRotator
            if (ar.Ver >= EUnrealEngineObjectUE3Version.STATICMESH_THUMBNAIL_DISTANCE) ar.Position += 4;
        }
        if (ar.Ver >= EUnrealEngineObjectUE3Version.STATICMESH_VERSION_18
            && FRenderingObjectVersion.Get(ar) < FRenderingObjectVersion.Type.DeprecatedHighResSourceMesh
            && ar.Game != EGame.GAME_APBReloaded)
        {
            ar.ReadFString();
            ar.Position += 4; // CRC
        }
    }
    ar.Position += 16; // LightingGuid (VER_UE3_INTEGRATED_LIGHTMASS+)
    int socketCount = ar.Read<int>();
    ar.Position += 4L * socketCount; // Sockets

    if (bCooked) throw new Exception("cooked static mesh — handled by CUE4Parse, not this path");

    // ---- per SourceModel: FStaticMeshSourceModel::SerializeBulkData ----
    int nSrc = mesh.GetOrDefault<UScriptArray>("SourceModels")?.Properties.Count ?? 1;
    int bIsValid = ar.Read<int>();
    if (bIsValid != 0)
    {
        var bulk = new FByteBulkData(ar); // reads header, skips inline payload, handles ZLIB
        if (FEditorObjectVersion.Get(ar) >= FEditorObjectVersion.Type.MeshDescriptionBulkDataGuid)
            ar.Position += 16; // GUID
        // NOTE: UE's bGuidIsHash trailing byte (FEnterpriseObjectVersion) is not tracked by
        // current CUE4Parse; it only affects SourceModels after the first, which we skip.

        var desc = bulk.Data ?? throw new Exception("FMeshDescription bulk payload is null");
        return ParseMeshDescription(desc, mesh, versions);
    }

    // v4: UE5-style save — geometry lives in separate StaticMeshDescriptionBulkData
    // exports (FEditorBulkData header + FCompressedBuffer/Oodle payload at file end).
    return ExtractFromBulkExports(ar, pkg, rawBytes, mesh, versions, serialEnd);
}


// ---------------------------------------------------------------------------
// UE5 uncooked bulk-export path: geometry in UStaticMeshDescriptionBulkData
// export objects. Layout (dump-verified on this pack + UE5.1 source):
//   export binary: [class ctrl][props->None][guid-bool] then FMeshDescriptionBulkData:
//     FEditorBulkData { EFlags u32, BulkDataId 16B, PayloadContentId 32B(SHA),
//                       PayloadSize i64, OffsetInFile i64 }
//     + Guid 16B + bGuidIsHash bool32
//   payload at OffsetInFile: FCompressedBuffer (BIG-endian fields):
//     magic 0xb7756362, crc32, method u8 (3=Oodle), compressor, level,
//     blockSizeExponent u8, blockCount u32, totalRawSize u64, totalCompressedSize u64,
//     rawHash 32B, [blockSizes u32 xN], blocks (oodle)
// ---------------------------------------------------------------------------

static (float[] pos, float[] uv, float[] nrm, uint[] idx, string[] slots, int[] cornerSlots)
    ExtractFromBulkExports(FAssetArchive ar, CUE4Parse.UE4.Assets.Package pkg, byte[] rawBytes,
        UStaticMesh mesh, VersionContainer versions, long serialEnd)
{
    byte[]? best = null;
    foreach (var exp in pkg.ExportMap)
    {
        var clsName = exp.ClassIndex?.ResolvedObject?.Name.Text;
        if (clsName != "StaticMeshDescriptionBulkData") continue;
        Console.Error.WriteLine($"[bulk-debug] found export {exp.ObjectName} @0x{exp.SerialOffset:X} len={exp.SerialSize}");
        if (exp.SerialSize < 60) continue;

        ar.Position = exp.SerialOffset;
        try
        {
            // dump-verified: these exports carry NO UObject header; the
            // FMeshDescriptionBulkData binary begins at the export start.
            // FEditorBulkData
            uint flags = ar.Read<uint>();
            ar.Position += 16;          // BulkDataId
            ar.Position += 32;          // PayloadContentId (SHA)
            long payloadSize = ar.Read<long>();
            long offsetInFile;
            if ((flags & 0x200) != 0)
            {
                Console.Error.WriteLine($"[bulk-debug] {exp.ObjectName}: TRAILER variant, unsupported");
                continue;
            }
            offsetInFile = ar.Read<long>();
            Console.Error.WriteLine($"[bulk-debug] {exp.ObjectName}: flags=0x{flags:X} payloadSize={payloadSize} off=0x{offsetInFile:X}");
            if (offsetInFile <= 0 || offsetInFile >= rawBytes.Length) { Console.Error.WriteLine("[bulk-debug]   offset out of range"); continue; }
            if (payloadSize <= 0 || payloadSize > (1L << 28)) { Console.Error.WriteLine("[bulk-debug]   payloadSize out of range"); continue; }

            var blob = DecodeCompressedBuffer(rawBytes, offsetInFile);
            Console.Error.WriteLine($"[bulk-debug] {exp.ObjectName}: decoded={(blob?.Length.ToString() ?? "null")}");
            if (blob != null && blob.Length > 32 && (best == null || blob.Length > best.Length))
                best = blob;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bulk-debug] {exp.ObjectName}: {ex.Message}");
        }
    }

    if (best == null) throw new Exception("no decodable StaticMeshDescriptionBulkData payload");
    {
        var hex = new StringBuilder();
        for (int i = 0; i < Math.Min(96, best.Length); i++)
        {
            hex.Append(best[i].ToString("x2"));
            if ((i & 15) == 15) hex.Append('\n'); else hex.Append(' ');
        }
        Console.Error.WriteLine($"[md-debug] blob len={best.Length}\n{hex}");
    }
    try
    {
        return ParseMeshDescriptionUE5(best, mesh, versions);
    }
    catch (NotImplementedException) { throw; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[ue5md] new-format parse failed ({ex.Message}); trying legacy parser");
        return ParseMeshDescription(best, mesh, versions);
    }
}


// ---------------------------------------------------------------------------
// UE5 new-format FMeshDescription (FNameAsStringProxyArchive):
//   { int32 containerCount; per container: { FString name;
//       TBitArray allocated {int32 numBits; words};
//       int32 numHoles;
//       FAttributesSetBase { int32 numElements; int32 attrCount;
//         per attr: { FString name; uint32 typeIdx; uint32 extent;
//           int32 numChannels; per channel: { int32 extent2; TArray bulk {int32 n; n*sizeof data} };
//           DefaultValue; uint32 flags } } } }
// AttributeTypes tuple: 0=FVector4f 1=FVector3f 2=FVector2f 3=float 4=int32 5=bool 6=FName(string)
// ---------------------------------------------------------------------------

static (float[] pos, float[] uv, float[] nrm, uint[] idx, string[] slots, int[] cornerSlots)
    ParseMeshDescriptionUE5(byte[] blob, UStaticMesh mesh, VersionContainer versions)
{
    var r = new BlobReader(blob);
    string Str()
    {
        int len = r.I32(); // includes null terminator
        var s = Encoding.ASCII.GetString(r.Bytes(len - 1));
        r.Skip(1); // consume null
        return s;
    }
    int[] BitArrayWords()
    {
        int numBits = r.I32();
        int words = (numBits + 31) / 32;
        var w = new int[words];
        for (int i = 0; i < words; i++) w[i] = r.I32();
        return new[] { numBits }.Concat(w).ToArray();
    }

    int nContainers = r.I32();
    var containers = new Dictionary<string, Ue5Container>(StringComparer.OrdinalIgnoreCase);
    for (int c = 0; c < nContainers; c++)
    {
        var cont = new Ue5Container { Name = Str() };
        int nChannels = r.I32(); // FMeshElementChannels: TArray<FMeshElementContainer>
        for (int contCh = 0; contCh < nChannels; contCh++)
        {
        cont.NumBits = r.I32();
        int words = (cont.NumBits + 31) / 32;
        r.Skip(4L * words);
        cont.NumHoles = r.I32();
        Console.Error.WriteLine($"[ue5md] {cont.Name}: nCh={nChannels} bits={cont.NumBits} holes={cont.NumHoles} pos=0x{r.Position:X}");
        // attributes
        cont.NumElements = r.I32();
        Console.Error.WriteLine($"[ue5md] {cont.Name}: numElements={cont.NumElements} pos=0x{r.Position:X}");
        int nAttr = r.I32();
        Console.Error.WriteLine($"[ue5md] {cont.Name}: nAttr={nAttr} pos=0x{r.Position:X}");
        if (cont.Name == "Vertices")
        {
            var hb = new System.Text.StringBuilder();
            for (int i = 0x51; i < Math.Min(0x120, blob.Length); i++)
            {
                hb.Append(blob[i].ToString("x2"));
                if ((i & 15) == 15) hb.Append((char)10); else hb.Append((char)32);


            }
            Console.Error.WriteLine("[ue5md] attr-region dump:" + (char)10 + hb);
        }
        for (int a = 0; a < nAttr; a++)
        {
          try
          {
            var at = new Ue5Attr { Name = Str(), TypeIdx = (uint)r.I32(), Extent = (uint)r.I32() };
            int setNumElements = r.I32(); // TMeshAttributeArraySet::NumElements
            int nCh = r.I32();
            Console.Error.WriteLine($"[ue5md]     attr '{at.Name}' type={at.TypeIdx} ext={at.Extent} nCh={nCh} pos=0x{r.Position:X}");
            for (int ch = 0; ch < nCh; ch++)
            {
                int extent2 = r.I32();
                int stride = r.I32(); // recorded element byte size (dump-verified: 12 for FVector3f)
                int n = r.I32(); // element count
                int typeSize = at.TypeIdx switch
                {
                    0 => 16, 1 => 12, 2 => 8, 3 => 4, 4 => 4, 5 => 4, 6 => 0, _ => throw new Exception($"unknown attr type {at.TypeIdx}")
                };
                if (at.TypeIdx == 6)
                {
                    // FName values serialized as strings via proxy; trailing attrs may
                    // overrun on format drift — tolerate (not needed for assembly)
                    var payload = new List<byte>();
                    try
                    {
                        for (int i = 0; i < n; i++)
                        {
                            int slen = r.I32();
                            if (slen is < 1 or > 4096) throw new Exception();
                            var sb = r.Bytes(slen);
                            payload.AddRange(BitConverter.GetBytes(slen));
                            payload.AddRange(sb);
                        }
                    }
                    catch (Exception) { /* partial FName data is fine */ }
                    at.Channels.Add(payload.ToArray());
                }
                else
                {
                    if (at.TypeIdx == 5) typeSize = Math.Max(1, stride); // bool: 1-byte stride
                    at.Channels.Add(r.Bytes((long)n * typeSize));
                }
                at.ElemCount = n;
                _ = extent2;
            }
            // DefaultValue + Flags
            switch (at.TypeIdx)
            {
                case 0: r.Skip(16); break;
                case 1: r.Skip(12); break;
                case 2: r.Skip(8); break;
                case 3: case 4: case 5: r.Skip(4); break;
                case 6: _ = Str(); break;
            }
            r.Skip(4); // EMeshAttributeFlags
            cont.Attrs[at.Name] = at;
            Console.Error.WriteLine($"[ue5md]   attr {at} (pos=0x{r.Position:X})");
          }
          catch (Exception ex)
          {
            // tolerate trailing-attribute format drift; safe because the data we
            // need (positions/instances/triangles/groups) lives in earlier containers
            Console.Error.WriteLine($"[ue5md]   attr read failed on {cont.Name} #{a}: {ex.Message} — stopping attr loop");
            break;
          }
        }
        } // channel loop
        containers[cont.Name] = cont;
        Console.Error.WriteLine($"[ue5md] {cont} (pos=0x{r.Position:X})");
    }
    foreach (var kv in containers)
        foreach (var at in kv.Value.Attrs.Values)
            Console.Error.WriteLine($"[ue5md]   {kv.Value.Name}.{at}");

    // ---- assemble indexed geometry ----
    Ue5Container? C(string name) => containers.TryGetValue(name, out var c) ? c : null;
    Ue5Attr? A(Ue5Container c, string name) => c.Attrs.TryGetValue(name, out var a) ? a
        : c.Attrs.FirstOrDefault(kv => kv.Key.Trim() == name).Value;
    float[] Vec(Ue5Container c, string name, int comps)
    {
        var a = A(c, name) ?? throw new Exception($"missing attr {name} on {c.Name}");
        var b = a.Channels[0];
        int n = a.ElemCount;
        var f = new float[n * comps];
        for (int i = 0; i < n * comps; i++)
            f[i] = BitConverter.ToSingle(b, i * 4);
        return f;
    }
    int[] Ints(Ue5Container c, string name)
    {
        var a = A(c, name) ?? throw new Exception($"missing attr {name} on {c.Name}");
        var b = a.Channels[0];
        int n = a.ElemCount;
        var v = new int[n];
        for (int i = 0; i < n; i++)
            v[i] = BitConverter.ToInt32(b, i * 4);
        return v;
    }

    var vCont = C("Vertices") ?? throw new Exception("no Vertices container");
    var iCont = C("VertexInstances") ?? throw new Exception("no VertexInstances container");
    var tCont = C("Triangles") ?? throw new Exception("no Triangles container");

    var vPos = Vec(vCont, "Position", 3);
    var iVert = Ints(iCont, "VertexIndex");
    var iUV = Vec(iCont, "TextureCoordinate", 2);
    var iNrm = Vec(iCont, "Normal", 3);
    var tInst = Ints(tCont, "VertexInstanceIndex");   // 3 per triangle
    var tGroup = Ints(tCont, "PolygonGroupIndex");    // 1 per triangle

    int nInst = iCont.NumElements;
    int nTri = tCont.NumElements;
    if (tInst.Length < nTri * 3) throw new Exception($"triangle instance array short: {tInst.Length} < {nTri * 3}");

    // build one glTF vertex per vertex-instance (corner): pos from its vertex, own uv+normal
    var pos = new float[nInst * 3];
    var uv = new float[nInst * 2];
    var nrm = new float[nInst * 3];
    for (int i = 0; i < nInst; i++)
    {
        int v = iVert[i];
        if (v < 0 || v * 3 + 2 >= vPos.Length) throw new Exception($"bad vertex index {v} at instance {i}");
        pos[i * 3] = vPos[v * 3]; pos[i * 3 + 1] = vPos[v * 3 + 1]; pos[i * 3 + 2] = vPos[v * 3 + 2];
        uv[i * 2] = iUV[i * 2]; uv[i * 2 + 1] = iUV[i * 2 + 1];
        nrm[i * 3] = iNrm[i * 3]; nrm[i * 3 + 1] = iNrm[i * 3 + 1]; nrm[i * 3 + 2] = iNrm[i * 3 + 2];
    }
    var idx = new uint[nTri * 3];
    var cornerSlots = new int[nTri * 3];
    var groupIds = new List<int>();
    for (int t = 0; t < nTri; t++)
    {
        int g = tGroup[t];
        if (!groupIds.Contains(g)) groupIds.Add(g);
        for (int k = 0; k < 3; k++)
        {
            int inst = tInst[t * 3 + k];
            if (inst < 0 || inst >= nInst) throw new Exception($"bad instance index {inst} at tri {t}");
            idx[t * 3 + k] = (uint)inst;
            cornerSlots[t * 3 + k] = groupIds.IndexOf(g);
        }
    }
    // slot names: map group index to StaticMaterials slot names where counts line up
    string[] slots;
    var propSlots = GetMeshSlots(mesh);
    var groupNames = new List<string>();
    for (int i = 0; i < groupIds.Count; i++)
        groupNames.Add(propSlots.Count == groupIds.Count ? propSlots[i].SlotName : $"Slot{groupIds[i]}");
    slots = groupNames.ToArray();
    Console.Error.WriteLine($"[ue5md] assembled: {vCont.NumElements} verts, {nInst} instances, {nTri} tris, {groupIds.Count} groups");
    return (pos, uv, nrm, idx, slots, cornerSlots);
}



// UE legacy compressed-chunk format (FArchiveSaveCompressedProxy):
// { u32 magic 0x9E2A83C1, u32 method, i64 chunkSize, i64 totalCompressed,
//   i64 totalUncompressed, per-chunk {i64 comp, i64 uncomp}, zlib streams }
// dump-verified on this pack's UE5-saved textures.
static byte[]? DecodeLegacyCompressed(byte[] raw, long magicOff)
{
    var r = new BlobReader(raw);
    r.Position = (int)magicOff;
    if (r.I32() != unchecked((int)0x9E2A83C1u)) return null;
    _ = r.I32();                       // method/flags
    long chunkSize = r.I32();          // i64 (high half below)
    _ = r.I32();
    long totalComp = r.I32(); _ = r.I32();
    long totalUncomp = r.I32(); _ = r.I32();
    if (chunkSize is <= 0 or > (1 << 24)) return null;
    if (totalComp <= 0 || totalComp > raw.Length) return null;
    if (totalUncomp is <= 0 or > (1L << 31)) return null;
    int nChunks = (int)((totalUncomp + chunkSize - 1) / chunkSize);
    if (nChunks is < 1 or > 4096) return null;
    var chunks = new (long comp, long uncomp)[nChunks];
    for (int i = 0; i < nChunks; i++)
    {
        chunks[i].comp = r.I32(); _ = r.I32();
        chunks[i].uncomp = r.I32(); _ = r.I32();
        if (chunks[i].comp <= 0 || chunks[i].uncomp <= 0) return null;
    }
    long dataStart = r.Position;
    if (dataStart + totalComp > raw.Length) return null;
    var output = new byte[totalUncomp];
    long outPos = 0;
    long inPos = dataStart;
    foreach (var (comp, uncomp) in chunks)
    {
        using var ms = new MemoryStream(raw, (int)inPos, (int)comp);
        using var zl = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
        int need = (int)uncomp, got = 0;
        while (got < need)
        {
            int rr = zl.Read(output, (int)outPos + got, need - got);
            if (rr <= 0) break;
            got += rr;
        }
        if (got != need) throw new Exception($"legacy chunk short: {got}/{need}");
        inPos += comp;
        outPos += uncomp;
    }
    return output;
}


// decode a UE5 FCompressedBuffer starting at fileOffset; returns uncompressed bytes
static byte[]? DecodeCompressedBuffer(byte[] raw, long fileOffset)
{
    var r = new BlobReaderBe(raw);
    r.Position = (int)fileOffset;
    uint magic = r.U32();
    if (magic != 0xb7756362u) return null;
    _ = r.U32();               // crc32
    byte method = r.U8();      // 0=none, 3=oodle, 4=lz4
    _ = r.U8(); _ = r.U8();    // compressor, level
    int blockSizeExp = r.U8();
    uint blockCount = r.U32();
    ulong totalRaw = r.U64();
    _ = r.U64();               // totalCompressedSize
    r.Skip(32);                // rawHash

    if (totalRaw == 0 || totalRaw > (1UL << 28)) return null;
    int blockSize = 1 << Math.Clamp(blockSizeExp, 12, 24);

    if (method == 0)
    {
        // one uncompressed block
        return r.Bytes((long)totalRaw);
    }
    if (method != 3) return null; // oodle only

    var blockSizes = new uint[blockCount];
    for (int i = 0; i < blockCount; i++) blockSizes[i] = r.U32();

    var compAll = Marshal.AllocHGlobal((int)(raw.Length - r.Position));
    var outAll = Marshal.AllocHGlobal((int)totalRaw);
    try
    {
        Marshal.Copy(raw, r.Position, compAll, (int)(raw.Length - r.Position));
        long compPos = 0;
        var output = new byte[(int)totalRaw];
        for (int i = 0; i < blockCount; i++)
        {
            int rawLen = (int)Math.Min((ulong)blockSize, totalRaw - (ulong)((long)i * blockSize));
            if (rawLen <= 0) break;
            long got = OodleNative.Decompress(compAll + (int)compPos, (int)blockSizes[i],
                (nint)(outAll + (long)i * blockSize), rawLen);
            if (got <= 0) throw new Exception($"oodle block {i} failed (rawLen={rawLen} comp={blockSizes[i]})");
            compPos += blockSizes[i];
        }
        Marshal.Copy(outAll, output, 0, (int)totalRaw);
        return output;
    }
    finally
    {
        Marshal.FreeHGlobal(compAll);
        Marshal.FreeHGlobal(outAll);
    }
}



static (float[] pos, float[] uv, float[] nrm, uint[] idx, string[] slots, int[] cornerSlots)
    TryRenderDataPath(FAssetArchive ar, long fromPos, long serialEnd)
{
    Exception? lastErr = null;
    (long scanPos, long reached, string msg)? best = null;
    for (long p = fromPos; p < Math.Min(fromPos + 512, serialEnd - 8); p++)
    {
        ar.Position = p;
        try
        {
            var rd = new FStaticMeshRenderData(ar);
            var lod0 = (rd.LODs?.Length ?? 0) > 0 ? rd.LODs[0] : null;
            if (lod0 == null || lod0.SkipLod) continue;
            var posBuf = lod0.PositionVertexBuffer ?? throw new Exception("no position buffer");
            int nVerts = posBuf.NumVertices;
            var verts = posBuf.Verts;
            if (nVerts < 3 || nVerts > 5_000_000 || verts.Length < nVerts) continue;
            var idxBuf = lod0.IndexBuffer ?? throw new Exception("no index buffer");
            uint[] indices = idxBuf.Buffer ?? throw new Exception("empty index buffer");
            if (indices.Length < 3 || indices.Length % 3 != 0) continue;
            if (indices.Max() >= (uint)nVerts) continue;
            var sections = lod0.Sections;
            if (sections.Length == 0 || sections.Length > 256) continue;
            if (sections.Any(s => s.NumTriangles <= 0 || s.FirstIndex < 0
                                  || s.FirstIndex + s.NumTriangles * 3 > indices.Length)) continue;

            var pos = new float[nVerts * 3];
            for (int i = 0; i < nVerts; i++)
            {
                pos[i * 3] = verts[i].X; pos[i * 3 + 1] = verts[i].Y; pos[i * 3 + 2] = verts[i].Z;
            }
            var uvItems = lod0.VertexBuffer?.UV;
            var uv = new float[nVerts * 2];
            if (uvItems != null)
            {
                for (int i = 0; i < Math.Min(nVerts, uvItems.Length); i++)
                {
                    var c = uvItems[i].UV;
                    if (c is { Length: > 0 })
                    {
                        uv[i * 2] = c[0].U; uv[i * 2 + 1] = c[0].V;
                    }
                }
            }
            // area-weighted smooth normals from triangles
            var acc = new float[nVerts * 3];
            for (int t = 0; t < indices.Length; t += 3)
            {
                uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
                float e1x = pos[b * 3] - pos[a * 3], e1y = pos[b * 3 + 1] - pos[a * 3 + 1], e1z = pos[b * 3 + 2] - pos[a * 3 + 2];
                float e2x = pos[c * 3] - pos[a * 3], e2y = pos[c * 3 + 1] - pos[a * 3 + 1], e2z = pos[c * 3 + 2] - pos[a * 3 + 2];
                float nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                acc[a * 3] += nx; acc[a * 3 + 1] += ny; acc[a * 3 + 2] += nz;
                acc[b * 3] += nx; acc[b * 3 + 1] += ny; acc[b * 3 + 2] += nz;
                acc[c * 3] += nx; acc[c * 3 + 1] += ny; acc[c * 3 + 2] += nz;
            }
            var nrm = new float[nVerts * 3];
            for (int i = 0; i < nVerts; i++)
            {
                float x = acc[i * 3], y = acc[i * 3 + 1], z = acc[i * 3 + 2];
                float len = MathF.Sqrt(x * x + y * y + z * z);
                if (len > 1e-20f) { x /= len; y /= len; z /= len; }
                else { x = 0; y = 0; z = 1; }
                nrm[i * 3] = x; nrm[i * 3 + 1] = y; nrm[i * 3 + 2] = z;
            }

            // cornerSlots from section ranges
            var cornerSlots = new int[indices.Length];
            var slotIds = new List<int>();
            foreach (var s in sections)
            {
                if (!slotIds.Contains(s.MaterialIndex)) slotIds.Add(s.MaterialIndex);
                for (long j = s.FirstIndex; j < s.FirstIndex + (long)s.NumTriangles * 3; j++)
                    cornerSlots[j] = slotIds.IndexOf(s.MaterialIndex);
            }
            var slots = slotIds.Select(i => $"Slot{i}").ToArray();
            return (pos, uv, nrm, indices, slots, cornerSlots);
        }
        catch (Exception ex)
        {
            lastErr = ex;
            if (best == null || ar.Position > best.Value.reached)
                best = (p, ar.Position, ex.Message);
        }
    }
    throw new Exception($"RenderData fallback failed (best scan: pos=0x{best?.scanPos:X} reached=0x{best?.reached:X} (+{best?.reached - best?.scanPos} bytes) msg={best?.msg}; last: {lastErr?.Message})");
}

static (float[] pos, float[] uv, float[] nrm, uint[] idx, string[] slots, int[] cornerSlots)
    ParseMeshDescription(byte[] blob, UStaticMesh mesh, VersionContainer versions)
{
    var r = new BlobReader(blob);

    // TBitArray: NumBits + ceil(NumBits/32) words
    int NumElements() { int bits = r.I32(); r.Skip(4L * ((bits + 31) / 32)); return bits; }

    int numVerts = NumElements();
    int numVI = NumElements();
    var viToVertex = new int[numVI];
    for (int i = 0; i < numVI; i++) viToVertex[i] = r.I32();

    int numEdges = NumElements();
    var edges = new int[numEdges * 2];
    for (int i = 0; i < numEdges * 2; i++) edges[i] = r.I32();

    int numPolys = NumElements();
    var polyGroup = new int[numPolys];
    for (int p = 0; p < numPolys; p++)
    {
        polyGroup[p] = r.I32(); // polygon group id
        r.I32();                // second int (unused/flags)
    }

    int numGroups = NumElements();
    r.I32(); // attribute count marker

    // ---- named attribute records (zero-padded gaps between records: resync) ----
    float[]? position = null;         // per-vertex
    float[]? texCoord = null;         // per-VI (2 floats)
    float[]? normal = null;           // per-VI (3 floats)

    bool LooksLikeRecord(int pos)
    {
        if (pos + 8 > blob.Length) return false;
        int tc = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(pos));
        if (tc is < 0 or > 8) return false;
        int nl = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(pos + 4));
        if (nl is < 1 or > 128 || pos + 8 + nl > blob.Length) return false;
        if (blob[pos + 8 + nl - 1] != 0) return false;
        for (int i = 0; i < nl - 1; i++)
            if (blob[pos + 8 + i] is < 32 or >= 127) return false;
        return true;
    }

    while (!r.AtEnd)
    {
        // resync over inter-record padding (observed runs of zeros)
        while (!r.AtEnd && !LooksLikeRecord(r.Position)) r.Skip(1);
        if (r.AtEnd) break;

        int recStart = r.Position;
        r.I32();                    // attribute type code (varies)
        int nameLen = r.I32();
        string name = Encoding.ASCII.GetString(r.Bytes(nameLen)).TrimEnd('\0').Trim();
        int elemType = r.I32();     // 1=float3, 2=float2, 3=float, 0=color, 5=bool, 6=string
        int count = r.I32();
        int seg = r.I32();
        int stride = r.I32();
        int count2 = r.I32();
        // v5: multi-UV meshes mark TextureCoordinate with seg = number of UV
        // channels while count2 == count; the record still holds count*stride
        // bytes (channel 0). Accept any seg.
        if (elemType != 6 && count2 != count)
            throw new Exception($"unexpected attribute record shape for {name}: elem={elemType} count={count} seg={seg} stride={stride} count2={count2}");

        if (elemType == 6) // FString blob: count2 = total byte length, null-separated
        {
            r.Bytes(count2); // ImportedMaterialSlotName values (also in StaticMaterials props)
            continue;
        }

        var data = r.Bytes((long)count * stride);
        if (seg > 1) Console.Error.WriteLine($"[md425] multi-UV attr '{name}': seg={seg} count={count} (channel 0)");
        switch (name)
        {
            case "Position" when count == numVerts && stride == 12:
                position = new float[count * 3];
                Buffer.BlockCopy(data, 0, position, 0, data.Length);
                break;
            case "TextureCoordinate" when count == numVI && stride == 8:
                texCoord = new float[count * 2];
                Buffer.BlockCopy(data, 0, texCoord, 0, data.Length);
                break;
            case "Normal" when count == numVI && stride == 12:
                normal = new float[count * 3];
                Buffer.BlockCopy(data, 0, normal, 0, data.Length);
                break;
        }
    }

    if (position == null) throw new Exception("no Position attribute");
    if (numVI % 3 != 0 || numVI / 3 != numPolys)
        throw new Exception($"non-triangle polygons (VI={numVI}, polys={numPolys}) not supported");
    if (texCoord == null) texCoord = new float[numVI * 2]; // no UVs: zeros

    // corners are ordered by construction: corner c -> vertex instance c
    var indices = new uint[numVI];
    var cornerSlots = new int[numVI];
    for (int p = 0; p < numPolys; p++)
    {
        int slot = Math.Min(Math.Max(polyGroup[p], 0), Math.Max(numGroups - 1, 0));
        for (int c = 0; c < 3; c++)
        {
            int vi = p * 3 + c;
            indices[vi] = (uint)vi;
            cornerSlots[vi] = slot;
        }
    }

    // ---- material slot names from the StaticMaterials property ----
    var slots = new List<string>();
    if (mesh.Properties != null)
    {
        if (mesh.Properties.FirstOrDefault(p => p.Name.Text == "StaticMaterials")?.Tag?.GenericValue
            is UScriptArray smArr)
        {
            foreach (var item in smArr.Properties)
            {
                string slotName = "";
                if (item.GenericValue is FScriptStruct fs && fs.StructType is FStructFallback fb)
                    slotName = fb.GetOrDefault<FName>("MaterialSlotName", new FName("")).Text;
                slots.Add(string.IsNullOrEmpty(slotName) ? "Material" : slotName);
            }
        }
        else if (mesh.Properties.FirstOrDefault(p => p.Name.Text == "Materials")?.Tag?.GenericValue
            is UScriptArray matArr)
        {
            foreach (var item in matArr.Properties)
            {
                var n = item.GenericValue is FPackageIndex pi ? pi.ResolvedObject?.Name.Text : null;
                slots.Add(string.IsNullOrEmpty(n) ? "Material" : n);
            }
        }
    }
    if (slots.Count == 0) slots.Add("Material");
    while (slots.Count < numGroups) slots.Add($"Material{slots.Count}");

    return (position, texCoord, normal ?? new float[numVI * 3], indices, slots.ToArray(), cornerSlots);
}

// ---------------------------------------------------------------------------
// Material slots + material instance parameters
// ---------------------------------------------------------------------------

static List<SlotInfo> GetMeshSlots(UStaticMesh mesh)
{
    var result = new List<SlotInfo>();
    var smProp = mesh.Properties?.FirstOrDefault(p => p.Name.Text == "StaticMaterials");
    if (smProp?.Tag?.GenericValue is not UScriptArray arr) return result;
    int idx = 0;
    foreach (var item in arr.Properties)
    {
        if (item.GenericValue is not FScriptStruct fss || fss.StructType is not FStructFallback fb) continue;
        var si = new SlotInfo
        {
            Index = idx++,
            SlotName = fb.GetOrDefault<FName>("MaterialSlotName", new FName("")).Text,
            ImportedName = fb.GetOrDefault<FName>("ImportedMaterialSlotName", new FName("")).Text,
        };
        if (fb.GetOrDefault<FPackageIndex?>("MaterialInterface", null) is { } mi)
        {
            si.MaterialIndex = mi;
            si.MaterialPath = mi.ResolvedObject?.GetPathName() is { } full && full.Contains('.')
                ? full[..full.LastIndexOf('.')] // strip the .Object suffix
                : null;
            si.MaterialName = mi.ResolvedObject?.Name.Text;
        }
        result.Add(si);
    }
    return result;
}

static MaterialParams ParseMiParams(UMaterialInstanceConstant mic, string path)
{
    var mp = new MaterialParams
    {
        Path = path,
        Name = path.After2('/'),
        ParentPath = mic.GetOrDefault<FPackageIndex?>("Parent", null)?.ResolvedObject?.GetPathName() is { } pp && pp.Contains('.')
            ? pp[..pp.LastIndexOf('.')]
            : null,
    };

    // Walk mic.Properties directly — TryGetValue<UScriptArray> does not convert
    // array properties in this CUE4Parse build (dump-verified).
    foreach (var prop in mic.Properties)
    {
        if (prop.Tag?.GenericValue is not UScriptArray arr) continue;
        switch (prop.Name.Text)
        {
            case "TextureParameterValues":
                foreach (var it in arr.Properties)
                {
                    if (it.GenericValue is not FScriptStruct fss || fss.StructType is not FStructFallback fb) continue;
                    var name = ParamName(fb);
                    var tex = fb.Properties.FirstOrDefault(x => x.Name.Text == "ParameterValue")?.Tag?.GenericValue;
                    var tp = (tex as FPackageIndex)?.ResolvedObject?.GetPathName();
                    if (name.Length > 0 && tp != null && tp.Contains('.'))
                        mp.Textures[name] = tp[..tp.LastIndexOf('.')];
                }
                break;
            case "ScalarParameterValues":
                foreach (var it in arr.Properties)
                {
                    if (it.GenericValue is not FScriptStruct fss || fss.StructType is not FStructFallback fb) continue;
                    var name = ParamName(fb);
                    var gv = fb.Properties.FirstOrDefault(x => x.Name.Text == "ParameterValue")?.Tag?.GenericValue;
                    if (name.Length > 0 && gv != null)
                    {
                        try { mp.Scalars[name] = Convert.ToDouble(gv); }
                        catch (FormatException) { }
                    }
                }
                break;
            case "VectorParameterValues":
                foreach (var it in arr.Properties)
                {
                    if (it.GenericValue is not FScriptStruct fss || fss.StructType is not FStructFallback fb) continue;
                    var name = ParamName(fb);
                    var gv = fb.Properties.FirstOrDefault(x => x.Name.Text == "ParameterValue")?.Tag?.GenericValue;
                    if (name.Length > 0 && gv is FLinearColor lc)
                        mp.Vectors[name] = new[] { lc.R, lc.G, lc.B, lc.A };
                }
                break;
        }
    }
    return mp;
}

// parameter name lives inside ParameterInfo: UE4.25 FMaterialParameterInfo
// = { FName Index; byte Association; int32 LayerIndex } — dump-verified.
static string ParamName(FStructFallback fb)
{
    if (fb.GetOrDefault<FStructFallback?>("ParameterInfo", null) is { } pinfo)
    {
        var n = pinfo.GetOrDefault<FName>("Index", new FName("")).Text;
        if (n.Length > 0) return n;
        n = pinfo.GetOrDefault<FName>("Name", new FName("")).Text;
        if (n.Length > 0) return n;
    }
    return fb.GetOrDefault<FName>("ParameterName", new FName("")).Text;
}

// ---------------------------------------------------------------------------
// Texture extraction from uncooked editor packages
// ---------------------------------------------------------------------------

static (byte[] png, int w, int h) ExtractTexturePng(CUE4Parse.UE4.Assets.IPackage package, UTexture2D tex, GameFile file, VersionContainer versions)
{
    if (package is not CUE4Parse.UE4.Assets.Package pkg)
        throw new Exception("package is not a CUE4Parse.UE4.Assets.Package");

    var expIdx = Array.FindIndex(pkg.ExportMap, e => e.ObjectName.Text == tex.Name);
    if (expIdx < 0) throw new Exception("texture export not found in export map");
    var exp = pkg.ExportMap[expIdx];

    var rawBytes = file is CUE4Parse.FileProvider.Objects.OsGameFile osf
        ? File.ReadAllBytes(osf.ActualFile.FullName)
        : file.Read();
    var baseAr = new FByteArchive(file.Path, rawBytes, versions);
    var ar = new FAssetArchive(baseAr, pkg);
    ar.Position = exp.SerialOffset;
    long serialEnd = exp.SerialOffset + exp.SerialSize;

    if (ar.Ver >= EUnrealEngineObjectUE5Version.PROPERTY_TAG_EXTENSION_AND_OVERRIDABLE_SERIALIZATION)
    {
        var ctrl = ar.Read<EClassSerializationControlExtension>();
        if (ctrl.HasFlag(EClassSerializationControlExtension.OverridableSerializationInformation))
            ar.Position += 1;
    }

    // locate the Source struct property blob; capture header fields from its tags
    long srcStart = -1; int srcLen = 0;
    int srcW = 0, srcH = 0;
    string srcFormatName = "";
    while (true)
    {
        var tag = new FPropertyTag(ar, false);
        if (tag.Name.IsNone) break;
        long dataStart = ar.Position;
        if (tag.Name.Text == "Source" && tag.PropertyType.Text == "StructProperty")
        {
            srcStart = dataStart;
            srcLen = tag.Size;
        }
        ar.Position = dataStart + tag.Size;
        if (ar.Position > serialEnd) throw new Exception("property parse overran export bounds");
    }
    if (srcStart < 0) throw new Exception("no Source property on editor texture");

    // walk the Source blob; extract scalar values straight from the skipped
    // data bytes (avoids the struct-value parser, which chokes on raw ids)
    {
        var sAr = new FAssetArchive(new FByteArchive($"{tex.Name}.Source", rawBytes, versions), pkg);
        sAr.Position = srcStart;
        long p = srcStart;
        while (p < srcStart + srcLen)
        {
            sAr.Position = p;
            var t = new FPropertyTag(sAr, false);
            if (t.Name.IsNone) break;
            long ds = sAr.Position;
            switch (t.Name.Text)
            {
                case "SizeX" when t.Size == 4:
                    srcW = BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan((int)ds));
                    break;
                case "SizeY" when t.Size == 4:
                    srcH = BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan((int)ds));
                    break;
                case "Format" when t.Size == 8:
                {
                    int nameIdx = BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan((int)ds));
                    if (nameIdx >= 0 && nameIdx < pkg.NameMap.Length)
                        srcFormatName = pkg.NameMap[nameIdx].Name ?? "";
                    break;
                }
            }
            p = ds + t.Size;
        }
    }
    if (srcW <= 0 || srcH <= 0) throw new Exception($"bad Source dimensions {srcW}x{srcH}");

    // ---- locate mip payloads in the file tail (after property list + guid) ----
    if (ar.ReadBoolean()) ar.Position += 16;
    long tailStart = ar.Position;

    // PNG fast path: editor textures with bPNGCompressed store whole PNGs inline
    var pngStream = FindLargestPng(rawBytes, (int)exp.SerialOffset, srcW, srcH);
    if (pngStream != null)
        return pngStream.Value;

    // raw path: scan for the FByteBulkData header (CUE4Parse natively decodes
    // UE's chunked zlib format once the header is located). The header may sit
    // a few bytes after the guid due to per-version layout differences.
    byte[]? assembled = null;
    for (int off = -16; off < 1024; off++)
    {
        long pos = tailStart + off;
        if (pos < 0 || pos + 32 > rawBytes.Length) continue;
        try
        {
            var probe = new FAssetArchive(new FByteArchive("scan", rawBytes, versions), pkg);
            probe.Position = pos;
            var bulk = new FByteBulkData(probe);
            var hd = bulk.Header;
            if (hd.ElementCount is < (1 << 19) or > (1 << 29)) continue;
            if (hd.SizeOnDisk < hd.ElementCount / 16 || hd.SizeOnDisk > hd.ElementCount * 1.2) continue;
            var data = bulk.Data;
            if (data == null || data.Length != hd.ElementCount) continue;
            assembled = data;
            break;
        }
        catch (Exception) { /* keep scanning */ }
    }
    if (assembled == null)
        // v5: UE5 texture tail may hold the source payload in the legacy
        // compressed-chunk format (magic 0x9E2A83C1 right after the last export)
        {
            long lastExportEnd = 0;
            foreach (var e3 in pkg.ExportMap)
                lastExportEnd = Math.Max(lastExportEnd, e3.SerialOffset + e3.SerialSize);

            int magic = unchecked((int)0x9E2A83C1u);
            for (long p = 8; p < rawBytes.Length - 16; p++)
            {
                if (BinaryPrimitives.ReadInt32LittleEndian(rawBytes.AsSpan((int)p)) != magic) continue;

                try
                {
                    var legacy = DecodeLegacyCompressed(rawBytes, p);
                    if (legacy == null || legacy.Length < 64) continue;
                    Console.Error.WriteLine($"[tex-ue5] legacy payload decoded: {legacy.Length} bytes from 0x{p:X}, head={BitConverter.ToString(legacy, 0, 8)}");
                    var pngL = FindLargestPng(legacy, 0, srcW, srcH);
                    if (pngL != null) return pngL.Value;
                    var fmtL = InferPixelFormat(srcW, srcH, legacy.Length, srcFormatName);
                    var fiL = PixelFormatUtils.PixelFormats.TryGetValue(fmtL, out var fL)
                        ? fL : throw new Exception($"pixel format {fmtL} has no format info");
                    if (fL.BlockBytes == 0) throw new Exception($"pixel format {fmtL} not supported");
                    int bxL = (srcW + fL.BlockSizeX - 1) / fL.BlockSizeX;
                    int byL = (srcH + fL.BlockSizeY - 1) / fL.BlockSizeY;
                    int expL = bxL * byL * fL.BlockBytes;
                    Console.Error.WriteLine($"[tex-ue5] legacy fmt={fmtL} len={legacy.Length} expected={expL}");
                    if (legacy.Length >= expL)
                    {
                        var mipL = legacy.AsSpan(0, expL).ToArray();
                        typeof(UTexture).GetProperty("Format")!.SetValue(tex, fmtL);
                        tex.PlatformData.Mips = new FTexture2DMipMap[] { new(new FByteArrayData(mipL), srcW, srcH, 1) };
                        var decL = tex.Decode();
                        if (decL != null)
                            return (decL.Encode(ETextureFormat.Png, false, out _), decL.Width, decL.Height);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tex-ue5] legacy probe @0x{p:X}: {ex.Message}");
                }
            }
        }

        // v4: UE5 virtualized editor textures — payload referenced by an
        // FEditorBulkData header near the end of the texture export region:
        // {flags u32, guid 16B, sha 32B, payloadSize i64, offsetInFile i64}
        for (long p = assembled == null ? exp.SerialOffset + exp.SerialSize - 128 : long.MaxValue;
             p < exp.SerialOffset + exp.SerialSize - 68; p++)
        {
            if (p < 0 || p + 68 > rawBytes.Length) continue;
            try
            {
                long h = p + 4 + 16 + 32; // after flags+guid+sha
                long payloadSize = BinaryPrimitives.ReadInt64LittleEndian(rawBytes.AsSpan((int)h));
                long offsetInFile = BinaryPrimitives.ReadInt64LittleEndian(rawBytes.AsSpan((int)(h + 8)));
                if (payloadSize is <= 0 or > (1L << 28)) continue;
                if (offsetInFile <= 0 || offsetInFile >= rawBytes.Length) continue;
                var blob = DecodeCompressedBuffer(rawBytes, offsetInFile);
                if (blob == null || blob.Length != payloadSize)
                {
                    var rh = offsetInFile < rawBytes.Length - 16 ? BitConverter.ToString(rawBytes, (int)offsetInFile, 16) : "?";
                    Console.Error.WriteLine($"[tex-ue5] decoded {blob?.Length.ToString() ?? "null"} != payloadSize {payloadSize} @0x{p:X} raw@0x{offsetInFile:X}: {rh}");
                    continue;
                }
                Console.Error.WriteLine($"[tex-ue5] virtualized payload decoded: {payloadSize} bytes from 0x{offsetInFile:X}");
                var head = blob.Length > 16 ? BitConverter.ToString(blob, 0, 16) : "";
                Console.Error.WriteLine($"[tex-ue5] blob head: {head}");
                // PNG fast path inside the decoded source payload
                var png2 = FindLargestPng(blob, 0, srcW, srcH);
                if (png2 != null) return png2.Value;
                // raw mip data
                var fmt2 = InferPixelFormat(srcW, srcH, blob.Length, srcFormatName);
                var fi2 = PixelFormatUtils.PixelFormats.TryGetValue(fmt2, out var f2)
                    ? f2 : throw new Exception($"pixel format {fmt2} has no format info");
                if (f2.BlockBytes == 0) throw new Exception($"pixel format {fmt2} not supported");
                int bx2 = (srcW + f2.BlockSizeX - 1) / f2.BlockSizeX;
                int by2 = (srcH + f2.BlockSizeY - 1) / f2.BlockSizeY;
                int expected2 = bx2 * by2 * f2.BlockBytes;
                Console.Error.WriteLine($"[tex-ue5] fmt={fmt2} blob={blob.Length} expected={expected2}");
                if (blob.Length < expected2) throw new Exception($"virtualized source too small: {blob.Length} < {expected2}");
                var mip2 = blob.AsSpan(0, expected2).ToArray();
                typeof(UTexture).GetProperty("Format")!.SetValue(tex, fmt2);
                tex.PlatformData.Mips = new FTexture2DMipMap[] { new(new FByteArrayData(mip2), srcW, srcH, 1) };
                var dec2 = tex.Decode();
                if (dec2 == null) throw new Exception("texture decode returned null");
                var pngOut = dec2.Encode(ETextureFormat.Png, false, out _);
                return (pngOut, dec2.Width, dec2.Height);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[tex-ue5] probe @0x{p:X}: {ex.Message}");
            }
        }
        // original v1 downstream: decode the assembled bulk payload
        if (assembled != null)
        {
            if (assembled.Length > 8 && assembled[0] == 0x89 && assembled[1] == 0x50 &&
                assembled[2] == 0x4E && assembled[3] == 0x47)
            {
                var pngA = ExtractFirstPng(assembled);
                if (pngA != null) return pngA.Value;
            }
            var fmtA = InferPixelFormat(srcW, srcH, assembled.Length, srcFormatName);
            var fiA = PixelFormatUtils.PixelFormats.TryGetValue(fmtA, out var fA)
                ? fA : throw new Exception($"pixel format {fmtA} has no format info");
            if (fiA.BlockBytes == 0) throw new Exception($"pixel format {fmtA} not supported");
            int bxA = (srcW + fiA.BlockSizeX - 1) / fiA.BlockSizeX;
            int byA = (srcH + fiA.BlockSizeY - 1) / fiA.BlockSizeY;
            int expectedA = bxA * byA * fiA.BlockBytes;
            int scaleA = 1;
            while (scaleA < 16 && assembled.Length < expectedA / (scaleA * scaleA)) scaleA *= 2;
            if (scaleA > 1)
            {
                int wA = Math.Max(4, srcW / scaleA), hA = Math.Max(4, srcH / scaleA);
                int bxA2 = (wA + fiA.BlockSizeX - 1) / fiA.BlockSizeX;
                int byA2 = (hA + fiA.BlockSizeY - 1) / fiA.BlockSizeY;
                int expA2 = bxA2 * byA2 * fiA.BlockBytes;
                if (assembled.Length >= expA2)
                {
                    Console.Error.WriteLine($"[tex] falling back to {wA}x{hA} (payload {assembled.Length} < {expectedA})");
                    var mipA2 = assembled.AsSpan(0, expA2).ToArray();
                    typeof(UTexture).GetProperty("Format")!.SetValue(tex, fmtA);
                    tex.PlatformData.Mips = new FTexture2DMipMap[] { new(new FByteArrayData(mipA2), wA, hA, 1) };
                    var decA2 = tex.Decode();
                    if (decA2 != null)
                        return (decA2.Encode(ETextureFormat.Png, false, out _), decA2.Width, decA2.Height);
                }
            }
            if (assembled.Length < expectedA)
                throw new Exception($"assembled mip payload {assembled.Length} < expected {expectedA} for {fmtA}");
            var mipA = assembled.AsSpan(0, expectedA).ToArray();
            typeof(UTexture).GetProperty("Format")!.SetValue(tex, fmtA);
            tex.PlatformData.Mips = new FTexture2DMipMap[] { new(new FByteArrayData(mipA), srcW, srcH, 1) };
            var decA = tex.Decode();
            if (decA == null) throw new Exception("texture decode returned null");
            var pngOutA = decA.Encode(ETextureFormat.Png, false, out _);
            return (pngOutA, decA.Width, decA.Height);
        }
        throw new Exception("no mip bulk payload found in file tail");

    // the assembled payload can itself be a stored PNG (bPNGCompressed with the
    // chunks holding the literal PNG file bytes)
    if (assembled.Length > 8 && assembled[0] == 0x89 && assembled[1] == 0x50 &&
        assembled[2] == 0x4E && assembled[3] == 0x47)
    {
        var png1 = ExtractFirstPng(assembled);
        if (png1 != null) return png1.Value;
    }

    // otherwise: raw mip data (possibly a full mip chain, mip0 first)
    var fmt = InferPixelFormat(srcW, srcH, assembled.Length, srcFormatName);
    var formatInfo = PixelFormatUtils.PixelFormats.TryGetValue(fmt, out var fi)
        ? fi : throw new Exception($"pixel format {fmt} has no format info");
    if (formatInfo.BlockBytes == 0) throw new Exception($"pixel format {fmt} not supported");
    int blocksX = (srcW + formatInfo.BlockSizeX - 1) / formatInfo.BlockSizeX;
    int blocksY = (srcH + formatInfo.BlockSizeY - 1) / formatInfo.BlockSizeY;
    int expected = blocksX * blocksY * formatInfo.BlockBytes;
    if (assembled.Length < expected)
        throw new Exception($"assembled mip payload {assembled.Length} < expected {expected} for {fmt}");

    var mipData = assembled.AsSpan(0, expected).ToArray();
    var formatProp = typeof(UTexture).GetProperty("Format");
    formatProp!.SetValue(tex, fmt);
    tex.PlatformData.Mips = new FTexture2DMipMap[]
    {
        new(new FByteArrayData(mipData), srcW, srcH, 1)
    };
    var decoded = tex.Decode();
    if (decoded == null) throw new Exception("texture decode returned null");
    var png = decoded.Encode(ETextureFormat.Png, false, out _);
    return (png, decoded.Width, decoded.Height);
}

// infer the stored pixel format from the assembled mip size + source format
static EPixelFormat InferPixelFormat(int w, int h, int bytes, string sourceFormat)
{
    long px = (long)w * h;
    if (bytes >= px && bytes < px * 2) return EPixelFormat.PF_BC5;   // 1 B/px + mip chain included
    if (bytes == px * 4)
        return sourceFormat.Contains("BGRA", StringComparison.OrdinalIgnoreCase)
            ? EPixelFormat.PF_B8G8R8A8 : EPixelFormat.PF_R8G8B8A8;
    if (bytes == px) return EPixelFormat.PF_G8;
    if (bytes == (px + 1) / 2) return EPixelFormat.PF_DXT1;
    if (bytes == px * 8) return EPixelFormat.PF_FloatRGBA;
    if (bytes == (px + 3) / 4 * 16) return EPixelFormat.PF_BC7;
    if (bytes == px * 2) return EPixelFormat.PF_BC5;
    if (bytes == (px + 7) / 8) return EPixelFormat.PF_BC4;
    return sourceFormat.Contains("BGRA", StringComparison.OrdinalIgnoreCase)
        ? EPixelFormat.PF_B8G8R8A8 : EPixelFormat.PF_R8G8B8A8;
}

// extract the first complete PNG stream (magic..IEND) from a byte range
static (byte[] png, int w, int h)? ExtractFirstPng(byte[] raw, int from = 0)
{
    for (int i = from; i <= raw.Length - 8; i++)
    {
        if (raw[i] != 0x89 || raw[i + 1] != 0x50 || raw[i + 2] != 0x4E || raw[i + 3] != 0x47
            || raw[i + 4] != 0x0D || raw[i + 5] != 0x0A || raw[i + 6] != 0x1A || raw[i + 7] != 0x0A)
            continue;
        int w = 0, h = 0;
        int p = i + 8;
        bool done = false;
        while (p + 8 <= raw.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p));
            if (len < 0) break;
            int type = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p + 4));
            if (type == 0x49484452 && p + 21 <= raw.Length) // IHDR
            {
                w = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p + 8));
                h = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p + 12));
            }
            p += 12 + len;
            if (type == 0x49454E44) { done = true; break; } // IEND
            if (p > raw.Length) break;
        }
        if (done && w > 0)
            return (raw[i..p], w, h);
    }
    return null;
}

// extract the largest complete PNG stream embedded in the file (mip0 of
// bPNGCompressed editor textures is stored as a literal PNG)
static (byte[] png, int w, int h)? FindLargestPng(byte[] raw, int from, int srcW, int srcH)
{
    (byte[] png, int w, int h, int len)? best = null;
    for (int i = from; i <= raw.Length - 8; i++)
    {
        if (raw[i] != 0x89 || raw[i + 1] != 0x50 || raw[i + 2] != 0x4E || raw[i + 3] != 0x47
            || raw[i + 4] != 0x0D || raw[i + 5] != 0x0A || raw[i + 6] != 0x1A || raw[i + 7] != 0x0A)
            continue;
        int w = 0, h = 0;
        int p = i + 8;
        bool done = false;
        while (p + 8 <= raw.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p));
            if (len < 0) break;
            int type = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p + 4));
            if (type == 0x49484452 && p + 21 <= raw.Length) // IHDR
            {
                w = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p + 8));
                h = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(p + 12));
            }
            p += 12 + len;
            if (type == 0x49454E44) { done = true; break; } // IEND
            if (p > raw.Length) break;
        }
        if (done && w > 0 && (best == null || p - i > best.Value.len))
            best = (raw[i..p], w, h, p - i);
    }
    if (best == null) return null;
    return (best.Value.png, best.Value.w, best.Value.h);
}

// ---------------------------------------------------------------------------
// Material binding: categorization + resolution + glTF-convention MR swizzle
// ---------------------------------------------------------------------------

static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

static string? FindTextureParam(MaterialParams mp, params string[] keys)
{
    foreach (var key in keys)
    {
        foreach (var (k, v) in mp.Textures)
        {
            var n = Norm(k);
            if (n == key || (key.Length >= 3 && n.EndsWith(key, StringComparison.Ordinal)))
                return v;
        }
    }
    return null;
}

static SlotBinding? ResolveBinding(MaterialParams? mp, string outDir, string gltfDir)
{
    if (mp == null) return null;
    var b = new SlotBinding { MaterialPath = mp.Path, MaterialName = mp.Name };

    string? ToPng(string? gamePath)
    {
        if (gamePath == null) return null;
        var rel = gamePath.StartsWith("/Game/") ? gamePath[6..] : gamePath;
        // texture output layout depends on the provider root: try both with and
        // without the top-level content folder name (e.g. AmericanCityPacks/)
        var parts = rel.Split('/').Skip(1).ToArray(); // drop first segment
        var cands = new[]
        {
            rel.Replace('/', Path.DirectorySeparatorChar) + ".png",
            parts.Length > 0 ? Path.Combine(parts) + ".png" : null
        };
        foreach (var c in cands)
        {
            if (c == null) continue;
            var abs = Path.Combine(outDir, "Textures", c);
            if (File.Exists(abs)) return abs;
        }
        return null;
    }

    b.BaseColorPng = ToPng(FindTextureParam(mp, "basecolor", "albedo", "diffuse"));
    b.NormalPng = ToPng(FindTextureParam(mp, "normaltexture", "normalmap", "normal", "bump"));
    b.EmissivePng = ToPng(FindTextureParam(mp, "emissivetexture", "emissive", "emission", "glow"));
    var packed = ToPng(FindTextureParam(mp, "ormhtexture", "ormtexture", "ormh", "orm", "mra", "mratexture",
        "metallicroughness", "metallicroughnesstexture", "packedtexture", "packed"));
    if (packed != null)
    {
        // Channel packing of this pack's <name>_M / ORMH maps (deduced from
        // T_DumpsterContainer_M channel statistics): R = metallic, G = roughness,
        // B = AO (A unused). glTF wants G=roughness, B=metallic -> swizzle and
        // store next to the original as <orig>_MR.png (also usable as
        // occlusionTexture: R = AO).
        b.MetallicRoughnessPng = CreateMrSwizzle(packed);
        b.OcclusionPng = b.MetallicRoughnessPng; // R channel of the swizzled copy = AO
    }
    foreach (var (k, v) in mp.Scalars)
    {
        var n = Norm(k);
        if (n is "metallic" or "metallicfactor") b.Metallic = v;
        else if (n is "roughness" or "roughnessfactor") b.Roughness = v;
    }
    return b;
}

// swizzle a packed _M/_ORMH png (R=metallic, G=roughness, B=AO) into a
// glTF-convention metallicRoughness texture (G=roughness, B=metallic, R=AO)
static string? CreateMrSwizzle(string origPng)
{
    var mrPath = Path.Combine(Path.GetDirectoryName(origPng)!,
        Path.GetFileNameWithoutExtension(origPng) + "_MR.png");
    if (File.Exists(mrPath)) return mrPath;
    try
    {
        using var src = SKBitmap.Decode(origPng);
        if (src == null) return null;
        var sb = src.Bytes;
        int bpp = src.BytesPerPixel;
        bool bgra = src.ColorType == SKColorType.Bgra8888;
        var db = new byte[sb.Length];
        long pxCount = (long)src.Width * src.Height;
        for (long i = 0; i < pxCount; i++)
        {
            int o = (int)(i * bpp);
            byte r = bgra ? sb[o + 2] : sb[o + 0];
            byte g = sb[o + 1];
            byte bl = bgra ? sb[o + 0] : sb[o + 2];
            int d = (int)(i * 4); // output is always RGBA8888
            db[d + 0] = bl;       // R = AO
            db[d + 1] = g;        // G = roughness
            db[d + 2] = r;        // B = metallic
            db[d + 3] = 255;
        }
        using var dst = new SKBitmap();
        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(db, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            dst.InstallPixels(info, handle.AddrOfPinnedObject());
            using var img = SKImage.FromBitmap(dst);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            using var pngFs = File.Create(mrPath);
            data.SaveTo(pngFs);
        }
        finally { handle.Free(); }
        return mrPath;
    }
    catch (Exception)
    {
        return null;
    }
}

// ---------------------------------------------------------------------------
// materials.json
// ---------------------------------------------------------------------------

static void WriteMaterialsJson(string path, List<(string MeshName, string PkgPath, List<SlotInfo> Slots)> meshes,
    List<MaterialParams> materials)
{
    using var fs = File.Create(path);
    using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    void WriteMaterialParams(MaterialParams mp)
    {
        w.WriteStartObject();
        w.WriteString("path", mp.Path);
        w.WriteString("name", mp.Name);
        if (mp.ParentPath != null) w.WriteString("parent", mp.ParentPath);
        w.WriteStartObject("textureParameters");
        foreach (var (k, v) in mp.Textures) w.WriteString(k, v);
        w.WriteEndObject();
        w.WriteStartObject("scalarParameters");
        foreach (var (k, v) in mp.Scalars) w.WriteNumber(k, v);
        w.WriteEndObject();
        w.WriteStartObject("vectorParameters");
        foreach (var (k, v) in mp.Vectors)
        {
            w.WriteStartArray(k);
            foreach (var c in v) w.WriteNumberValue(c);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    w.WriteStartObject();
    w.WriteStartArray("meshes");
    foreach (var (meshName, pkgPath, slots) in meshes)
    {
        w.WriteStartObject();
        w.WriteString("name", meshName);
        w.WriteString("packagePath", pkgPath);
        w.WriteStartArray("slots");
        foreach (var s in slots)
        {
            w.WriteStartObject();
            w.WriteNumber("slotIndex", s.Index);
            w.WriteString("slotName", s.SlotName);
            w.WriteString("importedSlotName", s.ImportedName);
            if (s.MaterialPath != null) w.WriteString("materialPath", s.MaterialPath);
            if (s.MaterialName != null) w.WriteString("materialName", s.MaterialName);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
    w.WriteEndArray();

    w.WriteStartArray("materials");
    foreach (var mp in materials) WriteMaterialParams(mp);
    w.WriteEndArray();
    w.WriteEndObject();
}

// ---------------------------------------------------------------------------
// Minimal glTF 2.0 writer
// ---------------------------------------------------------------------------

static void WriteGltf(string outPath, string meshName, float[] positions, float[] uvs,
    float[] normals, uint[] indices, string[] slotNames, int[] cornerSlots, SlotBinding?[] bindings)
{
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    var binPath = Path.ChangeExtension(outPath, ".bin");
    var gltfDir = Path.GetDirectoryName(outPath)!;

    // split primitives per material slot
    int numSlots = Math.Max(1, slotNames.Length);
    var perSlot = new List<uint>[numSlots];
    for (int i = 0; i < numSlots; i++) perSlot[i] = new List<uint>();
    for (int t = 0; t < indices.Length; t++)
        perSlot[Math.Min(cornerSlots[t], numSlots - 1)].Add(indices[t]);

    using var bin = new MemoryStream();
    var views = new List<(int offset, int length, int target)>();

    void Append(ReadOnlySpan<byte> bytes, int target)
    {
        bin.Align4();
        views.Add(((int)bin.Length, bytes.Length, target));
        bin.Write(bytes);
    }

    var posBytes = new byte[positions.Length * 4];
    Buffer.BlockCopy(positions, 0, posBytes, 0, posBytes.Length);
    Append(posBytes, 34962);

    var nrmBytes = new byte[normals.Length * 4];
    Buffer.BlockCopy(normals, 0, nrmBytes, 0, nrmBytes.Length);
    Append(nrmBytes, 34962);

    var uvBytes = new byte[uvs.Length * 4];
    Buffer.BlockCopy(uvs, 0, uvBytes, 0, uvBytes.Length);
    Append(uvBytes, 34962);

    var idxBytes = new byte[indices.Length * 4];
    for (int i = 0; i < indices.Length; i++)
        BinaryPrimitives.WriteUInt32LittleEndian(idxBytes.AsSpan(i * 4), indices[i]);
    Append(idxBytes, 34963);

    File.WriteAllBytes(binPath, bin.ToArray());

    var posView = views[0]; var nrmView = views[1]; var uvView = views[2]; var idxView = views[3];

    // ---- texture/image/sampler tables from the per-slot bindings ----
    // One texture per image; texture index == image index == position in both lists.
    var images = new List<string>();            // relative URIs
    var texOfImage = new List<int>();           // texture index -> image index
    int Tex(string? absPath)
    {
        if (absPath == null || !File.Exists(absPath)) return -1;
        var uri = Path.GetRelativePath(gltfDir, absPath).Replace('\\', '/');
        var ii = images.IndexOf(uri);
        if (ii < 0) { images.Add(uri); ii = images.Count - 1; texOfImage.Add(ii); }
        return ii;
    }

    var slotTex = new List<(int? baseColor, int? normal, int? emissive, int? mr, int? occl)>();
    var slotFactors = new List<(double? metallic, double? roughness)>();
    foreach (var bnd in bindings)
    {
        int bc = -1, nm = -1, em = -1, mr = -1, oc = -1;
        if (bnd != null)
        {
            bc = Tex(bnd.BaseColorPng);
            nm = Tex(bnd.NormalPng);
            em = Tex(bnd.EmissivePng);
            mr = Tex(bnd.MetallicRoughnessPng);
            oc = Tex(bnd.OcclusionPng);
        }
        slotTex.Add((bc < 0 ? null : bc, nm < 0 ? null : nm, em < 0 ? null : em, mr < 0 ? null : mr, oc < 0 ? null : oc));
        slotFactors.Add(bnd == null ? (null, null) : (bnd.Metallic, bnd.Roughness));
    }

    var opts = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    using var fs = File.Create(outPath);
    using var w = new Utf8JsonWriter(fs, opts);

    w.WriteStartObject();
    w.WriteStartObject("asset");
    w.WriteString("version", "2.0");
    w.WriteString("generator", "CityExporter v2 (UE uncooked editor packages; root node: -90deg X rotation + 0.01 scale -> Y-up meters)");
    w.WriteEndObject();

    w.WriteStartArray("scene"); w.WriteNumberValue(0); w.WriteEndArray();
    w.WriteStartArray("scenes");
    w.WriteStartObject();
    w.WriteStartArray("nodes"); w.WriteNumberValue(0); w.WriteEndArray();
    w.WriteEndObject();
    w.WriteEndArray();

    // node 0: root transform converting UE Z-up/cm to glTF Y-up/meters
    // (quaternion -90 deg about X: UE (0,0,1) -> glTF (0,1,0)); node 1: the mesh
    w.WriteStartArray("nodes");
    w.WriteStartObject();
    w.WriteString("name", meshName + "_root");
    w.WriteStartArray("rotation");
    w.WriteNumberValue(-0.7071068); w.WriteNumberValue(0); w.WriteNumberValue(0); w.WriteNumberValue(0.7071068);
    w.WriteEndArray();
    w.WriteStartArray("scale");
    w.WriteNumberValue(0.01); w.WriteNumberValue(0.01); w.WriteNumberValue(0.01);
    w.WriteEndArray();
    w.WriteStartArray("children"); w.WriteNumberValue(1); w.WriteEndArray();
    w.WriteEndObject();
    w.WriteStartObject();
    w.WriteString("name", meshName);
    w.WriteNumber("mesh", 0);
    w.WriteEndObject();
    w.WriteEndArray();

    w.WriteStartArray("materials");
    for (int i = 0; i < numSlots; i++)
    {
        w.WriteStartObject();
        w.WriteString("name", i < slotNames.Length && slotNames[i].Length > 0 ? slotNames[i] : $"Material{i}");
        w.WriteStartObject("pbrMetallicRoughness");
        var (bc, nm, em, mr, oc) = slotTex[i];
        var (metallic, roughness) = slotFactors[i];
        if (bc != null)
        {
            w.WriteStartObject("baseColorTexture");
            w.WriteNumber("index", bc.Value);
            w.WriteNumber("texCoord", 0);
            w.WriteEndObject();
        }
        if (mr != null)
        {
            w.WriteStartObject("metallicRoughnessTexture");
            w.WriteNumber("index", mr.Value);
            w.WriteNumber("texCoord", 0);
            w.WriteEndObject();
        }
        w.WriteNumber("metallicFactor", metallic ?? 1.0);
        w.WriteNumber("roughnessFactor", roughness ?? 1.0);
        w.WriteEndObject();
        if (nm != null)
        {
            w.WriteStartObject("normalTexture");
            w.WriteNumber("index", nm.Value);
            w.WriteNumber("texCoord", 0);
            w.WriteEndObject();
        }
        if (oc != null)
        {
            w.WriteStartObject("occlusionTexture");
            w.WriteNumber("index", oc.Value);
            w.WriteNumber("texCoord", 0);
            w.WriteEndObject();
        }
        if (em != null)
        {
            w.WriteStartObject("emissiveTexture");
            w.WriteNumber("index", em.Value);
            w.WriteNumber("texCoord", 0);
            w.WriteEndObject();
            w.WriteStartArray("emissiveFactor");
            w.WriteNumberValue(1.0); w.WriteNumberValue(1.0); w.WriteNumberValue(1.0);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
    w.WriteEndArray();

    w.WriteStartArray("meshes");
    w.WriteStartObject();
    w.WriteString("name", meshName);
    w.WriteStartArray("primitives");
    for (int i = 0; i < numSlots; i++)
    {
        if (perSlot[i].Count == 0) continue;
        w.WriteStartObject();
        w.WriteStartObject("attributes");
        w.WriteNumber("POSITION", 0);
        w.WriteNumber("NORMAL", 1);
        w.WriteNumber("TEXCOORD_0", 2);
        w.WriteEndObject();
        w.WriteNumber("indices", 3);
        w.WriteNumber("material", i);
        w.WriteEndObject();
    }
    w.WriteEndArray();
    w.WriteEndObject();
    w.WriteEndArray();

    w.WriteStartArray("accessors");
    WriteNumAccessor(w, 0, posView, positions.Length / 3, "VEC3", 5126, positions);
    WriteNumAccessor(w, 1, nrmView, normals.Length / 3, "VEC3", 5126, normals);
    WriteNumAccessor(w, 2, uvView, uvs.Length / 2, "VEC2", 5126, uvs);
    WriteIndexAccessor(w, 3, idxView, indices.Length);
    w.WriteEndArray();

    if (texOfImage.Count > 0)
    {
        w.WriteStartArray("textures");
        foreach (var t in texOfImage)
        {
            w.WriteStartObject();
            w.WriteNumber("sampler", 0);
            w.WriteNumber("source", t);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("images");
        foreach (var uri in images)
            w.WriteStringValue(uri);
        w.WriteEndArray();

        w.WriteStartArray("samplers");
        w.WriteStartObject();
        w.WriteNumber("magFilter", 9729);
        w.WriteNumber("minFilter", 9987);
        w.WriteNumber("wrapS", 10497);
        w.WriteNumber("wrapT", 10497);
        w.WriteEndObject();
        w.WriteEndArray();
    }

    w.WriteStartArray("bufferViews");
    WriteView(w, 0, posView); WriteView(w, 1, nrmView); WriteView(w, 2, uvView); WriteView(w, 3, idxView);
    w.WriteEndArray();

    w.WriteStartArray("buffers");
    w.WriteStartObject();
    w.WriteString("uri", Path.GetFileName(binPath));
    w.WriteNumber("byteLength", bin.Length);
    w.WriteEndObject();
    w.WriteEndArray();

    w.WriteEndObject();
}

static void WriteView(Utf8JsonWriter w, int i, (int offset, int length, int target) v)
{
    w.WriteStartObject();
    w.WriteNumber("buffer", 0);
    w.WriteNumber("byteOffset", v.offset);
    w.WriteNumber("byteLength", v.length);
    w.WriteNumber("target", v.target);
    w.WriteEndObject();
}

static void WriteNumAccessor(Utf8JsonWriter w, int i, (int offset, int length, int target) v,
    int count, string type, int componentType, float[] data)
{
    float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
    float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
    int n = data.Length / 3;
    for (int i0 = 0; i0 < n; i0++)
    {
        float x = data[3 * i0], y = data[3 * i0 + 1], z = data[3 * i0 + 2];
        if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
        if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
    }
    w.WriteStartObject();
    w.WriteNumber("bufferView", i);
    w.WriteNumber("componentType", componentType);
    w.WriteNumber("count", count);
    w.WriteString("type", type);
    if (type == "VEC3")
    {
        w.WriteStartArray("min"); w.WriteNumberValue(minX); w.WriteNumberValue(minY); w.WriteNumberValue(minZ); w.WriteEndArray();
        w.WriteStartArray("max"); w.WriteNumberValue(maxX); w.WriteNumberValue(maxY); w.WriteNumberValue(maxZ); w.WriteEndArray();
    }
    w.WriteEndObject();
}

static void WriteIndexAccessor(Utf8JsonWriter w, int i, (int offset, int length, int target) v, int count)
{
    w.WriteStartObject();
    w.WriteNumber("bufferView", i);
    w.WriteNumber("componentType", 5125); // UNSIGNED_INT
    w.WriteNumber("count", count);
    w.WriteString("type", "SCALAR");
    w.WriteEndObject();
}

// ---------------------------------------------------------------------------
// little sequential reader for the decoded mesh-description blob
// ---------------------------------------------------------------------------

class BlobReader
{
    readonly byte[] _b;
    public int Position;
    public BlobReader(byte[] b) { _b = b; }
    public bool AtEnd => Position >= _b.Length;
    public int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(Position)); Position += 4; return v; }
    public byte[] Bytes(long n)
    {
        if (Position + n > _b.Length) throw new Exception("blob overrun");
        var r = new byte[n];
        Buffer.BlockCopy(_b, Position, r, 0, (int)n);
        Position += (int)n;
        return r;
    }
    public void Skip(long n) => Position += (int)n;
}

static class MemoryStreamExtensions
{
    public static void Align4(this MemoryStream s)
    {
        while (s.Length % 4 != 0) s.WriteByte(0);
    }
}

static class StringExtensions
{
    // substring before the LAST '.' (strips ".ObjectName" from a /Game/ path)
    public static string SubstringBefore2(this string s, char c)
    {
        int i = s.LastIndexOf(c);
        return i >= 0 ? s[..i] : s;
    }
    // substring after the first '/'
    public static string After2(this string s, char c)
    {
        int i = s.IndexOf(c);
        return i >= 0 ? s[(i + 1)..] : s;
    }
}

// ---------------------------------------------------------------------------
// Type declarations (must follow all top-level statements/local functions)
// ---------------------------------------------------------------------------

class SlotInfo
{
    public int Index;
    public string SlotName = "";
    public string ImportedName = "";
    public string? MaterialPath;   // /Game/... path of the referenced MI
    public string? MaterialName;
    public FPackageIndex? MaterialIndex; // for loading the MI object
}

class MaterialParams
{
    public string Path = "";       // /Game/... package path (without extension)
    public string Name = "";
    public string? ParentPath;     // /Game/... of the master material
    public Dictionary<string, string> Textures = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> Scalars = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, float[]> Vectors = new(StringComparer.OrdinalIgnoreCase);
}

class SlotBinding
{
    public string MaterialPath = "";
    public string MaterialName = "";
    public string? BaseColorPng;
    public string? NormalPng;
    public string? EmissivePng;
    public string? MetallicRoughnessPng;
    public string? OcclusionPng;
    public double? Metallic;
    public double? Roughness;
    public string? BaseColorUri, NormalUri, EmissiveUri, MetallicRoughnessUri, OcclusionUri;
}

// big-endian reader over the raw file bytes
class BlobReaderBe
{
    readonly byte[] _b;
    public int Position;
    public BlobReaderBe(byte[] b) { _b = b; }
    public uint U32() { var v = BinaryPrimitives.ReadUInt32BigEndian(_b.AsSpan(Position)); Position += 4; return v; }
    public ulong U64() { var v = BinaryPrimitives.ReadUInt64BigEndian(_b.AsSpan(Position)); Position += 8; return v; }
    public byte U8() => _b[Position++];
    public byte[] Bytes(long n) { var a = _b[Position..(Position + (int)n)]; Position += (int)n; return a; }
    public void Skip(long n) => Position += (int)n;
}

static class OodleNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate long OodleDec(IntPtr comp, int compLen, IntPtr raw, int rawLen,
        int fuzzSafe, int checkCRC, int verbosity, IntPtr decBufBase, long decBufSize,
        IntPtr fpCallback, IntPtr callbackUserData, IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

    static OodleDec? _dec;

    public static long Decompress(IntPtr comp, int compLen, IntPtr raw, int rawLen)
    {
        if (_dec == null)
        {
            var h = LoadLibraryW(Path.Combine(AppContext.BaseDirectory, "oo2core_9_win64.dll"));
            if (h == IntPtr.Zero) throw new Exception("oo2core dll not found next to CityExporter");
            var p = GetProcAddress(h, "OodleLZ_Decompress");
            if (p == IntPtr.Zero) throw new Exception("OodleLZ_Decompress export missing");
            _dec = Marshal.GetDelegateForFunctionPointer<OodleDec>(p);
        }
        return _dec(comp, compLen, raw, rawLen, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);
}

class Ue5Container
{
    public string Name = "";
    public int NumBits; public int NumHoles; public int NumElements;
    public Dictionary<string, Ue5Attr> Attrs = new(StringComparer.OrdinalIgnoreCase);
    public override string ToString() => $"{Name}: bits={NumBits} holes={NumHoles} elems={NumElements} attrs=[{string.Join(",", Attrs.Keys)}]";
}

class Ue5Attr
{
    public string Name = "";
    public uint TypeIdx; public uint Extent;
    public List<byte[]> Channels = new(); // raw payload per channel (bulk, incl count header consumed)
    public int ElemCount; // per channel element count
    public override string ToString() => $"{Name}: t={TypeIdx} ext={Extent} ch={Channels.Count} n={ElemCount}";
}

