using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace QuestTree.QuestGraph
{
    /// <summary>
    /// WP2: the IDENTITY SIDECAR of a capture's 3D mesh, <c>captures/&lt;key&gt;/&lt;key&gt;-mesh.index</c> - what lets the
    /// next capture ADD to the stored mesh instead of rebuilding it: for every stored building, which renderer it was
    /// read from (a 64-bit hash of its scene path, its bounds, its submesh range and its geometry signature), which LOD
    /// group and level, its grade, the budget inputs it was planned with; for every atlas tile, which material it is
    /// and how it was captured; the packer's continuation state; and the SHA-256 of the mesh file it describes.
    ///
    /// Why a sidecar rather than a v4 mesh: every released reader - the host, the transfer, check-capture and the
    /// viewer - refuses a version it does not know, so a format bump is a coordinated host and client release. The
    /// sidecar is only needed by the machine that captures: it is never uploaded (the uploader reads only the files the
    /// meta names), never shipped (the package globs do not match it) and never read by the viewer.
    ///
    /// Binding: the sidecar is trusted only when its <see cref="MeshSha"/> is the mesh file's hash, its building list
    /// is the mesh's building list one for one (<see cref="Mismatch"/>), and the recipe, game version, extent, render
    /// mask and culling knowledge are this capture's. Anything else - no sidecar, an unreadable one, a mismatch - sends
    /// the capture down the from-scratch path, which is the pre-WP2 build exactly, and that capture writes a fresh one.
    ///
    /// One raw-deflate block, little-endian, like <see cref="MapMeshFile"/>; its reader checks every count against its
    /// cap before it allocates and refuses trailing bytes. Unity-free, so the harness round-trips it.
    /// </summary>
    internal sealed class MapMeshIndex
    {
        /// <summary>The layout's version. A change to the byte table below is a new number; the reader refuses others,
        /// which only costs one from-scratch capture. 2 (WP2 fixes 2): each building's triedTarget and triedLevel, each
        /// material's unplacedPages. 3 (WP2 fixes 3): each building's retargetTried and textureTried.</summary>
        internal const int Version = 3;

        /// <summary>The first four bytes inside the deflate block.</summary>
        internal const string Magic = "QTMI";

        /// <summary>The sidecar's file-name suffix, beside <c>&lt;key&gt;-mesh.bin</c>.</summary>
        internal const string Suffix = "-mesh.index";

        /// <summary>The sidecar's file name for a map key.</summary>
        /// <param name="key">The map key.</param>
        internal static string FileNameFor(string key) => key + Suffix;

        /// <summary>Caps on what the reader will allocate: the recipe and game strings' UTF-8 bytes, materials, and
        /// the inflated size (20,000 buildings with 64 ranges each is under 13 MB).</summary>
        internal const int MaxRecipeBytes = 512;

        internal const int MaxGameBytes = 128;
        internal const int MaxMaterials = 16384;
        internal const long MaxInflatedBytes = 64L * 1024 * 1024;

        /// <summary>A material tile's flags.</summary>
        internal const byte FlagCaptured = 1;

        internal const byte FlagLate = 2;
        internal const byte FlagFailed = 4;
        internal const byte FlagNoTexture = 8;
        internal const byte FlagMipDeficient = 16;
        internal const byte FlagNormalRefused = 32;

        /// <summary>WP2 (fixes 2): the material had no room in the atlas (every page it could use was full) - its row has
        /// no tile, and <see cref="Tile.UnplacedPages"/> says how many pages there were when it was tried.</summary>
        internal const byte FlagUnplaced = 64;

        /// <summary>An entry's <see cref="Entry.TriedLevel"/> when no level was ever tried.</summary>
        internal const byte NeverTried = 255;

        /// <summary>A tile's <see cref="Tile.Mip"/> when the texture was not streamed or its level was unknown.</summary>
        internal const byte MipUnknown = 255;

        /// <summary>Metres a stored building's bounds centre and size (and a group's position) may differ from a present
        /// renderer's on each axis and still be the same object. A tolerance rather than a rounding grid: any grid has
        /// boundaries, and the float wobble between two loads (~1e-4 m) crosses a half-metre boundary for about 2
        /// buildings in 3,000 a raid, which would store them twice.</summary>
        internal const float IdentitySlackMetres = 0.25f;

        /// <summary>The FNV-1a 64-bit offset basis and prime.</summary>
        internal const ulong Fnv64Offset = 14695981039346656037UL;

        internal const ulong Fnv64Prime = 1099511628211UL;

        // --- the contents -----------------------------------------------------------------------------------------

        /// <summary>The raw SHA-256 of the mesh file this index describes (32 bytes).</summary>
        internal byte[] MeshSha = new byte[32];

        /// <summary>MapMeshBuilder.MeshRecipe of the build that wrote it.</summary>
        internal string Recipe = "";

        /// <summary>Application.version + "|" + Application.unityVersion of the game that wrote it.</summary>
        internal string Game = "";

        /// <summary>The extent, to the bit (the mesh's and the meta's).</summary>
        internal double MinX;

        internal double MinZ;
        internal double MaxX;
        internal double MaxZ;

        /// <summary>The render mask and whether the culling lists were known - both change which renderers are
        /// candidates at all.</summary>
        internal int RenderMask;

        internal bool CullingKnown;

        /// <summary>The bands the build was given (MapMeshBuilder.Band): the stored relief is filled into this capture's
        /// only when these are the same.</summary>
        internal List<BandRow> Bands = new List<BandRow>();

        /// <summary>meta.captures of the capture that wrote it.</summary>
        internal int Captures;

        /// <summary>The atlas pages the mesh names: each PNG's SHA-256 and tile count.</summary>
        internal List<Page> Pages = new List<Page>();

        /// <summary>Where the atlas's shelf packing stopped, so the next build places new tiles after it.</summary>
        internal AtlasPackState Pack = AtlasPackState.Empty;

        /// <summary>Every material tile the atlas holds, keyed by MapMeshBuilder.MaterialKey.</summary>
        internal List<Tile> Materials = new List<Tile>();

        /// <summary>One row per building of the mesh, in the SAME ORDER and count as its Buildings.</summary>
        internal List<Entry> Buildings = new List<Entry>();

        /// <summary>One band as the build was given it.</summary>
        internal sealed class BandRow
        {
            internal int Level;
            internal float MinY;
            internal float MaxY;
            internal float CameraY;
            internal float DepthBelow;
            internal bool Interior;

            /// <summary>Whether two band rows are the same to the bit.</summary>
            internal bool SameAs(BandRow other) =>
                other != null && Level == other.Level && Bits(MinY) == Bits(other.MinY) && Bits(MaxY) == Bits(other.MaxY) &&
                Bits(CameraY) == Bits(other.CameraY) && Bits(DepthBelow) == Bits(other.DepthBelow) && Interior == other.Interior;
        }

        /// <summary>One atlas page.</summary>
        internal sealed class Page
        {
            internal byte[] Sha = new byte[32];
            internal int Tiles;
        }

        /// <summary>One material's tiles: its textured block (Page -1 = none), its flat 4 x 4 tile (FlatPage -1 = none),
        /// how it was captured and its average colour - what MapBuilding needs to map a NEW building onto a stored tile
        /// without capturing it again.</summary>
        internal sealed class Tile
        {
            internal ulong Key;
            internal int TexW;
            internal int TexH;
            internal int Page = -1;
            internal int X;
            internal int Y;
            internal int W;
            internal int H;
            internal int FlatPage = -1;
            internal int FlatX;
            internal int FlatY;
            internal byte Flags;
            internal byte Mip = MipUnknown;
            internal byte AvgR = 255;
            internal byte AvgG = 255;
            internal byte AvgB = 255;

            /// <summary>The share of its captured texels at or over the material's cutoff (-1 = never captured).</summary>
            internal float OpaqueShare = -1f;

            internal ushort CapturedAt;

            /// <summary>WP2 (fixes 2): the atlas's page count when the material found no room (FlagUnplaced), so its
            /// buildings are not re-read for a texture until a page could hold it.</summary>
            internal byte UnplacedPages;

            internal bool Captured => (Flags & FlagCaptured) != 0;

            /// <summary>Whether a stored textured tile deserves a second capture into the same rect: late, failed, or
            /// taken from a resident mip smaller than the tile while a sharper one is resident now.</summary>
            /// <param name="currentMip">The texture's loaded mip level now, or <see cref="MipUnknown"/>.</param>
            internal bool Deficient(int currentMip)
            {
                if (Page < 0) return false;
                if ((Flags & (FlagLate | FlagFailed)) != 0) return true;
                return MipUpgrade(TexW, W, Mip, currentMip);
            }

            internal Tile Copy() => (Tile)MemberwiseClone();
        }

        /// <summary>One stored building: which renderer it is, its geometry signature, its group and level, how it
        /// was stored, and the budget inputs it was planned with.</summary>
        internal sealed class Entry
        {
            /// <summary>FNV-1a 64 of the renderer's hierarchy path (the string MapMeshBuilder.HierarchyPath builds).</summary>
            internal ulong PathHash;

            /// <summary>The geometry signature: the renderer's submesh range, its source triangles and its mesh's
            /// vertex count. A game patch that edits a mesh under the same path changes them.</summary>
            internal int SubFirst;

            internal int SubEnd;
            internal int SourceTriangles;
            internal int MeshVertexCount;

            /// <summary>The renderer's world bounds' centre and size at capture.</summary>
            internal float Cx;

            internal float Cy;
            internal float Cz;
            internal float Sx;
            internal float Sy;
            internal float Sz;

            /// <summary>Its LOD group's path hash (0 = no group) and the group transform's position.</summary>
            internal ulong GroupPathHash;

            internal float Gx;
            internal float Gy;
            internal float Gz;

            /// <summary>PART-04's in-memory Building.GroupKey (KeyFor of the group's path and reference point, or the
            /// building's own Key with no group).</summary>
            internal int GroupKey;

            /// <summary>The ladder level it was read from (0 with no group).</summary>
            internal byte LevelIndex;

            /// <summary>PART-04's grade (MapMeshBuilder.GradeFor): lower is better.</summary>
            internal byte Grade;

            /// <summary>0.. among identical renderers (same path, bounds and signature).</summary>
            internal byte Dup;

            /// <summary>The budget inputs - its world box's footprint, surface and height - so a later build re-plans
            /// the union with the live rule.</summary>
            internal float Footprint;

            internal float Surface;
            internal float Height;

            /// <summary>The mesh building's triangles (checked by <see cref="Mismatch"/>).</summary>
            internal int StoredTriangles;

            /// <summary>Its mean vertex height, metres - its band after the bands' heights change.</summary>
            internal float Centroid;

            /// <summary>meta.captures of the capture that stored it.</summary>
            internal ushort CapturedAt;

            /// <summary>WP2 (fixes 2): the target it was last read (or re-read) at - a read that could not do better than
            /// what is stored is not tried again until the target grows past this by more than the shortfall - and the
            /// finest LOD level its group tried (<see cref="NeverTried"/> for none): a finer level is not tried again
            /// until the group reads a finer one still.</summary>
            internal int TriedTarget;

            internal byte TriedLevel = NeverTried;

            /// <summary>WP2 (fixes 3): the target a CLEAN re-target (in place or re-read) of it failed at, 0 for none - it is not
            /// picked again until its required target moves past this by more than the shortfall (<see cref="RetargetDue"/>);
            /// and whether a clean texture re-read of it was refused (1) - not repeated until its target grows.</summary>
            internal int RetargetTried;

            internal byte TextureTried;

            /// <summary>Each atlas range's material key, in the order of the mesh building's Ranges.</summary>
            internal ulong[] RangeMaterials = new ulong[0];

            /// <summary>The LOD level its grade says it was read from (see <see cref="LevelOfGrade"/>).</summary>
            internal int Lod => LevelOfGrade(Grade);

            internal Entry Copy()
            {
                var copy = (Entry)MemberwiseClone();
                copy.RangeMaterials = (ulong[])RangeMaterials.Clone();
                return copy;
            }
        }

        // --- the binding ------------------------------------------------------------------------------------------

        /// <summary>
        /// Why this index cannot be trusted for this mesh and this capture, or null when it can. In order: the mesh's
        /// hash, the building count, each building's triangle count and range count, the recipe, the game version,
        /// the extent to the bit, the render mask, the culling knowledge, and the atlas page count.
        /// </summary>
        /// <param name="mesh">The mesh file read back.</param>
        /// <param name="meshSha">Its raw SHA-256.</param>
        /// <param name="recipe">This build's MapMeshBuilder.MeshRecipe.</param>
        /// <param name="game">This game's version string.</param>
        /// <param name="minX">This capture's extent.</param>
        /// <param name="minZ">This capture's extent.</param>
        /// <param name="maxX">This capture's extent.</param>
        /// <param name="maxZ">This capture's extent.</param>
        /// <param name="renderMask">This capture's render mask.</param>
        /// <param name="cullingKnown">Whether this capture knows the culling lists.</param>
        internal string Mismatch(MapMeshFile mesh, byte[] meshSha, string recipe, string game, double minX, double minZ,
            double maxX, double maxZ, int renderMask, bool cullingKnown)
        {
            if (mesh == null) return "there is no mesh to bind it to";
            if (!SameBytes(MeshSha, meshSha)) return "it describes another mesh file (its sha256 differs)";

            var count = mesh.Buildings?.Count ?? 0;
            if (Buildings.Count != count)
                return $"it lists {Buildings.Count} building(s) where the mesh has {count}";

            for (var i = 0; i < count; i++)
            {
                var b = mesh.Buildings[i];

                if (Buildings[i].StoredTriangles != b.TriangleCount)
                    return $"building {i} has {b.TriangleCount} triangle(s) in the mesh and {Buildings[i].StoredTriangles} in the index";

                var ranges = b.Ranges?.Count ?? 0;
                if (Buildings[i].RangeMaterials.Length != ranges)
                    return $"building {i} has {ranges} atlas range(s) in the mesh and {Buildings[i].RangeMaterials.Length} in the index";
            }

            if (!string.Equals(Recipe, recipe ?? "", StringComparison.Ordinal)) return "the mesh recipe changed";
            if (!string.Equals(Game, game ?? "", StringComparison.Ordinal)) return "the game version changed";

            if (Bits(MinX) != Bits(minX) || Bits(MinZ) != Bits(minZ) || Bits(MaxX) != Bits(maxX) || Bits(MaxZ) != Bits(maxZ))
                return "the extent changed";

            if (RenderMask != renderMask) return "the render mask changed";
            if (CullingKnown != cullingKnown) return CullingChanged;

            if (Pages.Count != mesh.AtlasPages)
                return $"it lists {Pages.Count} atlas page(s) where the mesh names {mesh.AtlasPages}";

            return null;
        }

        /// <summary>Mismatch's reason when only the culling knowledge differs - a TEMPORARY refusal (a capture whose
        /// culling scan failed this once), which the capture answers by carrying the stored mesh, not rebuilding it.</summary>
        internal const string CullingChanged = "whether the culling lists are known changed";

        // --- writing ----------------------------------------------------------------------------------------------

        /// <summary>The index as bytes, after <see cref="Validate"/>.</summary>
        /// <param name="index">The index.</param>
        internal static byte[] ToBytes(MapMeshIndex index)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));

            index.Validate();

            using (var buffer = new MemoryStream())
            {
                using (var deflate = new DeflateStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                using (var w = new BinaryWriter(deflate))
                {
                    foreach (var c in Magic) w.Write((byte)c);
                    w.Write(Version);
                    w.Write(index.MeshSha, 0, 32);
                    WriteString(w, index.Recipe);
                    WriteString(w, index.Game);
                    w.Write(index.MinX);
                    w.Write(index.MinZ);
                    w.Write(index.MaxX);
                    w.Write(index.MaxZ);
                    w.Write(index.RenderMask);
                    w.Write((byte)(index.CullingKnown ? 1 : 0));

                    w.Write(index.Bands.Count);
                    foreach (var band in index.Bands)
                    {
                        w.Write(band.Level);
                        w.Write(band.MinY);
                        w.Write(band.MaxY);
                        w.Write(band.CameraY);
                        w.Write(band.DepthBelow);
                        w.Write((byte)(band.Interior ? 1 : 0));
                    }

                    w.Write(index.Captures);

                    w.Write(index.Pages.Count);
                    foreach (var page in index.Pages)
                    {
                        w.Write(page.Sha, 0, 32);
                        w.Write(page.Tiles);
                    }

                    w.Write(index.Pack.Page);
                    w.Write(index.Pack.ShelfY);
                    w.Write(index.Pack.ShelfH);
                    w.Write(index.Pack.Cursor);

                    w.Write(index.Materials.Count);
                    foreach (var t in index.Materials)
                    {
                        w.Write(t.Key);
                        w.Write(t.TexW);
                        w.Write(t.TexH);
                        w.Write(t.Page);
                        w.Write(t.X);
                        w.Write(t.Y);
                        w.Write(t.W);
                        w.Write(t.H);
                        w.Write(t.FlatPage);
                        w.Write(t.FlatX);
                        w.Write(t.FlatY);
                        w.Write(t.Flags);
                        w.Write(t.Mip);
                        w.Write(t.AvgR);
                        w.Write(t.AvgG);
                        w.Write(t.AvgB);
                        w.Write(t.OpaqueShare);
                        w.Write(t.CapturedAt);
                        w.Write(t.UnplacedPages);
                    }

                    w.Write(index.Buildings.Count);
                    foreach (var e in index.Buildings)
                    {
                        w.Write(e.PathHash);
                        w.Write(e.SubFirst);
                        w.Write(e.SubEnd);
                        w.Write(e.SourceTriangles);
                        w.Write(e.MeshVertexCount);
                        w.Write(e.Cx);
                        w.Write(e.Cy);
                        w.Write(e.Cz);
                        w.Write(e.Sx);
                        w.Write(e.Sy);
                        w.Write(e.Sz);
                        w.Write(e.GroupPathHash);
                        w.Write(e.Gx);
                        w.Write(e.Gy);
                        w.Write(e.Gz);
                        w.Write(e.GroupKey);
                        w.Write(e.LevelIndex);
                        w.Write(e.Grade);
                        w.Write(e.Dup);
                        w.Write(e.Footprint);
                        w.Write(e.Surface);
                        w.Write(e.Height);
                        w.Write(e.StoredTriangles);
                        w.Write(e.Centroid);
                        w.Write(e.CapturedAt);
                        w.Write(e.TriedTarget);
                        w.Write(e.TriedLevel);
                        w.Write(e.RetargetTried);
                        w.Write(e.TextureTried);
                        w.Write((byte)e.RangeMaterials.Length);
                        foreach (var key in e.RangeMaterials) w.Write(key);
                    }
                }

                return buffer.ToArray();
            }
        }

        /// <summary>Throws <see cref="InvalidDataException"/> for anything the reader would refuse - so a writer bug is
        /// blamed at the write, not found at the next capture.</summary>
        internal void Validate()
        {
            if (MeshSha == null || MeshSha.Length != 32) throw new InvalidDataException("the mesh sha is not 32 bytes");
            if (Utf8(Recipe).Length > MaxRecipeBytes) throw new InvalidDataException("the recipe is too long");
            if (Utf8(Game).Length > MaxGameBytes) throw new InvalidDataException("the game string is too long");
            if (Bands == null || Bands.Count > MapMeshFile.MaxBands) throw new InvalidDataException("too many bands");
            if (Pages == null || Pages.Count > MapMeshFile.MaxAtlasPages) throw new InvalidDataException("too many atlas pages");

            foreach (var page in Pages)
                if (page?.Sha == null || page.Sha.Length != 32 || page.Tiles < 0)
                    throw new InvalidDataException("an atlas page row is malformed");

            if (Materials == null || Materials.Count > MaxMaterials) throw new InvalidDataException("too many materials");

            var keys = new HashSet<ulong>();
            foreach (var t in Materials)
            {
                if (t == null) throw new InvalidDataException("a material row is null");
                if (!keys.Add(t.Key)) throw new InvalidDataException($"material {t.Key:x16} is listed twice");
            }

            if (Buildings == null || Buildings.Count > MapMeshFile.MaxBuildings) throw new InvalidDataException("too many buildings");

            foreach (var e in Buildings)
            {
                if (e?.RangeMaterials == null) throw new InvalidDataException("a building row is malformed");
                if (e.RangeMaterials.Length > MapMeshFile.MaxRangesPerBuilding)
                    throw new InvalidDataException("a building row has too many ranges");
                if (e.StoredTriangles < 0) throw new InvalidDataException("a building row has a negative triangle count");
            }
        }

        private static void WriteString(BinaryWriter w, string value)
        {
            var bytes = Utf8(value);
            w.Write(bytes.Length);
            w.Write(bytes, 0, bytes.Length);
        }

        private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value ?? "");

        // --- reading ----------------------------------------------------------------------------------------------

        /// <summary>An index from its file's bytes. Every failure is an <see cref="InvalidDataException"/>.</summary>
        /// <param name="deflated">The file's bytes.</param>
        internal static MapMeshIndex Read(byte[] deflated)
        {
            if (deflated == null || deflated.Length == 0) throw new InvalidDataException("the index file is empty");

            byte[] body;

            try
            {
                using (var input = new MemoryStream(deflated, writable: false))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var chunk = new byte[1 << 16];
                    int n;

                    while ((n = deflate.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        output.Write(chunk, 0, n);
                        if (output.Length > MaxInflatedBytes)
                            throw new InvalidDataException($"the index inflates past {MaxInflatedBytes} bytes");
                    }

                    body = output.ToArray();
                }
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch (IOException ex)
            {
                throw new InvalidDataException("the index's deflate stream is broken or truncated", ex);
            }

            try
            {
                using (var r = new BinaryReader(new MemoryStream(body, writable: false)))
                {
                    var index = ReadBody(r);

                    if (r.BaseStream.Position != r.BaseStream.Length)
                        throw new InvalidDataException(
                            $"the index has {r.BaseStream.Length - r.BaseStream.Position} byte(s) after its last building");

                    return index;
                }
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("the index ends early", ex);
            }
        }

        private static MapMeshIndex ReadBody(BinaryReader r)
        {
            var magic = r.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != Magic[0] || magic[1] != Magic[1] || magic[2] != Magic[2] || magic[3] != Magic[3])
                throw new InvalidDataException("this is not a QuestTree mesh index");

            var version = r.ReadInt32();
            if (version != Version) throw new InvalidDataException($"the index is version {version}; this reads {Version}");

            var index = new MapMeshIndex { MeshSha = Exactly(r, 32, "the mesh sha") };
            index.Recipe = ReadString(r, MaxRecipeBytes, "the recipe");
            index.Game = ReadString(r, MaxGameBytes, "the game string");
            index.MinX = r.ReadDouble();
            index.MinZ = r.ReadDouble();
            index.MaxX = r.ReadDouble();
            index.MaxZ = r.ReadDouble();
            index.RenderMask = r.ReadInt32();
            index.CullingKnown = Flag(r, "culling-known");

            var bands = Count(r, MapMeshFile.MaxBands, "bands");
            for (var i = 0; i < bands; i++)
                index.Bands.Add(new BandRow
                {
                    Level = r.ReadInt32(), MinY = r.ReadSingle(), MaxY = r.ReadSingle(), CameraY = r.ReadSingle(),
                    DepthBelow = r.ReadSingle(), Interior = Flag(r, "a band's interior flag"),
                });

            index.Captures = r.ReadInt32();

            var pages = Count(r, MapMeshFile.MaxAtlasPages, "atlas pages");
            for (var i = 0; i < pages; i++)
            {
                var page = new Page { Sha = Exactly(r, 32, "a page's sha"), Tiles = r.ReadInt32() };
                if (page.Tiles < 0) throw new InvalidDataException("a page has a negative tile count");
                index.Pages.Add(page);
            }

            index.Pack = new AtlasPackState
            {
                Page = r.ReadInt32(), ShelfY = r.ReadInt32(), ShelfH = r.ReadInt32(), Cursor = r.ReadInt32(),
            };

            if (index.Pack.Page < -1 || index.Pack.Page >= MapMeshFile.MaxAtlasPages)
                throw new InvalidDataException($"the packing state names page {index.Pack.Page}");

            var materials = Count(r, MaxMaterials, "materials");
            var keys = new HashSet<ulong>();

            for (var i = 0; i < materials; i++)
            {
                var t = new Tile
                {
                    Key = r.ReadUInt64(), TexW = r.ReadInt32(), TexH = r.ReadInt32(), Page = r.ReadInt32(), X = r.ReadInt32(),
                    Y = r.ReadInt32(), W = r.ReadInt32(), H = r.ReadInt32(), FlatPage = r.ReadInt32(), FlatX = r.ReadInt32(),
                    FlatY = r.ReadInt32(), Flags = r.ReadByte(), Mip = r.ReadByte(), AvgR = r.ReadByte(), AvgG = r.ReadByte(),
                    AvgB = r.ReadByte(), OpaqueShare = r.ReadSingle(), CapturedAt = r.ReadUInt16(), UnplacedPages = r.ReadByte(),
                };

                if (!keys.Add(t.Key)) throw new InvalidDataException($"material {t.Key:x16} is listed twice");
                if (t.Page < -1 || t.Page >= MapMeshFile.MaxAtlasPages || t.FlatPage < -1 || t.FlatPage >= MapMeshFile.MaxAtlasPages)
                    throw new InvalidDataException($"material {t.Key:x16} names a page past the cap");

                index.Materials.Add(t);
            }

            var buildings = Count(r, MapMeshFile.MaxBuildings, "buildings");
            for (var i = 0; i < buildings; i++)
            {
                var e = new Entry
                {
                    PathHash = r.ReadUInt64(), SubFirst = r.ReadInt32(), SubEnd = r.ReadInt32(), SourceTriangles = r.ReadInt32(),
                    MeshVertexCount = r.ReadInt32(), Cx = r.ReadSingle(), Cy = r.ReadSingle(), Cz = r.ReadSingle(),
                    Sx = r.ReadSingle(), Sy = r.ReadSingle(), Sz = r.ReadSingle(), GroupPathHash = r.ReadUInt64(),
                    Gx = r.ReadSingle(), Gy = r.ReadSingle(), Gz = r.ReadSingle(), GroupKey = r.ReadInt32(),
                    LevelIndex = r.ReadByte(), Grade = r.ReadByte(), Dup = r.ReadByte(), Footprint = r.ReadSingle(),
                    Surface = r.ReadSingle(), Height = r.ReadSingle(), StoredTriangles = r.ReadInt32(), Centroid = r.ReadSingle(),
                    CapturedAt = r.ReadUInt16(), TriedTarget = r.ReadInt32(), TriedLevel = r.ReadByte(),
                    RetargetTried = r.ReadInt32(), TextureTried = r.ReadByte(),
                };

                if (e.StoredTriangles < 0) throw new InvalidDataException($"building {i} has a negative triangle count");

                var ranges = r.ReadByte();
                if (ranges > MapMeshFile.MaxRangesPerBuilding)
                    throw new InvalidDataException($"building {i} lists {ranges} ranges, over the cap");

                e.RangeMaterials = new ulong[ranges];
                for (var k = 0; k < ranges; k++) e.RangeMaterials[k] = r.ReadUInt64();

                index.Buildings.Add(e);
            }

            return index;
        }

        private static int Count(BinaryReader r, int cap, string what)
        {
            var n = r.ReadInt32();
            if (n < 0 || n > cap) throw new InvalidDataException($"the index claims {n} {what} (cap {cap})");
            return n;
        }

        private static bool Flag(BinaryReader r, string what)
        {
            var b = r.ReadByte();
            if (b > 1) throw new InvalidDataException($"{what} is {b}, not 0 or 1");
            return b == 1;
        }

        private static byte[] Exactly(BinaryReader r, int n, string what)
        {
            var bytes = r.ReadBytes(n);
            if (bytes.Length != n) throw new InvalidDataException($"the index ends inside {what}");
            return bytes;
        }

        private static string ReadString(BinaryReader r, int cap, string what)
        {
            var n = Count(r, cap, what + " byte(s)");
            return Encoding.UTF8.GetString(Exactly(r, n, what));
        }

        // --- the rules WP2's build keeps, Unity-free so the harness holds them -------------------------------------

        /// <summary>FNV-1a 64 over a string, each UTF-16 unit's low then high byte (the way MapMeshFile's KeyFor hashes).</summary>
        /// <param name="value">The string.</param>
        internal static ulong Fnv64(string value) => Fnv64Continue(Fnv64Offset, value);

        /// <summary>FNV-1a 64 continued over a string - so hash(prefix + s) == continue(hash(prefix), s).</summary>
        /// <param name="hash">The hash so far.</param>
        /// <param name="value">The string to add.</param>
        internal static ulong Fnv64Continue(ulong hash, string value)
        {
            if (value == null) return hash;

            unchecked
            {
                for (var i = 0; i < value.Length; i++)
                {
                    hash = (hash ^ (byte)value[i]) * Fnv64Prime;
                    hash = (hash ^ (byte)(value[i] >> 8)) * Fnv64Prime;
                }
            }

            return hash;
        }

        /// <summary>FNV-1a 64 continued over one character.</summary>
        internal static ulong Fnv64Continue(ulong hash, char c)
        {
            unchecked
            {
                hash = (hash ^ (byte)c) * Fnv64Prime;
                hash = (hash ^ (byte)(c >> 8)) * Fnv64Prime;
            }

            return hash;
        }

        /// <summary>FNV-1a 64 continued over a 64-bit value's eight bytes, low first.</summary>
        internal static ulong Fnv64Continue(ulong hash, ulong value)
        {
            unchecked
            {
                for (var shift = 0; shift < 64; shift += 8) hash = (hash ^ ((value >> shift) & 0xFF)) * Fnv64Prime;
            }

            return hash;
        }

        /// <summary>The LOD level a GRADE says a building was read from (MapMeshBuilder.GradeFor: sub for level 0,
        /// 10 + 4 x lod + sub for level lod &gt;= 1).</summary>
        /// <param name="grade">The grade.</param>
        internal static int LevelOfGrade(byte grade) => grade < 10 ? 0 : (grade - 10) / 4;

        /// <summary>How a building was stored, the low part of its grade (0 within its limit .. 3 clustered).</summary>
        /// <param name="grade">The grade.</param>
        internal static int SubOfGrade(byte grade) => grade < 10 ? grade : (grade - 10) % 4;

        /// <summary>Whether a texture streamed at a coarser mip than its tile, with a sharper one resident now: the
        /// stored tile was upsampled from (texW &gt;&gt; storedMip) pixels, fewer than its width, and
        /// (texW &gt;&gt; currentMip) is more.</summary>
        internal static bool MipUpgrade(int texW, int tileW, int storedMip, int currentMip)
        {
            if (storedMip == MipUnknown || currentMip == MipUnknown || texW <= 0) return false;
            if (storedMip < 0 || storedMip > 30 || currentMip < 0 || currentMip > 30) return false;

            var stored = texW >> storedMip;
            return stored < tileW && (texW >> currentMip) > stored;
        }

        /// <summary>Why a present, unchanged stored building is read again (WP2 fixes): nothing, degraded (stored
        /// clustered), a finer level read now, a shortfall against this build's target, no texture though its materials
        /// have one, a re-target (D3), or a changed signature.</summary>
        internal const int ReasonNone = 0;

        internal const int ReasonDegraded = 1;
        internal const int ReasonLevel = 2;
        internal const int ReasonShortfall = 3;
        internal const int ReasonTexture = 4;
        internal const int ReasonRetarget = 5;
        internal const int ReasonChanged = 6;

        /// <summary>
        /// WP2 (fixes): whether a present stored building whose signature is unchanged is read again - only when there is
        /// something to gain, so a scene read twice reads nothing twice: stored CLUSTERED (sub-grade 3); stored at a
        /// COARSER level than the one its group reads now; or its target at the scale the re-read will use exceeds what
        /// is stored by more than <paramref name="shortfall"/> AND its source holds more than is stored. Over budget (1) and
        /// as it is (2) are what the same source gives again, so they are not re-read. targetNow 0 leaves the degraded and
        /// shortfall tests out (they are judged once the plan knows the target). WP2 fixes 2: a building whose last read
        /// was at a target (triedTarget) not grown past by the shortfall, or whose group already tried the level read now
        /// (triedLevel), is not read again.
        /// </summary>
        internal static int ReReadReason(byte storedGrade, int currentLod, long stored, long source, long targetNow, double shortfall,
            int triedTarget, byte triedLevel)
        {
            // WP2 (fixes 2): a read that could not do better is not repeated until its target grows (or, for a level, until
            // the group reads a finer level than the one tried) - so a scene read twice reads nothing twice
            var grown = targetNow > 0 && (triedTarget <= 0 || targetNow > triedTarget * (1d + shortfall));

            if (SubOfGrade(storedGrade) >= 3 && grown) return ReasonDegraded;
            if (LevelOfGrade(storedGrade) > currentLod && (triedLevel == NeverTried || currentLod < triedLevel)) return ReasonLevel;
            if (grown && source > stored && Math.Min(source, targetNow) > stored * (1d + shortfall)) return ReasonShortfall;
            return ReasonNone;
        }

        /// <summary>
        /// WP2 (fixes 3): records a read's attempt on a row - only a CLEAN one (the level read completed and the decimation,
        /// when one was due, ran to its own stop: not skipped by the soft or hard cap, not timed out, not refused for want
        /// of headroom or the over-budget pool, not aborted). An unclean read leaves the row as it was, so the next stop
        /// tries it once more; a clean failure there records it. The tried target only grows and the tried level only gets
        /// finer. level <see cref="NeverTried"/> records the target alone. True when the row changed.
        /// </summary>
        internal static bool RecordAttempt(Entry e, bool clean, int target, int level)
        {
            if (e == null || !clean) return false;

            var changed = false;

            if (target > e.TriedTarget)
            {
                e.TriedTarget = target;
                changed = true;
            }

            var l = (byte)Math.Max(0, Math.Min(NeverTried, level));
            if (l < e.TriedLevel)
            {
                e.TriedLevel = l;
                changed = true;
            }

            return changed;
        }

        /// <summary>WP2 (fixes 3): whether an over-served stored building may be re-targeted to this required target - never
        /// tried, or its last clean failure was at a target this one differs from by more than the shortfall.</summary>
        internal static bool RetargetDue(long required, int retargetTried, double shortfall) =>
            retargetTried <= 0 || Math.Abs(required - (long)retargetTried) > retargetTried * shortfall;

        /// <summary>WP2 (fixes 3): whether a texture re-read may be tried - never refused before, or its target grew past
        /// the one it was last read at by more than the shortfall.</summary>
        internal static bool TextureDue(byte textureTried, long targetNow, int triedTarget, double shortfall) =>
            textureTried == 0 || (targetNow > 0 && (triedTarget <= 0 || targetNow > triedTarget * (1d + shortfall)));

        /// <summary>
        /// WP2 (fixes, I6): whether a re-read may replace the stored copy. A changed signature always; a degraded or
        /// finer-level re-read only at a strictly better grade and never clustered; a shortfall at no worse a grade and
        /// with more triangles; a texture re-read at no worse a grade and textured; a re-target (a deliberate reduction)
        /// never at a worse sub-grade or level - a present re-target that fell to as it is or clustered does not replace a
        /// stored copy within its limit. A refused re-read leaves the stored building exactly as it was.
        /// </summary>
        internal static bool ReplaceAccepted(int reason, byte newGrade, byte storedGrade, long kept, long stored, bool textured)
        {
            switch (reason)
            {
                case ReasonChanged:
                    return true;
                case ReasonDegraded:
                case ReasonLevel:
                    return newGrade < storedGrade && SubOfGrade(newGrade) < 3;
                case ReasonShortfall:
                    return newGrade <= storedGrade && kept > stored;
                case ReasonTexture:
                    return newGrade <= storedGrade && textured;
                case ReasonRetarget:
                    return SubOfGrade(newGrade) <= SubOfGrade(storedGrade) && LevelOfGrade(newGrade) <= LevelOfGrade(storedGrade);
                default:
                    return newGrade <= storedGrade;
            }
        }

        /// <summary>Whether two floats are within the identity slack.</summary>
        internal static bool Near(float a, float b) => Math.Abs(a - b) <= IdentitySlackMetres;

        /// <summary>Whether an entry is the object at this path, centre and size (the MATCH KEY, without the signature).</summary>
        internal static bool SameObject(Entry e, ulong pathHash, float cx, float cy, float cz, float sx, float sy, float sz) =>
            e.PathHash == pathHash && Near(e.Cx, cx) && Near(e.Cy, cy) && Near(e.Cz, cz) && Near(e.Sx, sx) && Near(e.Sy, sy) &&
            Near(e.Sz, sz);

        /// <summary>Whether an entry's geometry signature equals this one.</summary>
        internal static bool SameSignature(Entry e, int subFirst, int subEnd, long sourceTriangles, int meshVertexCount) =>
            e.SubFirst == subFirst && e.SubEnd == subEnd && e.SourceTriangles == sourceTriangles && e.MeshVertexCount == meshVertexCount;

        /// <summary>Whether two entries belong to one LOD group: the same group path hash (not 0) and positions within
        /// the slack - the path alone collides (every "building/karkas" group has the same one).</summary>
        internal static bool SameGroup(Entry a, Entry b) =>
            a.GroupPathHash != 0 && a.GroupPathHash == b.GroupPathHash && Near(a.Gx, b.Gx) && Near(a.Gy, b.Gy) && Near(a.Gz, b.Gz);

        /// <summary>
        /// PART-04's merge rule as WP2 keeps it: within one map, for each LOD group, the buildings at the group's LOWEST
        /// level (by grade) win WHOLESALE and every building of that group at another level is removed - so two levels
        /// of one group are never stored together. Buildings of one level with different sub-grades (one decimated
        /// within its limit, one stored as it is) are one level and are all kept; a building with no group is always
        /// kept. Answers, per row, whether it is kept.
        /// </summary>
        /// <param name="rows">The buildings' rows.</param>
        internal static bool[] KeepLowestLevel(IList<Entry> rows)
        {
            var keep = new bool[rows.Count];
            var groupOf = GroupsOf(rows);
            var best = new Dictionary<int, int>();

            for (var i = 0; i < rows.Count; i++)
            {
                keep[i] = true;
                if (groupOf[i] < 0) continue;

                var level = rows[i].Lod;
                if (!best.TryGetValue(groupOf[i], out var b) || level < b) best[groupOf[i]] = level;
            }

            for (var i = 0; i < rows.Count; i++)
                if (groupOf[i] >= 0 && rows[i].Lod != best[groupOf[i]])
                    keep[i] = false;

            return keep;
        }

        /// <summary>Each row's group number (rows of one group share it; -1 for no group), by <see cref="SameGroup"/>.</summary>
        /// <param name="rows">The rows.</param>
        internal static int[] GroupsOf(IList<Entry> rows)
        {
            var groupOf = new int[rows.Count];
            var byHash = new Dictionary<ulong, List<int>>();     // hash -> the first row of each group with it
            var groups = 0;

            for (var i = 0; i < rows.Count; i++)
            {
                groupOf[i] = -1;
                var row = rows[i];
                if (row.GroupPathHash == 0) continue;

                if (!byHash.TryGetValue(row.GroupPathHash, out var heads)) byHash[row.GroupPathHash] = heads = new List<int>();

                foreach (var head in heads)
                    if (SameGroup(rows[head], row))
                    {
                        groupOf[i] = groupOf[head];
                        break;
                    }

                if (groupOf[i] >= 0) continue;

                groupOf[i] = groups++;
                heads.Add(i);
            }

            return groupOf;
        }

        /// <summary>Whether two identities collide (the checker's uniqueness rule): the same path hash and dup with
        /// centres and sizes within the slack.</summary>
        internal static bool Collide(Entry a, Entry b) =>
            a.Dup == b.Dup && SameObject(a, b.PathHash, b.Cx, b.Cy, b.Cz, b.Sx, b.Sy, b.Sz);

        /// <summary>
        /// Whether a stored relief band can be filled into this build's: the same level, the same grid (width, height)
        /// and the same derived cell to the bit (PART-03: the cell is derived from the extent, so a base built under
        /// another cell - another rule or another extent - is a different grid, and its cells are not this build's).
        /// </summary>
        /// <param name="stored">The stored band, or null.</param>
        /// <param name="level">This band's level.</param>
        /// <param name="width">This grid's columns.</param>
        /// <param name="height">This grid's rows.</param>
        /// <param name="cellMetres">This build's cell.</param>
        internal static bool ReliefFillable(MapMeshFile.ReliefBand stored, int level, int width, int height, float cellMetres)
        {
            if (stored == null || stored.Heights == null || stored.Distance == null) return false;
            if (stored.Level != level || stored.Width != width || stored.Height != height) return false;
            if (Bits(stored.CellMetres) != Bits(cellMetres)) return false;

            var cells = (long)width * height;
            return stored.Heights.Length == cells && stored.Distance.Length == cells;
        }

        /// <summary>
        /// The relief merge, cell by cell (D2): a cell this cast MEASURED keeps this cast's height; a cell it left empty
        /// takes the stored height and distance; the distance byte of a measured cell is the smaller of the two - the
        /// closest observation - so a no-op stop leaves the band byte-identical. Metres in, so it runs before the band is
        /// quantised. Returns the cells filled from the stored band; <paramref name="filled"/>, when given, marks them.
        /// </summary>
        /// <param name="metres">This cast's heights, NaN where it missed.</param>
        /// <param name="distance">This cast's distance bytes.</param>
        /// <param name="stored">The stored band (<see cref="ReliefFillable"/> already said yes).</param>
        /// <param name="heightOf">The stored file's HeightOf.</param>
        /// <param name="filled">Marks the filled cells, or null.</param>
        internal static int FillRelief(float[] metres, byte[] distance, MapMeshFile.ReliefBand stored, Func<ushort, float> heightOf,
            bool[] filled)
        {
            var count = 0;

            for (var n = 0; n < metres.Length; n++)
            {
                var old = stored.Heights[n];
                var oldDistance = stored.Distance[n];

                if (float.IsNaN(metres[n]))
                {
                    if (old == MapMeshFile.NoHit) continue;

                    metres[n] = heightOf(old);
                    distance[n] = oldDistance;
                    if (filled != null) filled[n] = true;
                    count++;
                }
                else if (oldDistance != MapMeshFile.DistanceEmpty && oldDistance < distance[n])
                {
                    distance[n] = oldDistance;
                }
            }

            return count;
        }

        // --- small helpers ----------------------------------------------------------------------------------------

        internal static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

        internal static int Bits(float v) => BitConverter.SingleToInt32Bits(v);

        internal static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>A hex SHA-256 (the meta's form) as its 32 raw bytes, or null when it is not one.</summary>
        /// <param name="hex">The hex string.</param>
        internal static byte[] ShaBytes(string hex)
        {
            if (hex == null || hex.Length != 64) return null;

            var bytes = new byte[32];

            for (var i = 0; i < 32; i++)
                if (!byte.TryParse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    return null;

            return bytes;
        }

        /// <summary>32 raw bytes as the meta's lower-case hex.</summary>
        internal static string ShaHex(byte[] bytes)
        {
            if (bytes == null) return null;

            var text = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return text.ToString();
        }
    }

    /// <summary>
    /// Where a shelf packing stopped (WP2): the page, the current shelf's bottom and height, and the next free x on it -
    /// so AtlasPacker.PackFrom continues a stored atlas's packing with the same rule instead of starting a page over.
    /// <see cref="Empty"/> (page -1) is "nothing packed yet", and packing from it is AtlasPacker.Pack exactly.
    /// </summary>
    internal struct AtlasPackState
    {
        internal int Page;
        internal int ShelfY;
        internal int ShelfH;
        internal int Cursor;

        /// <summary>Nothing packed yet.</summary>
        internal static AtlasPackState Empty => new AtlasPackState { Page = -1 };
    }
}
