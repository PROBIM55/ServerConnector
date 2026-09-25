namespace Connector.AgrConversion;

/// <summary>Распределение расстояний: максимум, p99, p50 и число точек (метры).</summary>
public sealed record AgrDistanceStats(double Max, double P99, double P50, int Points)
{
    public static AgrDistanceStats Of(double[] d)
    {
        if (d.Length == 0)
        {
            return new AgrDistanceStats(0, 0, 0, 0);
        }
        var s = (double[])d.Clone();
        Array.Sort(s);
        return new AgrDistanceStats(s[^1], Quantile(s, 0.99), Quantile(s, 0.5), s.Length);
    }

    static double Quantile(double[] sorted, double q)
    {
        double pos = (sorted.Length - 1) * q;
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }
}

/// <summary>
/// Вырожденные треугольники уровня (требование (р)): совпавшие вершины и коллинеарные в значениях буфера. Все четыре
/// числа — отдельные счётчики (невырожденные не выводятся вычитанием), поэтому сверка их суммы с числом треугольников
/// по JSON GLB — настоящая.
/// </summary>
public sealed record AgrDegenerateStats(long Triangles, long Coincident, long Collinear, long NonDegenerate);

/// <summary>
/// Расстояния «точка → ближайшая точка поверхности» по дереву AABB треугольников (точно, в double), выборка
/// случайных точек по площади, счёт вырожденных треугольников и тексель. Метод ошибок — требования (к) и (о) C1:
/// прямое направление — уникальные вершины исходника → поверхность уровня; обратное — вершины уровня и случайные
/// точки по площади его треугольников → поверхность исходника; geometricError = max(прямое, обратное, тексель).
/// </summary>
public sealed class AgrTriangleTree
{
    const int LeafSize = 6;
    readonly double[] _tri; // 9 чисел на треугольник
    readonly int[] _order;
    readonly List<Node> _nodes = new();

    struct Node
    {
        public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
        public int Left, Right, Start, Count;
    }

    public int Triangles => _order.Length;

