// POST /uda/files/apply
// Writes Platform-generated Tekla UDA definition files into the currently
// opened Tekla model folder. The server sends file contents; Bridge.Desktop
// resolves the model folder locally via Tekla API and never accepts a target
// filesystem path from the network.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Platform.Bridge.Desktop.Tekla.Logging;
using Platform.Bridge.Desktop.Tekla.Tekla;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.Http
{
    public sealed class UdaFilesHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly Regex MacroFieldLineRegex = new(
            "^\\s*(?<id>[A-Za-z][A-Za-z0-9_]*)\\s*,\\s*(?<type>INT|STRING|FLOAT|DATE)\\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly TeklaWorker _worker;
        private readonly JsonLineLogger _log;

        public UdaFilesHandler(TeklaWorker worker, JsonLineLogger log)
        {
            _worker = worker;
            _log = log;
        }

        public async Task<HttpResult> ApplyAsync(RequestContext ctx, CancellationToken ct)
        {
            string raw;
            try { raw = await ctx.ReadStringAsync(); }
            catch (Exception ex) { return HttpResult.BadRequest("BODY_READ_FAILED", ex.Message); }

            if (string.IsNullOrWhiteSpace(raw))
            {
                return HttpResult.BadRequest("EMPTY_BODY", "Request body required.");
            }

            UdaFilesApplyRequest? request;
            try { request = JsonSerializer.Deserialize<UdaFilesApplyRequest>(raw, JsonOptions); }
            catch (JsonException ex) { return HttpResult.BadRequest("INVALID_JSON", ex.Message); }

            var validationError = ValidateRequest(request);
            if (validationError is not null)
            {
                return validationError;
            }

            try
            {
                var result = await _worker.RunAsync(model => ApplyToCurrentModel(model, request!), ct);
                if (result.Conflicts.Count > 0)
                {
                    return HttpResult.Conflict("UDA_IMPORT_MACRO_CONFLICT",
                        "Existing import_macro_data_types.dat contains fields with conflicting types.",
                        new { conflicts = result.Conflicts });
                }

                return HttpResult.Ok(result);
            }
            catch (TeklaDisconnectedException ex)
            {
                return HttpResult.ServiceUnavailable("TEKLA_DISCONNECTED", ex.Message);
            }
            catch (TeklaStaleHandleException ex)
            {
                _log.Warn("uda.files.apply.stale", new { inner = ex.InnerException?.GetType().Name });
                return HttpResult.ServiceUnavailable("BRIDGE_STALE_TEKLA",
                    "Tekla remote references became stale after Tekla restart; in-process reconnect failed. Connector should hard-restart Bridge.Desktop.");
            }
            catch (Exception ex)
            {
                _log.Error("uda.files.apply.unhandled", new { err = ex.Message, type = ex.GetType().Name });
                return HttpResult.ServerError("UDA_FILES_APPLY_FAILED", ex.Message);
            }
        }

        private UdaFilesApplyResult ApplyToCurrentModel(Model model, UdaFilesApplyRequest request)
        {
            var info = model.GetInfo();
            var modelPath = Path.GetFullPath(info?.ModelPath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
            {
                throw new InvalidOperationException("Tekla model folder was not resolved or does not exist.");
            }

            var modelName = info?.ModelName ?? string.Empty;
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var dryRun = request.DryRun == true;
            var written = new List<UdaWrittenFile>();
            var skipped = new List<UdaMacroFieldAction>();
            var conflicts = new List<UdaMacroFieldConflict>();

            var objectsFileName = SafeObjectsFileName(request.ObjectsFile!.FileName!);
            var objectsPath = SafeChildPath(modelPath, objectsFileName);
            var objectsEncoding = ResolveEncoding(request.ObjectsFile.PreferredEncoding);
            var macroFileName = "import_macro_data_types.dat";
            var macroPath = SafeChildPath(modelPath, macroFileName);
            var additionalFiles = (request.AdditionalFiles ?? Array.Empty<UdaAdditionalFile>())
                .Select(SafeAdditionalFile)
                .ToArray();
            var ifcPropertySet = additionalFiles.FirstOrDefault(static file =>
                string.Equals(file.Role, "ifc-property-set-config", StringComparison.OrdinalIgnoreCase));
            var existingMacro = File.Exists(macroPath)
                ? File.ReadAllText(macroPath, Encoding.UTF8)
                : string.Empty;
            var merge = MergeManagedBlock(
                existingMacro,
                request.ImportMacro!.ManagedBlock ?? string.Empty,
                request.PresetId ?? "unknown");
            skipped.AddRange(merge.SkippedExisting);
            conflicts.AddRange(merge.Conflicts);

            if (conflicts.Count == 0 && !dryRun)
            {
                var objectsBackup = BackupIfExists(modelPath, objectsPath, timestamp);
                WriteTextAtomic(objectsPath, request.ObjectsFile.Content ?? string.Empty, objectsEncoding);
                written.Add(new UdaWrittenFile(
                    Role: "objects-inp",
                    FileName: objectsFileName,
                    Path: objectsPath,
                    BackupPath: objectsBackup,
                    Bytes: objectsEncoding.GetByteCount(request.ObjectsFile.Content ?? string.Empty)));

                var macroBackup = BackupIfExists(modelPath, macroPath, timestamp);
                WriteTextAtomic(macroPath, merge.MergedContent, Utf8NoBom());
                written.Add(new UdaWrittenFile(
                    Role: "import-macro-data-types",
                    FileName: macroFileName,
                    Path: macroPath,
                    BackupPath: macroBackup,
                    Bytes: Encoding.UTF8.GetByteCount(merge.MergedContent)));

                foreach (var file in additionalFiles)
                {
                    var targetPath = SafeAdditionalFilePath(modelPath, file.RelativeDirectory, file.FileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    var backup = BackupIfExists(modelPath, targetPath, timestamp);
                    var encoding = ResolveEncoding(file.PreferredEncoding);
                    WriteTextAtomic(targetPath, file.Content, encoding);
                    written.Add(new UdaWrittenFile(
                        Role: file.Role,
                        FileName: Path.Combine(file.RelativeDirectory, file.FileName),
                        Path: targetPath,
                        BackupPath: backup,
                        Bytes: encoding.GetByteCount(file.Content)));
                }

                if (ifcPropertySet is not null)
                {
                    written.Add(EnsureIfcExportSettings(
                        modelPath,
                        modelName,
                        ifcPropertySet.FileName,
                        timestamp,
                        dryRun: false));
                }
            }
            else if (conflicts.Count == 0)
            {
                written.Add(new UdaWrittenFile("objects-inp", objectsFileName, objectsPath, null, 0));
                written.Add(new UdaWrittenFile("import-macro-data-types", macroFileName, macroPath, null, 0));
                foreach (var file in additionalFiles)
                {
                    written.Add(new UdaWrittenFile(
                        file.Role,
                        Path.Combine(file.RelativeDirectory, file.FileName),
                        SafeAdditionalFilePath(modelPath, file.RelativeDirectory, file.FileName),
                        null,
                        0));
                }

                if (ifcPropertySet is not null)
                {
                    written.Add(EnsureIfcExportSettings(
                        modelPath,
                        modelName,
                        ifcPropertySet.FileName,
                        timestamp,
                        dryRun: true));
                }
            }

            _log.Info("uda.files.apply", new
            {
                request.PresetId,
                request.PresetName,
                modelName,
                modelPath,
                dryRun,
                written = written.Count,
                skipped = skipped.Count,
                conflicts = conflicts.Count
            });

            return new UdaFilesApplyResult(
                Ok: conflicts.Count == 0,
                DryRun: dryRun,
                PresetId: request.PresetId ?? string.Empty,
                PresetName: request.PresetName ?? string.Empty,
                ModelName: modelName,
                ModelPath: modelPath,
                WrittenFiles: written,
                SkippedExistingFields: skipped,
                Conflicts: conflicts,
                Message: conflicts.Count == 0
                    ? "UDA files were applied to the current Tekla model folder."
                    : "UDA files were not written because import macro conflicts were found.");
        }

        private static UdaWrittenFile EnsureIfcExportSettings(
            string modelPath,
            string modelName,
            string propertySetFileName,
            string timestamp,
            bool dryRun)
        {
            var attributesDir = SafeChildPath(modelPath, "attributes");
            var settingsPath = SafeChildPath(attributesDir, "standard.ifc4export.json");
            var targetName = SafeOutputName(ModelTitle(modelName));
            var outputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Tekla IFC",
                targetName) + Path.DirectorySeparatorChar;

            var settings = ReadIfcExportSettings(settingsPath);
            settings["DialogType"] = ValueOr(settings, "DialogType", "export.common");
            settings["Folder"] = outputFolder;
            settings["SelectionTypeIndex"] = ValueOr(settings, "SelectionTypeIndex", 1);
            settings["TargetFileName"] = targetName;
            settings["FileNameSuffix"] = ValueOr(settings, "FileNameSuffix", string.Empty);
            settings["FileNameTemplateData"] = ValueOr(settings, "FileNameTemplateData", string.Empty);
            settings["LocationByGuid"] = ValueOr(settings, "LocationByGuid", string.Empty);
            settings["SelectedObjectColoring"] = ValueOr(settings, "SelectedObjectColoring", "По классу объекта");
            settings["SelectedExportLayerAsItem"] = ValueOr(settings, "SelectedExportLayerAsItem", "Name");
            settings["NumberOfObjectsPerFile"] = ValueOr(settings, "NumberOfObjectsPerFile", 100);
            settings["SelectedSplittingType"] = ValueOr(settings, "SelectedSplittingType", 0);
            settings["ExportTypeIndex"] = ValueOr(settings, "ExportTypeIndex", 0);
            settings["BasePointExportIndex"] = ValueOr(settings, "BasePointExportIndex", 0);
            settings["CIPExportListIndex"] = ValueOr(settings, "CIPExportListIndex", 0);
            settings["SelectedAdditionalPropertySetName"] = Path.GetFileNameWithoutExtension(propertySetFileName);
            settings["SelectedExportFormat"] = "Ifc";
            settings["SelectedProtocol"] = settings.TryGetValue("SelectedProtocol", out var selectedProtocol)
                ? selectedProtocol
                : null;
            settings["PropertySetsIndex"] = ValueOr(settings, "PropertySetsIndex", 0);
            settings["IsFlatBeamsAsPlates"] = ValueOr(settings, "IsFlatBeamsAsPlates", false);
            settings["IsLocationFromOrganizer"] = false;
            settings["IsExcludeSinglePartAssemblies"] = ValueOr(settings, "IsExcludeSinglePartAssemblies", false);
            settings["IsPoursEnabled"] = ValueOr(settings, "IsPoursEnabled", false);
            settings["SelectedSharingUploadOptionIndex"] = ValueOr(settings, "SelectedSharingUploadOptionIndex", 0);
            settings["SelectedTrimbleConnectFolder"] = settings.TryGetValue("SelectedTrimbleConnectFolder", out var trimbleFolder)
                ? trimbleFolder
                : null;
            settings["SelectedTrimbleConnectProject"] = settings.TryGetValue("SelectedTrimbleConnectProject", out var trimbleProject)
                ? trimbleProject
                : null;
            settings["IsAssembliesEnabled"] = ValueOr(settings, "IsAssembliesEnabled", true);
            settings["IsBoltsEnabled"] = ValueOr(settings, "IsBoltsEnabled", false);
            settings["IsWeldsEnabled"] = ValueOr(settings, "IsWeldsEnabled", false);
            settings["IsGridsEnabled"] = ValueOr(settings, "IsGridsEnabled", false);
            settings["IsRebarsEnabled"] = ValueOr(settings, "IsRebarsEnabled", false);
            settings["IsSurfaceTreatmentsAndSurfacesEnabled"] = ValueOr(settings, "IsSurfaceTreatmentsAndSurfacesEnabled", false);
            settings["IsSpacesEnabled"] = ValueOr(settings, "IsSpacesEnabled", false);

            var content = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            if (!dryRun)
            {
                Directory.CreateDirectory(attributesDir);
                Directory.CreateDirectory(outputFolder);
                var backup = BackupIfExists(modelPath, settingsPath, timestamp);
                WriteTextAtomic(settingsPath, content, Utf8NoBom());
                return new UdaWrittenFile(
                    Role: "ifc-export-settings",
                    FileName: Path.Combine("attributes", "standard.ifc4export.json"),
                    Path: settingsPath,
                    BackupPath: backup,
                    Bytes: Utf8NoBom().GetByteCount(content));
            }

            return new UdaWrittenFile(
                Role: "ifc-export-settings",
                FileName: Path.Combine("attributes", "standard.ifc4export.json"),
                Path: settingsPath,
                BackupPath: null,
                Bytes: 0);
        }

        private static Dictionary<string, object?> ReadIfcExportSettings(string settingsPath)
        {
            if (!File.Exists(settingsPath))
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return new Dictionary<string, object?>(StringComparer.Ordinal);
                }

                var result = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    result[property.Name] = ConvertJsonValue(property.Value);
                }
                return result;
            }
            catch
            {
                return new Dictionary<string, object?>(StringComparer.Ordinal);
            }
        }

        private static object? ConvertJsonValue(JsonElement value)
            => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
                JsonValueKind.Number when value.TryGetDouble(out var number) => number,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => value.GetRawText()
            };

        private static object? ValueOr(Dictionary<string, object?> settings, string key, object? fallback)
            => settings.TryGetValue(key, out var value) ? value : fallback;

        private static string ModelTitle(string modelName)
        {
            var title = string.IsNullOrWhiteSpace(modelName) ? "TeklaModel" : modelName.Trim();
            if (title.EndsWith(".db1", StringComparison.OrdinalIgnoreCase) ||
                title.EndsWith(".db2", StringComparison.OrdinalIgnoreCase))
            {
                title = Path.GetFileNameWithoutExtension(title);
            }
            return string.IsNullOrWhiteSpace(title) ? "TeklaModel" : title;
        }

        private static string SafeOutputName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            var result = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(result) ? "TeklaModel" : result;
        }

        private static HttpResult? ValidateRequest(UdaFilesApplyRequest? request)
        {
            if (request is null)
            {
                return HttpResult.BadRequest("EMPTY_BODY", "Request body required.");
            }
            if (request.ObjectsFile is null)
            {
                return HttpResult.BadRequest("UDA_OBJECTS_FILE_REQUIRED", "objectsFile is required.");
            }
            if (request.ImportMacro is null)
            {
                return HttpResult.BadRequest("UDA_IMPORT_MACRO_REQUIRED", "importMacro is required.");
            }
            try
            {
                SafeObjectsFileName(request.ObjectsFile.FileName ?? string.Empty);
            }
            catch (Exception ex)
            {
                return HttpResult.BadRequest("UDA_OBJECTS_FILE_INVALID", ex.Message);
            }
            if (string.IsNullOrWhiteSpace(request.ObjectsFile.Content))
            {
                return HttpResult.BadRequest("UDA_OBJECTS_CONTENT_REQUIRED", "objectsFile.content is required.");
            }
            if (!string.Equals(Path.GetFileName(request.ImportMacro.FileName ?? string.Empty),
                    "import_macro_data_types.dat", StringComparison.OrdinalIgnoreCase))
            {
                return HttpResult.BadRequest("UDA_IMPORT_MACRO_FILE_INVALID",
                    "importMacro.fileName must be import_macro_data_types.dat.");
            }
            if (string.IsNullOrWhiteSpace(request.ImportMacro.ManagedBlock))
            {
                return HttpResult.BadRequest("UDA_IMPORT_MACRO_BLOCK_REQUIRED", "importMacro.managedBlock is required.");
            }
            foreach (var file in request.AdditionalFiles ?? Array.Empty<UdaAdditionalFile>())
            {
                try
                {
                    SafeAdditionalFile(file);
                }
                catch (Exception ex)
                {
                    return HttpResult.BadRequest("UDA_ADDITIONAL_FILE_INVALID", ex.Message);
                }
            }
            return null;
        }

        private static string SafeObjectsFileName(string value)
        {
            var fileName = Path.GetFileName(value.Trim());
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException("objects file name is empty.");
            }
            if (!fileName.StartsWith("objects_", StringComparison.OrdinalIgnoreCase) ||
                !fileName.EndsWith(".inp", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("objects file name must match objects_<suffix>.inp.");
            }
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("objects file name contains invalid characters.");
            }
            return fileName;
        }

        private static string SafeChildPath(string modelPath, string fileName)
        {
            var full = Path.GetFullPath(Path.Combine(modelPath, fileName));
            var root = Path.GetFullPath(modelPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved path is outside Tekla model folder.");
            }
            return full;
        }

        private static SafeUdaAdditionalFile SafeAdditionalFile(UdaAdditionalFile file)
        {
            var rawRole = file.Role ?? string.Empty;
            var rawRelativeDirectory = file.RelativeDirectory ?? string.Empty;
            var role = string.IsNullOrWhiteSpace(rawRole)
                ? "additional"
                : rawRole.Trim();
            var relativeDirectory = string.IsNullOrWhiteSpace(rawRelativeDirectory)
                ? string.Empty
                : rawRelativeDirectory.Trim().Trim('\\', '/');
            var fileName = Path.GetFileName((file.FileName ?? string.Empty).Trim());
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException("additionalFiles.fileName is empty.");
            }
            if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("additionalFiles.fileName contains invalid characters.");
            }
            var isAdditionalPsetsXml =
                string.Equals(relativeDirectory, "AdditionalPSets", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            var isAttributesObjectGroup =
                string.Equals(relativeDirectory, "attributes", StringComparison.OrdinalIgnoreCase) &&
                fileName.EndsWith(".SObjGrp", StringComparison.OrdinalIgnoreCase);

            if (!isAdditionalPsetsXml && !isAttributesObjectGroup)
            {
                throw new InvalidOperationException(
                    "additionalFiles must be AdditionalPSets/*.xml or attributes/*.SObjGrp.");
            }
            if (string.IsNullOrWhiteSpace(file.Content))
            {
                throw new InvalidOperationException("additionalFiles.content is required.");
            }

            return new SafeUdaAdditionalFile(
                role,
                isAdditionalPsetsXml ? "AdditionalPSets" : "attributes",
                fileName,
                file.PreferredEncoding,
                file.Content ?? string.Empty);
        }

        private static string SafeAdditionalFilePath(string modelPath, string relativeDirectory, string fileName)
        {
            var modelRoot = Path.GetFullPath(modelPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(modelPath, relativeDirectory, fileName));
            if (!full.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Resolved additional file path is outside Tekla model folder.");
            }
            return full;
        }

        private static string? BackupIfExists(string modelPath, string path, string timestamp)
        {
            if (!File.Exists(path))
            {
                return null;
            }
            var backupDirectory = SafeBackupDirectory(modelPath);
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(
                backupDirectory,
                $"{BackupFileStem(modelPath, path)}.bak-{timestamp}");
            File.Copy(path, backup, overwrite: false);
            return backup;
        }

        private static string SafeBackupDirectory(string modelPath)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("LocalApplicationData folder was not resolved.");
            }

            return Path.GetFullPath(Path.Combine(
                localAppData,
                "Platform",
                "Bridge",
                "model-backups",
                "uda",
                SanitizeBackupSegment(Path.GetFullPath(modelPath))));
        }

        private static string BackupFileStem(string modelPath, string path)
        {
            var modelRoot = Path.GetFullPath(modelPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            var stem = fullPath.StartsWith(modelRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(modelRoot.Length)
                : Path.GetFileName(fullPath);
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                stem = stem.Replace(invalid, '_');
            }
            return stem
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_');
        }

        private static string SanitizeBackupSegment(string value)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }
            return value
                .Replace(Path.DirectorySeparatorChar, '_')
                .Replace(Path.AltDirectorySeparatorChar, '_');
        }

        private static void WriteTextAtomic(string path, string content, Encoding encoding)
        {
            var tmp = $"{path}.tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tmp, NormalizeLineEndings(content), encoding);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(tmp, path);
        }

        private static Encoding ResolveEncoding(string? preferredEncoding)
        {
            if (string.Equals(preferredEncoding, "windows-1251", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(preferredEncoding, "cp1251", StringComparison.OrdinalIgnoreCase))
            {
                return Encoding.GetEncoding(1251);
            }

            return Utf8NoBom();
        }

        private static Encoding Utf8NoBom()
            => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private static UdaMacroMergeResult MergeManagedBlock(string existingContent, string managedBlock, string presetId)
        {
            var existingLines = SplitLines(existingContent);
            var desired = ParseMacroFields(managedBlock);
            var begin = $"// BEGIN PLATFORM UDA TAG preset={presetId}";
            var end = $"// END PLATFORM UDA TAG preset={presetId}";
            var outside = new List<string>();
            var inside = false;

            foreach (var line in existingLines)
            {
                if (line.Trim().Equals(begin, StringComparison.OrdinalIgnoreCase))
                {
                    inside = true;
                    continue;
                }
                if (inside && line.Trim().Equals(end, StringComparison.OrdinalIgnoreCase))
                {
                    inside = false;
                    continue;
                }
                if (!inside)
                {
                    outside.Add(line);
                }
            }

            var existingFields = ParseMacroFields(string.Join("\n", outside));
            var skipped = new List<UdaMacroFieldAction>();
            var conflicts = new List<UdaMacroFieldConflict>();
            var blockFields = new List<KeyValuePair<string, string>>();

            foreach (var item in desired)
            {
                if (existingFields.TryGetValue(item.Key, out var existingType))
                {
                    if (string.Equals(existingType, item.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add(new UdaMacroFieldAction(item.Key, item.Value, "existing",
                            "Field already exists outside Platform managed block with the same type."));
                    }
                    else
                    {
                        conflicts.Add(new UdaMacroFieldConflict(item.Key, item.Value, existingType,
                            "Existing import macro field has a different type."));
                    }
                    continue;
                }
                blockFields.Add(item);
            }

            var output = new List<string>(outside);
            while (output.Count > 0 && string.IsNullOrWhiteSpace(output[output.Count - 1]))
            {
                output.RemoveAt(output.Count - 1);
            }
            if (output.Count > 0)
            {
                output.Add(string.Empty);
            }
            output.Add(begin);
            output.Add("// Generated by Platform. Merge this block; do not replace external fields.");
            foreach (var item in blockFields)
            {
                output.Add($"{item.Key}, {item.Value}");
            }
            output.Add(end);
            output.Add(string.Empty);

            return new UdaMacroMergeResult(NormalizeLineEndings(string.Join("\n", output)), skipped, conflicts);
        }

        private static Dictionary<string, string> ParseMacroFields(string content)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in SplitLines(content))
            {
                var match = MacroFieldLineRegex.Match(line);
                if (!match.Success)
                {
                    continue;
                }
                var id = match.Groups["id"].Value.Trim();
                var type = match.Groups["type"].Value.Trim().ToUpperInvariant();
                if (!fields.ContainsKey(id))
                {
                    fields.Add(id, type);
                }
            }
            return fields;
        }

        private static string[] SplitLines(string content)
            => NormalizeLineEndings(content).Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.None);

        private static string NormalizeLineEndings(string value)
            => (value ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

        public sealed class UdaFilesApplyRequest
        {
            public string? PresetId { get; set; }
            public string? PresetName { get; set; }
            public bool? DryRun { get; set; }
            public UdaTextFile? ObjectsFile { get; set; }
            public UdaImportMacroFile? ImportMacro { get; set; }
            public IReadOnlyList<UdaAdditionalFile>? AdditionalFiles { get; set; }
        }

        public sealed class UdaTextFile
        {
            public string? FileName { get; set; }
            public string? PreferredEncoding { get; set; }
            public string? Content { get; set; }
        }

        public sealed class UdaImportMacroFile
        {
            public string? FileName { get; set; }
            public string? ManagedBlock { get; set; }
        }

        public sealed class UdaAdditionalFile
        {
            public string? Role { get; set; }
            public string? RelativeDirectory { get; set; }
            public string? FileName { get; set; }
            public string? PreferredEncoding { get; set; }
            public string? Content { get; set; }
        }

        private sealed record SafeUdaAdditionalFile(
            string Role,
            string RelativeDirectory,
            string FileName,
            string? PreferredEncoding,
            string Content);

        private sealed record UdaMacroMergeResult(
            string MergedContent,
            IReadOnlyList<UdaMacroFieldAction> SkippedExisting,
            IReadOnlyList<UdaMacroFieldConflict> Conflicts);

        public sealed record UdaFilesApplyResult(
            bool Ok,
            bool DryRun,
            string PresetId,
            string PresetName,
            string ModelName,
            string ModelPath,
            IReadOnlyList<UdaWrittenFile> WrittenFiles,
            IReadOnlyList<UdaMacroFieldAction> SkippedExistingFields,
            IReadOnlyList<UdaMacroFieldConflict> Conflicts,
            string Message);

        public sealed record UdaWrittenFile(
            string Role,
            string FileName,
            string Path,
            string? BackupPath,
            int Bytes);

        public sealed record UdaMacroFieldAction(
            string AttributeId,
            string FieldType,
            string Action,
            string Message);

        public sealed record UdaMacroFieldConflict(
            string AttributeId,
            string DesiredType,
            string ExistingType,
            string Message);
    }
}
