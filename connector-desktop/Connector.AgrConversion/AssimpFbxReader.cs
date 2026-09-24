using System.Globalization;
using System.Text;
using Silk.NET.Assimp;
using AssimpApi = Silk.NET.Assimp.Assimp;

namespace Connector.AgrConversion;

/// <summary>Сетка из FBX до разбиения по тайлам: координаты уже в осях АГР, трансформации запечены.</summary>
public sealed class FbxMesh
{
    public required string Node { get; init; }
    public required string Material { get; init; }
    public bool Collision { get; init; }
    public required float[] Positions { get; init; }
    public float[]? Normals { get; init; }
    public float[]? Uvs { get; init; }
    public required int[] Indices { get; init; }
    public int NonTriangleFaces { get; init; }
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
}

public sealed class FbxScene
{
    public string AssimpVersion { get; init; } = "";
    public List<FbxMesh> Meshes { get; } = new();
    public SortedDictionary<string, string> Meta { get; } = new(StringComparer.Ordinal);
    public double[] RootTransform { get; set; } = Array.Empty<double>();
    public List<string> MaterialNames { get; } = new();
}

/// <summary>
/// Чтение FBX АГР через Assimp (Silk.NET.Assimp 2.23.0, нативный assimp 6.0.2), параметры спайка S2:
/// Triangulate + JoinIdenticalVertices + ValidateDataStructure, PRESERVE_PIVOTS = 0, без света/камер/анимаций.
/// Файл читается из памяти: путь с кириллицей не зависит от кодировки строк assimp.
/// Сцена assimp освобождается сразу после копирования в управляемые массивы.
/// <para>
/// Потокобезопасность: <see cref="Read(string)"/> и <see cref="Read(byte[], string)"/> можно вызывать из нескольких
/// потоков; сам импорт assimp при этом идёт по одному (см. комментарий к ImportGate), разбор сцены — параллельно.
/// </para>
/// </summary>
public static unsafe class AssimpFbxReader
{
    const uint SceneFlagsIncomplete = 0x1;

    public const uint Flags = (uint)(PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices
                                     | PostProcessSteps.ValidateDataStructure);

    public static string ImportDescription =>
        $"memory import, flags=0x{Flags:X} ({(PostProcessSteps)Flags}); PRESERVE_PIVOTS=0; lights/cameras/animations=0; " +
        $"axes assimp→АГР {AgrRules.AssimpToAgrAxes}; scale 1";

    static readonly object ApiGate = new();
    static AssimpApi? _api;

    static AssimpApi Api
    {
        get
        {
            lock (ApiGate)
            {
                return _api ??= AssimpApi.GetApi();
            }
        }
    }

    public static string Version()
    {
        var api = Api;
        return $"{api.GetVersionMajor()}.{api.GetVersionMinor()}.{api.GetVersionPatch()} (rev {api.GetVersionRevision():x})";
    }

    // Импорт assimp сериализован замком. Причина — глобальное состояние C API assimp (code/Common/Assimp.cpp):
    // неудачный aiImportFileFromMemoryWithProperties пишет текст ошибки в одну на процесс std::string
    // gLastErrorString без синхронизации, а aiGetErrorString отдаёт указатель на неё же. Два неудачных импорта
    // одновременно — гонка записи в одну строку (порча кучи, падение процесса), а строка, прочитанная после своего
    // импорта, может оказаться чужой. Под замком строка ошибки берётся сразу после неудачного импорта. Разбор готовой
    // сцены и её освобождение (aiReleaseImport) глобального состояния не трогают и идут без замка.
    static readonly object ImportGate = new();