    public AgrTriangleTree(IEnumerable<AgrGeomPrimitive> prims)
    {
        var tri = new List<double>();
        foreach (var p in prims)
        {
            var P = p.Positions;
            for (int t = 0; t < p.Indices.Length; t += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = p.Indices[t + c];
                    tri.Add(P[v * 3]);
                    tri.Add(P[v * 3 + 1]);
                    tri.Add(P[v * 3 + 2]);
                }
            }
        }
        _tri = tri.ToArray();
        int n = _tri.Length / 9;
        _order = Enumerable.Range(0, n).ToArray();
        var centroid = new double[n * 3];
        for (int i = 0; i < n; i++)
        {
            for (int a = 0; a < 3; a++)
            {
                centroid[i * 3 + a] = (_tri[i * 9 + a] + _tri[i * 9 + 3 + a] + _tri[i * 9 + 6 + a]) / 3.0;
            }
        }
        if (n > 0)
        {
            Build(0, n, centroid);
        }
    }

    int Build(int start, int count, double[] centroid)
    {
        var node = new Node { Start = start, Count = count, Left = -1, Right = -1 };
        node.MinX = node.MinY = node.MinZ = double.MaxValue;
        node.MaxX = node.MaxY = node.MaxZ = double.MinValue;
        for (int i = start; i < start + count; i++)
        {
            int t = _order[i] * 9;
            for (int c = 0; c < 9; c += 3)
            {
                node.MinX = Math.Min(node.MinX, _tri[t + c]); node.MaxX = Math.Max(node.MaxX, _tri[t + c]);
                node.MinY = Math.Min(node.MinY, _tri[t + c + 1]); node.MaxY = Math.Max(node.MaxY, _tri[t + c + 1]);
                node.MinZ = Math.Min(node.MinZ, _tri[t + c + 2]); node.MaxZ = Math.Max(node.MaxZ, _tri[t + c + 2]);
            }
        }
        int index = _nodes.Count;
        _nodes.Add(node);
        if (count > LeafSize)
        {
            double ex = node.MaxX - node.MinX, ey = node.MaxY - node.MinY, ez = node.MaxZ - node.MinZ;
            int axis = ex >= ey && ex >= ez ? 0 : ey >= ez ? 1 : 2;
            Array.Sort(_order, start, count, Comparer<int>.Create((a, b) => centroid[a * 3 + axis].CompareTo(centroid[b * 3 + axis])));
            int half = count / 2;
            int left = Build(start, half, centroid);
            int right = Build(start + half, count - half, centroid);
            node.Left = left;
            node.Right = right;
            _nodes[index] = node;
        }
        return index;
    }

    /// <summary>Расстояние от точки до ближайшей точки треугольников дерева (метры); пустое дерево — +∞.</summary>
    public double Distance(double px, double py, double pz)
    {
        if (_order.Length == 0)
        {
            return double.PositiveInfinity;
        }
        double best = double.MaxValue;
        Span<int> stack = stackalloc int[128];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            var node = _nodes[stack[--sp]];
            if (BoxDistSq(node, px, py, pz) >= best)
            {
                continue;
            }
            if (node.Left < 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    double d = PointTriangleSq(px, py, pz, _tri, _order[i] * 9);
                    if (d < best) best = d;
                }
                continue;
            }
            var l = _nodes[node.Left];
            var r = _nodes[node.Right];
            double dl = BoxDistSq(l, px, py, pz), dr = BoxDistSq(r, px, py, pz);
            if (dl < dr)
            {
                if (dr < best) stack[sp++] = node.Right;
                if (dl < best) stack[sp++] = node.Left;
            }
            else
            {
                if (dl < best) stack[sp++] = node.Left;
                if (dr < best) stack[sp++] = node.Right;
            }
        }
        return Math.Sqrt(best);
    }

    /// <summary>Расстояния для точек (x, y, z подряд), параллельно.</summary>
    public double[] Distances(double[] points)
    {
        int n = points.Length / 3;
        var d = new double[n];
        Parallel.For(0, n, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) },
            k => d[k] = Distance(points[k * 3], points[k * 3 + 1], points[k * 3 + 2]));
        return d;
    }

    static double BoxDistSq(in Node n, double x, double y, double z)
    {
        double dx = x < n.MinX ? n.MinX - x : x > n.MaxX ? x - n.MaxX : 0;
        double dy = y < n.MinY ? n.MinY - y : y > n.MaxY ? y - n.MaxY : 0;
        double dz = z < n.MinZ ? n.MinZ - z : z > n.MaxZ ? z - n.MaxZ : 0;
        return dx * dx + dy * dy + dz * dz;
    }

    /// <summary>Квадрат расстояния до треугольника (Ericson, «Real-Time Collision Detection», 5.1.5).</summary>
    static double PointTriangleSq(double px, double py, double pz, double[] t, int o)
    {
        double ax = t[o], ay = t[o + 1], az = t[o + 2];
        double bx = t[o + 3], by = t[o + 4], bz = t[o + 5];
        double cx = t[o + 6], cy = t[o + 7], cz = t[o + 8];
        double abx = bx - ax, aby = by - ay, abz = bz - az;
        double acx = cx - ax, acy = cy - ay, acz = cz - az;
        double apx = px - ax, apy = py - ay, apz = pz - az;
        double d1 = abx * apx + aby * apy + abz * apz, d2 = acx * apx + acy * apy + acz * apz;
        double qx, qy, qz;
        if (d1 <= 0 && d2 <= 0) { qx = ax; qy = ay; qz = az; }
        else
        {
            double bpx = px - bx, bpy = py - by, bpz = pz - bz;
            double d3 = abx * bpx + aby * bpy + abz * bpz, d4 = acx * bpx + acy * bpy + acz * bpz;
            if (d3 >= 0 && d4 <= d3) { qx = bx; qy = by; qz = bz; }
            else
            {
                double vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0)
                {
                    double v = d1 / (d1 - d3);
                    qx = ax + v * abx; qy = ay + v * aby; qz = az + v * abz;
                }
                else
                {
                    double cpx = px - cx, cpy = py - cy, cpz = pz - cz;
                    double d5 = abx * cpx + aby * cpy + abz * cpz, d6 = acx * cpx + acy * cpy + acz * cpz;
                    if (d6 >= 0 && d5 <= d6) { qx = cx; qy = cy; qz = cz; }
                    else
                    {
                        double vb = d5 * d2 - d1 * d6;
                        if (vb <= 0 && d2 >= 0 && d6 <= 0)
                        {
                            double w = d2 / (d2 - d6);
                            qx = ax + w * acx; qy = ay + w * acy; qz = az + w * acz;
                        }
                        else
                        {
                            double va = d3 * d6 - d5 * d4;
                            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                            {
                                double w = (d4 - d3) / (d4 - d3 + (d5 - d6));
                                qx = bx + w * (cx - bx); qy = by + w * (cy - by); qz = bz + w * (cz - bz);
                            }
                            else
                            {
                                double sum = va + vb + vc;
                                if (sum == 0)
                                {
                                    // вырожденный треугольник: ближайшая из трёх вершин (рёбра проверены выше)
                                    qx = ax; qy = ay; qz = az;
                                }
                                else
                                {
                                    double denom = 1.0 / sum;
                                    double v = vb * denom, w = vc * denom;
                                    qx = ax + abx * v + acx * w; qy = ay + aby * v + acy * w; qz = az + abz * v + acz * w;
                                }
                            }
                        }
                    }
                }
            }
        }
        double dx = px - qx, dy = py - qy, dz = pz - qz;
        return dx * dx + dy * dy + dz * dz;
    }
}

