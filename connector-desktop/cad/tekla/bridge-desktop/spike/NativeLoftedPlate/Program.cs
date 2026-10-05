using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;

namespace NativeLoftedPlate
{
    internal static class Program
    {
        private const double CoordinateOffset = 714000.0;
        private const double Tolerance = 1.0;

        private static int Main()
        {
            var reportDirectory = Path.Combine(Path.GetTempPath(), "NativeLoftedPlate");
            Directory.CreateDirectory(reportDirectory);
            using var report = new StreamWriter(Path.Combine(reportDirectory, "report.txt"), append: false) { AutoFlush = true };
            Model? model = null;
            TransformationPlane? previousPlane = null;
            LoftedPlate? plate = null;

            void Log(string message)
            {
                var line = $"[{DateTime.UtcNow:O}] {message}";
                Console.WriteLine(line);
                report.WriteLine(line);
            }

            try
            {
                model = new Model();
                Require(model.GetConnectionStatus(), "Tekla 2025 model is not connected");
                var info = model.GetInfo();
                Log($"MODEL name={info.ModelName} path={info.ModelPath}");
                previousPlane = model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
                Require(model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()), "activate global work plane");

                VerifyParallelLineDirectricesAccepted(model);
                Log("STEP parallelLineDirectrices=true");

                plate = CreatePlate(upperStation: 2200, includeIgnoredThirdCurve: false);
                Require(plate.Insert(), "LoftedPlate.Insert with two arc curves");
                Require(model.CommitChanges(), "commit LoftedPlate insert");
                var guid = model.GetGUIDByIdentifier(plate.Identifier);
                Require(!string.IsNullOrWhiteSpace(guid), "persistent LoftedPlate GUID");

                var inserted = ReadPlate(model, guid);
                var insertedSolid = inserted.GetSolid();
                Log($"STEP insert=true guid={guid} baseCurves={inserted.BaseCurves.Count} solidMin={F(insertedSolid.MinimumPoint)} solidMax={F(insertedSolid.MaximumPoint)}");
                VerifySolidHeight(insertedSolid, upperStation: 2200);

                var thirdCurveRejected = false;
                inserted.BaseCurves = CreateCurves(upperStation: 2200, includeIgnoredThirdCurve: true);
                try
                {
                    Require(inserted.Modify(), "LoftedPlate.Modify with third curve");
                    Require(model.CommitChanges(), "commit third curve");
                    var thirdCurveSolid = ReadPlate(model, guid).GetSolid();
                    VerifySameBounds(insertedSolid, thirdCurveSolid);
                }
                catch (InvalidCurveCombinationException)
                {
                    thirdCurveRejected = true;
                }
                var withThirdCurve = ReadPlate(model, guid);
                Log($"STEP thirdCurveRejected={thirdCurveRejected.ToString().ToLowerInvariant()} thirdCurveIgnored={(!thirdCurveRejected).ToString().ToLowerInvariant()}");

                withThirdCurve.BaseCurves = CreateCurves(upperStation: 3100, includeIgnoredThirdCurve: false);
                Require(withThirdCurve.Modify(), "LoftedPlate.Modify station");
                Require(model.CommitChanges(), "commit LoftedPlate modify");
                var modified = ReadPlate(model, guid);
                Require(string.Equals(model.GetGUIDByIdentifier(modified.Identifier), guid, StringComparison.OrdinalIgnoreCase), "LoftedPlate GUID changed after Modify");
                VerifySolidHeight(modified.GetSolid(), upperStation: 3100);
                Log($"STEP modify=true guidPreserved=true baseCurves={modified.BaseCurves.Count} solidMax={F(modified.GetSolid().MaximumPoint)}");

                Require(modified.Delete(), "LoftedPlate.Delete");
                Require(model.CommitChanges(), "commit LoftedPlate delete");
                Require(model.SelectModelObject(model.GetIdentifierByGUID(guid)) is null, "LoftedPlate still exists after Delete");
                plate = null;
                Log("STEP rollbackDelete=true");

                Log($"RESULT capability=teklaLoftedPlateTwoDirectrixV1 supported=true arcDirectrices=true parallelLineDirectrices=true thirdCurveRejected={thirdCurveRejected.ToString().ToLowerInvariant()} modifyGuidPreserved=true rollbackDelete=true genericClosedSectionLoft=false");
                Log("BLOCKED Constructive LoftedPlate uses ordered closed cross-sections; Tekla LoftedPlate uses exactly two directrix curves. Generic create-lofted-plate must remain disabled until a separate exact lowering strategy exists.");
                return 0;
            }
            catch (Exception exception)
            {
                Log($"FATAL {exception.GetType().Name}: {exception.Message}");
                Log(exception.ToString());
                return 99;
            }
            finally
            {
                if (model is not null)
                {
                    try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane()); }
                    catch { }
                    if (plate is not null)
                    {
                        try
                        {
                            var guid = model.GetGUIDByIdentifier(plate.Identifier);
                            var selected = string.IsNullOrWhiteSpace(guid) ? plate : model.SelectModelObject(model.GetIdentifierByGUID(guid));
                            if (selected is not null) selected.Delete();
                            model.CommitChanges();
                        }
                        catch (Exception exception) { Log($"CLEANUP_WARN LoftedPlate: {exception.Message}"); }
                    }
                    if (previousPlane is not null)
                    {
                        try { model.GetWorkPlaneHandler().SetCurrentTransformationPlane(previousPlane); }
                        catch (Exception exception) { Log($"CLEANUP_WARN work plane: {exception.Message}"); }
                    }
                }
            }
        }

        private static LoftedPlate CreatePlate(double upperStation, bool includeIgnoredThirdCurve)
        {
            var plate = new LoftedPlate
            {
                Name = "STRUCTURA LOFTED PLATE SPIKE",
                Class = "99",
                BaseCurves = CreateCurves(upperStation, includeIgnoredThirdCurve),
                FaceType = LoftedPlate.LoftedPlateFaceTypeEnum.Perpendicular,
            };
            plate.Profile.ProfileString = "PL12";
            plate.Material.MaterialString = "S355";
            return plate;
        }

        private static List<ICurve> CreateCurves(double upperStation, bool includeIgnoredThirdCurve)
        {
            var curves = new List<ICurve>
            {
                new Arc(P(0, 0, 0), P(1200, 0, 0), new Vector(0, 0, 1), Math.PI),
                new Arc(P(0, 0, upperStation), P(1200, 0, upperStation), new Vector(0, 0, 1), Math.PI),
            };
            if (includeIgnoredThirdCurve)
            {
                curves.Add(new Arc(P(0, 9000, 1100), P(1200, 9000, 1100), new Vector(0, 0, 1), Math.PI));
            }
            return curves;
        }

        private static LoftedPlate ReadPlate(Model model, string guid)
        {
            var plate = model.SelectModelObject(model.GetIdentifierByGUID(guid)) as LoftedPlate;
            if (plate is null || !plate.Select()) throw new InvalidOperationException("Cannot select LoftedPlate for readback.");
            return plate;
        }

        private static void VerifyParallelLineDirectricesAccepted(Model model)
        {
            var plate = new LoftedPlate
            {
                Name = "STRUCTURA LOFTED PLATE INVALID LINE PROBE",
                BaseCurves = new List<ICurve>
                {
                    new LineSegment(P(0, 0, 0), P(1200, 0, 0)),
                    new LineSegment(P(0, 0, 2200), P(1200, 0, 2200)),
                },
            };
            plate.Profile.ProfileString = "PL12";
            plate.Material.MaterialString = "S355";
            Require(plate.Insert(), "parallel line directrices were rejected");
            Require(model.CommitChanges(), "commit parallel line directrix probe");
            VerifySolidHeight(plate.GetSolid(), 2200);
            Require(plate.Delete(), "delete parallel line directrix probe");
            Require(model.CommitChanges(), "commit parallel line directrix cleanup");
        }

        private static void VerifySolidHeight(Solid solid, double upperStation)
        {
            Require(Math.Abs(solid.MinimumPoint.Z) <= 20, $"solid min Z expected near 0 actual={solid.MinimumPoint.Z}");
            Require(Math.Abs(solid.MaximumPoint.Z - upperStation) <= 20, $"solid max Z expected={upperStation} actual={solid.MaximumPoint.Z}");
        }

        private static void VerifySameBounds(Solid expected, Solid actual)
        {
            VerifyPoint(expected.MinimumPoint, actual.MinimumPoint, "solid minimum changed after third curve");
            VerifyPoint(expected.MaximumPoint, actual.MaximumPoint, "solid maximum changed after third curve");
        }

        private static void VerifyPoint(Point expected, Point actual, string message)
        {
            Require(Math.Abs(expected.X - actual.X) <= Tolerance, $"{message}: X expected={expected.X} actual={actual.X}");
            Require(Math.Abs(expected.Y - actual.Y) <= Tolerance, $"{message}: Y expected={expected.Y} actual={actual.Y}");
            Require(Math.Abs(expected.Z - actual.Z) <= Tolerance, $"{message}: Z expected={expected.Z} actual={actual.Z}");
        }

        private static Point P(double x, double y, double z) => new(CoordinateOffset + x, y, z);

        private static string F(Point point) => string.Format(
            CultureInfo.InvariantCulture,
            "({0:0.###},{1:0.###},{2:0.###})",
            point.X,
            point.Y,
            point.Z);

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