    /// <summary>
    /// Чтение FBX с диска. Потокобезопасно: импорт assimp сериализован (<see cref="ImportGate"/>), остальное — без
    /// общего состояния. Ошибки — <see cref="AgrReadException"/> с именем файла; сырой текст — в диагностике.
    /// </summary>
    public static FbxScene Read(string fbxPath)
    {
        string name = System.IO.Path.GetFileName(fbxPath);
        byte[] data;
        try
        {
            data = System.IO.File.ReadAllBytes(fbxPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw AgrReadException.Unreadable("FBX части", name, ex);
        }
        return Read(data, name);
    }

    public static FbxScene Read(byte[] data, string displayName)
    {
        // Пустой файл — до assimp: на 0 байт assimp строку ошибки не обновляет и отдавал текст прошлого импорта.
        if (data.Length == 0)
        {
            throw new AgrReadException(displayName, $"FBX части: файл {displayName} пустой (0 байт) — недокачан или повреждён.");
        }
        var api = Api;
        var res = new FbxScene { AssimpVersion = Version() };
        Scene* sc;
        string error = "";
        lock (ImportGate)
        {
            string before = api.GetErrorStringS() ?? "";
            PropertyStore* store = api.CreatePropertyStore();
            try
            {
                api.SetImportPropertyInteger(store, "IMPORT_FBX_PRESERVE_PIVOTS", 0);
                api.SetImportPropertyInteger(store, "IMPORT_FBX_READ_LIGHTS", 0);
                api.SetImportPropertyInteger(store, "IMPORT_FBX_READ_CAMERAS", 0);
                api.SetImportPropertyInteger(store, "IMPORT_FBX_READ_ANIMATIONS", 0);
                fixed (byte* p = data)
                {
                    sc = api.ImportFileFromMemoryWithProperties(p, (uint)data.Length, Flags, "fbx", store);
                }
                if (sc == null)
                {
                    // только сразу после неудачного импорта и под тем же замком
                    string after = api.GetErrorStringS() ?? "";
                    error = after.Length == 0 ? "строка ошибки assimp пуста"
                        : after == before ? "строка ошибки assimp не изменилась, возможно она от прошлого импорта: " + after
                        : after;
                }
            }
            finally
            {
                api.ReleasePropertyStore(store);
            }
        }
        if (sc == null)
        {
            throw new AgrReadException(displayName, $"FBX части: файл {displayName} не читается — повреждён или это не FBX.",
                                       diagnostic: $"assimp {res.AssimpVersion}, {data.Length} байт: {error}");
        }
        try
        {
            if ((sc->MFlags & SceneFlagsIncomplete) != 0 || sc->MRootNode == null)
            {
                throw new AgrReadException(displayName, $"FBX части: файл {displayName} прочитан не полностью — сцена неполная.",
                                           diagnostic: $"assimp {res.AssimpVersion}: AI_SCENE_FLAGS_INCOMPLETE или нет корневого узла");
            }
            ReadMeta(sc->MMetaData, res.Meta, "");
            for (uint i = 0; i < sc->MNumMaterials; i++)
            {
                res.MaterialNames.Add(MaterialName(api, sc->MMaterials[i]));
            }
            res.RootTransform = ReadMatrix(&sc->MRootNode->MTransformation);
            Walk(api, sc, sc->MRootNode, Identity(), res, false);
        }
        finally
        {
            api.ReleaseImport(sc);
        }
        return res;
    }

    static string Str(AssimpString* s)
    {
        uint len = s->Length;
        if (len > 1024) len = 1024;
        return Encoding.UTF8.GetString((byte*)s + 4, (int)len);
    }

    static string MaterialName(AssimpApi api, Silk.NET.Assimp.Material* mat)
    {
        AssimpString s;
        api.GetMaterialString(mat, "?mat.name", 0, 0, &s);
        return Str(&s);
    }

    // Матрица assimp в порядке строк: a1..a4 — первая строка, перенос в a4/b4/c4.
    static double[] ReadMatrix(void* p)
    {
        float* f = (float*)p;
        var m = new double[16];
        for (int i = 0; i < 16; i++) m[i] = f[i];
        return m;
    }

    static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

    static double[] Mul(double[] x, double[] y)
    {
        var r = new double[16];
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += x[i * 4 + k] * y[k * 4 + j];
                r[i * 4 + j] = s;
            }
        }
        return r;
    }

    /// <summary>Обратная транспонированная 3x3 (для нормалей), по строкам.</summary>
    static double[] NormalMatrix(double[] m)
    {
        double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
        double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
        double D = -(b * i - c * h), E = a * i - c * g, F = -(a * h - b * g);
        double G = b * f - c * e, H = -(a * f - c * d), I = a * e - b * d;
        double det = a * A + b * B + c * C;
        if (Math.Abs(det) < 1e-30) det = 1e-30;
        return new[] { A / det, B / det, C / det, D / det, E / det, F / det, G / det, H / det, I / det };
    }

    static void ReadMeta(Metadata* md, SortedDictionary<string, string> dst, string prefix)
    {
        if (md == null) return;
        for (uint i = 0; i < md->MNumProperties; i++)
        {
            string key = prefix + Str(&md->MKeys[i]);
            MetadataEntry e = md->MValues[i];
            string val;
            if (e.MData == null)
            {
                val = "null";
            }
            else
            {
                switch (e.MType)
                {
                    case MetadataType.Bool: val = (*(byte*)e.MData != 0).ToString(); break;
                    case MetadataType.Int32: val = (*(int*)e.MData).ToString(CultureInfo.InvariantCulture); break;
                    case MetadataType.Uint32: val = (*(uint*)e.MData).ToString(CultureInfo.InvariantCulture); break;
                    case MetadataType.Uint64: val = (*(ulong*)e.MData).ToString(CultureInfo.InvariantCulture); break;
                    case MetadataType.Int64: val = (*(long*)e.MData).ToString(CultureInfo.InvariantCulture); break;
                    case MetadataType.Float: val = (*(float*)e.MData).ToString("R", CultureInfo.InvariantCulture); break;
                    case MetadataType.Double: val = (*(double*)e.MData).ToString("R", CultureInfo.InvariantCulture); break;
                    case MetadataType.Aistring: val = Str((AssimpString*)e.MData); break;
                    case MetadataType.Aivector3D:
                        float* v = (float*)e.MData;
                        val = string.Create(CultureInfo.InvariantCulture, $"{v[0]},{v[1]},{v[2]}");
                        break;
                    case MetadataType.Aimetadata:
                        ReadMeta((Metadata*)e.MData, dst, key + ".");
                        continue;
                    default: val = "type" + (int)e.MType; break;
                }
            }
            dst[key] = val;
        }
    }

    static void Walk(AssimpApi api, Scene* sc, Node* nd, double[] parent, FbxScene res, bool collisionParent)
    {
        var world = Mul(parent, ReadMatrix(&nd->MTransformation));
        string name = Str(&nd->MName);
        int piv = name.IndexOf("_$AssimpFbx$", StringComparison.Ordinal);
        string baseName = piv >= 0 ? name[..piv] : name;
        bool coll = collisionParent || AgrRules.IsCollisionName(baseName);
        for (uint i = 0; i < nd->MNumMeshes; i++)
        {
            Mesh* m = sc->MMeshes[nd->MMeshes[i]];
            res.Meshes.Add(CopyMesh(api, sc, m, world, baseName, coll));
        }
        for (uint c = 0; c < nd->MNumChildren; c++)
        {
            Walk(api, sc, nd->MChildren[c], world, res, coll);
        }
    }

    static FbxMesh CopyMesh(AssimpApi api, Scene* sc, Mesh* m, double[] w, string node, bool coll)
    {
        string material = m->MMaterialIndex < sc->MNumMaterials ? MaterialName(api, sc->MMaterials[m->MMaterialIndex]) : "";
        int nv = (int)m->MNumVertices;

        var pos = new float[nv * 3];
        float* pv = (float*)m->MVertices;
        for (int v = 0; v < nv; v++)
        {
            double x = pv[v * 3], y = pv[v * 3 + 1], z = pv[v * 3 + 2];
            double wx = w[0] * x + w[1] * y + w[2] * z + w[3];
            double wy = w[4] * x + w[5] * y + w[6] * z + w[7];
            double wz = w[8] * x + w[9] * y + w[10] * z + w[11];
            AgrRules.ToAgrFrame(wx, wy, wz, out double ox, out double oy, out double oz);
            pos[v * 3] = (float)ox;
            pos[v * 3 + 1] = (float)oy;
            pos[v * 3 + 2] = (float)oz;
        }

        float[]? nrm = null;
        if (m->MNormals != null)
        {
            nrm = new float[nv * 3];
            float* pn = (float*)m->MNormals;
            var nm = NormalMatrix(w);
            for (int v = 0; v < nv; v++)
            {
                double x = pn[v * 3], y = pn[v * 3 + 1], z = pn[v * 3 + 2];
                double X = nm[0] * x + nm[1] * y + nm[2] * z;
                double Y = nm[3] * x + nm[4] * y + nm[5] * z;
                double Z = nm[6] * x + nm[7] * y + nm[8] * z;
                AgrRules.ToAgrFrame(X, Y, Z, out double ox, out double oy, out double oz);
                double l = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                if (l > 0)
                {
                    ox /= l; oy /= l; oz /= l;
                }
                nrm[v * 3] = (float)ox;
                nrm[v * 3 + 1] = (float)oy;
                nrm[v * 3 + 2] = (float)oz;
            }
        }

        float[]? uv = null;
        void** tcs = (void**)&m->MTextureCoords;
        if (tcs[0] != null)
        {
            uv = new float[nv * 2];
            float* pt = (float*)tcs[0];
            for (int v = 0; v < nv; v++)
            {
                uv[v * 2] = pt[v * 3];
                uv[v * 2 + 1] = pt[v * 3 + 1];
            }
        }

        int nt = 0, other = 0;
        for (uint f = 0; f < m->MNumFaces; f++)
        {
            if (m->MFaces[f].MNumIndices == 3) nt++;
            else other++;
        }
        var idx = new int[nt * 3];
        int t = 0;
        for (uint f = 0; f < m->MNumFaces; f++)
        {
            Face fc = m->MFaces[f];
            if (fc.MNumIndices != 3) continue;
            idx[t++] = (int)fc.MIndices[0];
            idx[t++] = (int)fc.MIndices[1];
            idx[t++] = (int)fc.MIndices[2];
        }

        return new FbxMesh
        {
            Node = node,
            Material = material,
            Collision = coll,
            Positions = pos,
            Normals = nrm,
            Uvs = uv,
            Indices = idx,
            NonTriangleFaces = other,
        };
    }
}