public static class AgrMeshSampling
{
    /// <summary>Уникальные позиции вершин, на которые ссылаются треугольники (точное совпадение координат).</summary>
    public static double[] UniqueUsedVertices(IEnumerable<AgrGeomPrimitive> prims)
    {
        var seen = new HashSet<(double, double, double)>();
        var list = new List<double>();
        foreach (var p in prims)
        {
            foreach (int v in p.Indices)
            {
                var key = (p.Positions[v * 3], p.Positions[v * 3 + 1], p.Positions[v * 3 + 2]);
                if (seen.Add(key))
                {
                    list.Add(key.Item1);
                    list.Add(key.Item2);
                    list.Add(key.Item3);
                }
            }
        }
        return list.ToArray();
    }

    /// <summary>
    /// <paramref name="count"/> случайных точек, равномерно по площади треугольников (выбор треугольника пропорционально
    /// площади, точка — равномерно внутри). Генератор с постоянным зерном: замер воспроизводим.
    /// </summary>
    public static double[] AreaSamples(IReadOnlyList<AgrGeomPrimitive> prims, int count, int seed)
    {
        var tris = new List<(AgrGeomPrimitive P, int T)>();
        var cum = new List<double>();
        double total = 0;
        foreach (var p in prims)
        {
            for (int t = 0; t < p.Indices.Length; t += 3)
            {
                double a = Area(p.Positions, p.Indices[t], p.Indices[t + 1], p.Indices[t + 2]);
                if (a <= 0) continue;
                total += a;
                tris.Add((p, t));
                cum.Add(total);
            }
        }
        if (tris.Count == 0 || count <= 0)
        {
            return Array.Empty<double>();
        }
        var rnd = new Random(seed);
        var cumArr = cum.ToArray();
        var pts = new double[count * 3];
        for (int k = 0; k < count; k++)
        {
            int i = Array.BinarySearch(cumArr, rnd.NextDouble() * total);
            if (i < 0) i = ~i;
            if (i >= tris.Count) i = tris.Count - 1;
            var (p, t) = tris[i];
            double r1 = Math.Sqrt(rnd.NextDouble()), r2 = rnd.NextDouble();
            double wa = 1 - r1, wb = r1 * (1 - r2), wc = r1 * r2;
            int a = p.Indices[t] * 3, b = p.Indices[t + 1] * 3, c = p.Indices[t + 2] * 3;
            for (int ax = 0; ax < 3; ax++)
            {
                pts[k * 3 + ax] = wa * p.Positions[a + ax] + wb * p.Positions[b + ax] + wc * p.Positions[c + ax];
            }
        }
        return pts;
    }

