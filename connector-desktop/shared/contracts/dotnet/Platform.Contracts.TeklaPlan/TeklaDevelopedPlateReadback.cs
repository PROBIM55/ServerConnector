#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Platform.Contracts.TeklaPlan
{
    public sealed class TeklaSolidFaceReadback
    {
        public TeklaVector3 Normal { get; set; } = new(0, 0, 1);
        public IReadOnlyList<IReadOnlyList<TeklaVector3>> Loops { get; set; } = Array.Empty<IReadOnlyList<TeklaVector3>>();
    }

    /// <summary>Source-contour expectations and native measurements, never a native geometry builder.</summary>
    public static class TeklaDevelopedPlateReadback
    {
        private const double Epsilon = 1e-6;

        public static IReadOnlyList<TeklaPlanValidationDiagnostic> ValidateContour(TeklaPlanCommand command, TeklaDevelopedPlateSpec developed)
        {
            if (developed.DevelopedContour is null) return Array.Empty<TeklaPlanValidationDiagnostic>();
            var contour = developed.DevelopedContour;
            var points = Ordered(contour);
            var last = developed.StationFrame.Stations.Last().StationMm;
            var low = developed.TransverseOffsetMm - developed.StockWidthMm / 2;
            var high = developed.TransverseOffsetMm + developed.StockWidthMm / 2;
            var sign = Math.Sign(Area(points));
            if (contour.Edges.Any(edge => edge.Kind != "line") || sign == 0 ||
                points.Any(point => point.X < -Epsilon || point.X > last + Epsilon || point.Y < low - Epsilon || point.Y > high + Epsilon) ||
                Math.Abs(points.Min(point => point.X)) > Epsilon || Math.Abs(points.Max(point => point.X) - last) > Epsilon ||
                Math.Abs(points.Min(point => point.Y) - low) > Epsilon || Math.Abs(points.Max(point => point.Y) - high) > Epsilon)
                return Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_CONTOUR_INVALID", "Final development must be a line-only contour within the declared stock extents.");
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if (points.Any(p => sign * ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) < -Epsilon))
                    return Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_CONTOUR_INVALID", "Processed native development requires a convex contour.");
            }
            return Array.Empty<TeklaPlanValidationDiagnostic>();
        }

        public static IReadOnlyList<TeklaPlanValidationDiagnostic> ValidateReferences(TeklaPlanDocument plan, TeklaPlanCommand command)
        {
            if (command.Kind != "create-poly-beam") return Array.Empty<TeklaPlanValidationDiagnostic>();
            var parsed = TeklaPlanPayloads.ParseCreatePolyBeam(command);
            if (!parsed.Success || parsed.Value is null) return parsed.Diagnostics;
            var developed = parsed.Value.DevelopedPlate;
            if (developed is null) return Array.Empty<TeklaPlanValidationDiagnostic>();
            var cuts = plan.Commands.Where(c => c.Kind == "apply-boolean-cut" && c.Source.Kind == command.Source.Kind && c.Source.Id == command.Source.Id).ToArray();
            if (developed.DevelopedContour is null)
                return cuts.Length == 0 ? Array.Empty<TeklaPlanValidationDiagnostic>() :
                    Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_EVIDENCE_MISSING", "Processed PolyBeam requires its canonical developedContour for final readback.");
            if (cuts.Length == 0 || ExpectedRemovedVolume(developed) <= Epsilon)
                return Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_CUTS_INVALID", "Processed development must have mapped cuts and a positive source removal volume.");

            var mappings = plan.Mappings.Where(m => m.Source.Kind == command.Source.Kind && m.Source.Id == command.Source.Id).ToArray();
            var ids = new[] { command.CommandId }.Concat(cuts.Select(c => c.CommandId)).ToArray();
            if (mappings.Length != 1 || mappings[0].Strategy != "native-composite" ||
                mappings[0].CommandIds.Length != ids.Length || !new HashSet<string>(mappings[0].CommandIds, StringComparer.Ordinal).SetEquals(ids))
                return Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_CUTS_INVALID", "Processed development requires one complete stock/cuts mapping.");
            foreach (var cut in cuts)
            {
                var payload = TeklaPlanPayloads.ParseApplyBooleanCut(cut);
                if (!payload.Success || payload.Value is null) return payload.Diagnostics;
                var cutter = payload.Value.Cutter;
                if (payload.Value.Target.ElementId != command.Source.Id || cut.Source.StableKey != command.Source.StableKey ||
                    !cut.DependsOn.Contains(command.CommandId, StringComparer.Ordinal) ||
                    cutter.Kind != "through-contour" || cutter.ExtrusionSide != "symmetric" ||
                    Math.Abs(cutter.ThicknessMm - developed.ThicknessMm) > Epsilon ||
                    !CutInFlatRun(cutter, developed))
                    return Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_CUTS_INVALID", "Development cuts must depend on their stock and use its exact flat-run plane, thickness and symmetric side.");
            }
            // Other material-changing operations need their own final-state evidence.
            var modifyingKinds = new[] { "apply-boolean-cut", "apply-fitting", "create-hole", "create-bolt-group", "apply-edge-treatment", "apply-contour-corner", "apply-bend" };
            foreach (var operation in plan.Commands.Where(c => modifyingKinds.Contains(c.Kind) && !cuts.Contains(c)))
            {
                if (operation.Payload.TryGetValue("target", out var target) && target.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    target.TryGetProperty("elementId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String && id.GetString() == command.Source.Id)
                    return Error(command, "TEKLA_PLAN_DEVELOPED_READBACK_OPERATION_UNSUPPORTED", "Additional material-changing operations are not represented by the final development contour.");
            }
            return Array.Empty<TeklaPlanValidationDiagnostic>();
        }

        private static bool CutInFlatRun(TeklaBooleanCutGeometryPayload cutter, TeklaDevelopedPlateSpec developed)
        {
            var stations = developed.StationFrame.Stations;
            for (var start = 0; start < stations.Count - 1; start++)
            {
                var frame = stations[start].Frame;
                if (!Near(cutter.Plane.Origin, frame.Origin) || !Near(cutter.Plane.AxisX, frame.AxisZ) ||
                    !Near(cutter.Plane.AxisY, frame.AxisX) || !Near(cutter.Plane.AxisZ, frame.AxisY)) continue;
                var end = start;
                while (end + 1 < stations.Count && SameAxes(frame, stations[end + 1].Frame) &&
                    Near(Subtract(stations[end + 1].Frame.Origin, frame.Origin), Scale(frame.AxisZ, stations[end + 1].StationMm - stations[start].StationMm))) end++;
                var length = stations[end].StationMm - stations[start].StationMm;
                if (length > Epsilon && cutter.Contour.Edges.All(edge => edge.Kind == "line") &&
                    cutter.Contour.Vertices.All(vertex => vertex.Point.X >= -Epsilon && vertex.Point.X <= length + Epsilon)) return true;
            }
            return false;
        }

        public static TeklaVector2 WidthAt(TeklaDevelopedPlateSpec developed, double stationMm)
        {
            if (developed.DevelopedContour is null)
                return new TeklaVector2(developed.TransverseOffsetMm - developed.StockWidthMm / 2, developed.TransverseOffsetMm + developed.StockWidthMm / 2);
            var points = Ordered(developed.DevelopedContour);
            var widths = new List<double>();
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if (stationMm < Math.Min(a.X, b.X) - Epsilon || stationMm > Math.Max(a.X, b.X) + Epsilon) continue;
                if (Math.Abs(b.X - a.X) <= Epsilon) { widths.Add(a.Y); widths.Add(b.Y); }
                else widths.Add(a.Y + (b.Y - a.Y) * Math.Max(0, Math.Min(1, (stationMm - a.X) / (b.X - a.X))));
            }
            if (widths.Count < 2 || widths.Max() - widths.Min() <= Epsilon)
                throw new InvalidOperationException("Final development has no finite-width section at the requested station.");
            return new TeklaVector2(widths.Min(), widths.Max());
        }

        public static double ExpectedRemovedVolume(TeklaDevelopedPlateSpec developed)
            => developed.DevelopedContour is null ? 0 :
                (developed.StockWidthMm * developed.StationFrame.Stations.Last().StationMm - Math.Abs(Area(Ordered(developed.DevelopedContour)))) * developed.ThicknessMm;

        public static IReadOnlyList<TeklaPlanValidationDiagnostic> ValidateEndSection(TeklaPlanCommand command,
            TeklaDevelopedPlateSpec developed, TeklaDevelopedStationSpec station, IReadOnlyList<TeklaVector3> vertices)
        {
            var frame = station.Frame;
            var section = vertices.Select(point => Subtract(point, frame.Origin))
                .Where(point => Math.Abs(Dot(point, frame.AxisZ)) <= 0.25).ToArray();
            if (section.Length < 4)
                return Error(command, "TEKLA_PLAN_DEVELOPED_PLATE_SECTION_MISSING", $"Final section at '{station.Id}' is missing from its source plane.");
            var width = WidthAt(developed, station.StationMm);
            var x = section.Select(point => Dot(point, frame.AxisX)).ToArray();
            var y = section.Select(point => Dot(point, frame.AxisY)).ToArray();
            var errors = new[] { x.Min() - width.X, x.Max() - width.Y, y.Min() + developed.ThicknessMm / 2, y.Max() - developed.ThicknessMm / 2 };
            return errors.Any(value => !Finite(value) || Math.Abs(value) > 1e-3)
                ? Error(command, "TEKLA_PLAN_READBACK_MISMATCH", $"Final section at '{station.Id}' differs from its developed width, signed plane or thickness.")
                : Array.Empty<TeklaPlanValidationDiagnostic>();
        }

        public static IReadOnlyList<TeklaPlanValidationDiagnostic> ValidateVolumes(
            TeklaPlanCommand command, TeklaDevelopedPlateSpec developed, double rawVolume, double finalVolume)
        {
            var expected = ExpectedRemovedVolume(developed);
            var tolerance = Math.Max(1, rawVolume * 1e-6);
            return !Finite(rawVolume) || !Finite(finalVolume) || rawVolume <= 0 || finalVolume <= 0 ||
                Math.Abs(rawVolume - finalVolume - expected) > tolerance
                ? Error(command, "TEKLA_PLAN_READBACK_MISMATCH", $"Native developed removal volume is {rawVolume - finalVolume:0.###} mm3, expected {expected:0.###} mm3.")
                : Array.Empty<TeklaPlanValidationDiagnostic>();
        }

        /// <summary>Divergence theorem on measured planar faces, retaining holes and independent of loop winding.</summary>
        public static double MeasureVolume(IReadOnlyList<TeklaSolidFaceReadback> faces)
        {
            if (faces.Count < 4 || faces.Any(face => face.Loops.Count == 0 || face.Loops.Any(loop => loop.Count < 3)))
                throw new InvalidOperationException("Native solid has incomplete face loops.");
            var origin = faces[0].Loops[0][0];
            var sum = 0d;
            var areaSum = 0d;
            var closure = new TeklaVector3(0, 0, 0);
            foreach (var face in faces)
            {
                var length = Math.Sqrt(Dot(face.Normal, face.Normal));
                if (!Finite(length) || length < Epsilon) throw new InvalidOperationException("Native solid has an invalid face normal.");
                var normal = Scale(face.Normal, 1 / length);
                var reference = face.Loops[0][0];
                var loopAreas = new List<double>();
                foreach (var loop in face.Loops)
                {
                    var area = 0d;
                    for (var i = 0; i < loop.Count; i++)
                    {
                        var p = Subtract(loop[i], reference);
                        var q = Subtract(loop[(i + 1) % loop.Count], reference);
                        if (!Finite(Dot(p, p)) || Math.Abs(Dot(p, normal)) > 1e-3)
                            throw new InvalidOperationException("Native solid readback requires finite planar face loops.");
                        area += Dot(Cross(p, q), normal) / 2;
                    }
                    loopAreas.Add(Math.Abs(area));
                }
                var faceArea = 2 * loopAreas.Max() - loopAreas.Sum();
                if (faceArea <= 0) throw new InvalidOperationException("Native solid face has invalid outer/inner loop areas.");
                sum += faceArea * Dot(Subtract(reference, origin), normal) / 3;
                areaSum += faceArea;
                closure = Add(closure, Scale(normal, faceArea));
            }
            if (!Finite(sum) || sum <= 0 || Math.Sqrt(Dot(closure, closure)) > Math.Max(1e-3, areaSum * 1e-8))
                throw new InvalidOperationException("Native solid face normals do not form a closed outward-oriented solid.");
            return sum;
        }

        private static IReadOnlyList<TeklaVector2> Ordered(TeklaContour2 contour)
        {
            var vertices = contour.Vertices.ToDictionary(vertex => vertex.Id, vertex => vertex.Point, StringComparer.Ordinal);
            return contour.Edges.Select(edge => vertices[edge.StartVertexId]).ToArray();
        }
        private static double Area(IReadOnlyList<TeklaVector2> points)
            => points.Select((p, i) => p.X * points[(i + 1) % points.Count].Y - p.Y * points[(i + 1) % points.Count].X).Sum() / 2;
        private static IReadOnlyList<TeklaPlanValidationDiagnostic> Error(TeklaPlanCommand command, string code, string message)
            => new[] { new TeklaPlanValidationDiagnostic(code, message, command.CommandId) };
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static bool SameAxes(TeklaPlane3 a, TeklaPlane3 b) => Near(a.AxisX, b.AxisX) && Near(a.AxisY, b.AxisY) && Near(a.AxisZ, b.AxisZ);
        private static bool Near(TeklaVector3 a, TeklaVector3 b) => Dot(Subtract(a, b), Subtract(a, b)) <= Epsilon * Epsilon;
        private static TeklaVector3 Add(TeklaVector3 a, TeklaVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        private static TeklaVector3 Subtract(TeklaVector3 a, TeklaVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        private static TeklaVector3 Scale(TeklaVector3 a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
        private static double Dot(TeklaVector3 a, TeklaVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        private static TeklaVector3 Cross(TeklaVector3 a, TeklaVector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