    public static double Area(double[] P, int a, int b, int c)
    {
        double ux = P[b * 3] - P[a * 3], uy = P[b * 3 + 1] - P[a * 3 + 1], uz = P[b * 3 + 2] - P[a * 3 + 2];
        double vx = P[c * 3] - P[a * 3], vy = P[c * 3 + 1] - P[a * 3 + 1], vz = P[c * 3 + 2] - P[a * 3 + 2];
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt(cx * cx + cy * cy + cz * cz);
    }

    /// <summary>
    /// Вырожденные треугольники в значениях буфера (целые при квантовании): совпали две вершины (индексы или
    /// координаты) — «совпавшие»; иначе векторное произведение рёбер ровно ноль — «коллинеарные».
    /// </summary>
    public static AgrDegenerateStats Degenerate(IEnumerable<AgrGeomPrimitive> prims)
    {
        long tris = 0, same = 0, line = 0, good = 0;
        foreach (var p in prims)
        {
            var R = p.RawPositions;
            for (int t = 0; t < p.Indices.Length; t += 3)
            {
                tris++;
                int a = p.Indices[t] * 3, b = p.Indices[t + 1] * 3, c = p.Indices[t + 2] * 3;
                bool eqAb = R[a] == R[b] && R[a + 1] == R[b + 1] && R[a + 2] == R[b + 2];
                bool eqBc = R[b] == R[c] && R[b + 1] == R[c + 1] && R[b + 2] == R[c + 2];
                bool eqCa = R[c] == R[a] && R[c + 1] == R[a + 1] && R[c + 2] == R[a + 2];
                if (eqAb || eqBc || eqCa)
                {
                    same++;
                    continue;
                }
                double ux = R[b] - R[a], uy = R[b + 1] - R[a + 1], uz = R[b + 2] - R[a + 2];
                double vx = R[c] - R[a], vy = R[c + 1] - R[a + 1], vz = R[c + 2] - R[a + 2];
                if (uy * vz - uz * vy == 0 && uz * vx - ux * vz == 0 && ux * vy - uy * vx == 0)
                {
                    line++;
                }
                else
                {
                    good++;
                }
            }
        }
        return new AgrDegenerateStats(tris, same, line, good);
    }

    /// <summary>
    /// Метров на единицу UV: средневзвешенный по площади sqrt(A / A_uv) треугольников с текстурой (как S1a-2b);
    /// тексель уровня — <see cref="Texel"/> (то же, делённое на сторону картинки материала).
    /// </summary>
    public static double MetersPerUv(IEnumerable<AgrGeomPrimitive> prims, Func<int?, bool> textured) =>
        Texel(prims, m => textured(m) ? 1 : 0);

    /// <summary>
    /// Тексель уровня в метрах по размеру картинок: Σ(A·sqrt(A/A_uv) / px) / ΣA по примитивам, у материала которых
    /// <paramref name="px"/> &gt; 0 (сторона картинки baseColor в уровне); остальные примитивы не входят.
    /// </summary>
    public static double Texel(IEnumerable<AgrGeomPrimitive> prims, Func<int?, int> px)
    {
        double aw = 0, sumW = 0;
        foreach (var p in prims)
        {
            int side = px(p.Material);
            if (p.Uvs == null || side <= 0) continue;
            double A = 0, Auv = 0;
            var U = p.Uvs;
            for (int t = 0; t < p.Indices.Length; t += 3)
            {
                int a = p.Indices[t], b = p.Indices[t + 1], c = p.Indices[t + 2];
                A += Area(p.Positions, a, b, c);
                Auv += 0.5 * Math.Abs((U[b * 2] - U[a * 2]) * (double)(U[c * 2 + 1] - U[a * 2 + 1])
                                      - (U[c * 2] - U[a * 2]) * (double)(U[b * 2 + 1] - U[a * 2 + 1]));
            }
            if (Auv > 0 && A > 0)
            {
                aw += A * Math.Sqrt(A / Auv) / side;
                sumW += A;
            }
        }
        return sumW > 0 ? aw / sumW : 0;
    }
}
