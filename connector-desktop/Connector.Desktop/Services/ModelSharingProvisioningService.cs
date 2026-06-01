using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Connector.Desktop.Services;

// Provisions Tekla Structures on this PC for self-hosted Model Sharing:
//   (1) IL-patches bin\Features\SharingUIFeature.dll (license gate -> Ok + local token, identity -> synthetic
//       local user, so NOTHING calls Trimble Identity). Per-PC identity is baked from the connected device.
//   (2) writes bin\SharingConfiguration.xml redirecting the client to our on-prem coordinator (VPS net.tcp).
// Nothing else in the Tekla install is touched. Re-runnable (idempotent): always patches from the pristine
// backup, and detects when a Tekla service pack replaced the DLL (then re-captures the new pristine).
public sealed class ModelSharingProvisioningService
{
    private const string FeatureDllName = "SharingUIFeature.dll";
    private const string PristineBackupSuffix = ".trimble-orig";
    private const string StateSuffix = ".structura-ms.json";
    private const int FileReplaceMaxAttempts = 3;

    public string LogFilePath { get; }

    public ModelSharingProvisioningService()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ConnectorAgentDesktop");
        Directory.CreateDirectory(root);
        LogFilePath = Path.Combine(root, "model-sharing.log");
    }

    public bool IsTeklaRunning()
    {
        try
        {
            return Process.GetProcessesByName("TeklaStructures").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // Best-effort resolution of the Tekla "bin" folder that contains Features\SharingUIFeature.dll.
    // Order: explicit configured value -> derive from the Extensions local path -> scan C:\TeklaStructures\* -> default.
    public string ResolveTeklaBin(string? configuredBin, string? extensionsLocalPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredBin) && File.Exists(Path.Combine(configuredBin, "Features", FeatureDllName)))
        {
            return configuredBin.Trim();
        }

        // ...\<ver>\Environments\common\Extensions  ->  ...\<ver>\bin
        if (!string.IsNullOrWhiteSpace(extensionsLocalPath))
        {
            var idx = extensionsLocalPath.IndexOf(@"\Environments", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var candidate = Path.Combine(extensionsLocalPath.Substring(0, idx), "bin");
                if (File.Exists(Path.Combine(candidate, "Features", FeatureDllName)))
                {
                    return candidate;
                }
            }
        }

        try
        {
            const string teklaRoot = @"C:\TeklaStructures";
            if (Directory.Exists(teklaRoot))
            {
                var match = Directory.GetDirectories(teklaRoot)
                    .Select(d => Path.Combine(d, "bin"))
                    .Where(b => File.Exists(Path.Combine(b, "Features", FeatureDllName)))
                    .OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
        }
        catch
        {
            // Ignore scan failures and fall back to the default path.
        }

        return @"C:\TeklaStructures\2025.0\bin";
    }

    public ModelSharingStatus GetStatus(string teklaBin)
    {
        var status = new ModelSharingStatus { TeklaBin = teklaBin };
        var dll = Path.Combine(teklaBin, "Features", FeatureDllName);
        status.FeatureDllExists = File.Exists(dll);
        status.ConfigExists = File.Exists(Path.Combine(teklaBin, "SharingConfiguration.xml"));

        if (!status.FeatureDllExists)
        {
            return status;
        }

        var state = ReadState(dll + StateSuffix);
        if (state is null)
        {
            return status;
        }

        try
        {
            status.Provisioned = string.Equals(ComputeSha(File.ReadAllBytes(dll)), state.PatchedSha, StringComparison.OrdinalIgnoreCase) && status.ConfigExists;
            status.NeedsReapply = !status.Provisioned; // a Tekla service pack likely replaced our patched DLL
            status.IdentityEmail = state.IdentityEmail;
            status.IdentityName = state.IdentityName;
            status.ServerHost = state.ServerHost;
            status.ServerPort = state.ServerPort;
            status.AppliedUtc = state.AppliedUtc;
        }
        catch
        {
            // Treat unreadable state as not provisioned.
        }

        return status;
    }

    public ModelSharingProvisionResult Provision(ModelSharingProvisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdentityEmail))
        {
            return ModelSharingProvisionResult.Fail("Не удалось определить пользователя. Сначала подключитесь по токену устройства.");
        }

        if (IsTeklaRunning())
        {
            return ModelSharingProvisionResult.Fail("Сейчас запущена Tekla Structures. Закройте Tekla и повторите настройку Model Sharing.");
        }

        var teklaBin = request.TeklaBin.Trim();
        var featuresDir = Path.Combine(teklaBin, "Features");
        var live = Path.Combine(featuresDir, FeatureDllName);
        var backup = live + PristineBackupSuffix;
        var statePath = live + StateSuffix;

        if (!File.Exists(live))
        {
            return ModelSharingProvisionResult.Fail(
                "Не найден файл " + live + ". Проверьте путь к папке bin Tekla и версию Tekla Structures.");
        }

        var authToken = "local:" + request.IdentityEmail;
        var licenseToken = "local-license:" + request.IdentityEmail;

        try
        {
            AppendLog($"Старт настройки Model Sharing: bin='{teklaBin}', user='{request.IdentityEmail}' ({request.IdentityName}), server={request.ServerHost}:{request.ServerPort}.");

            // 1) Resolve the pristine (un-patched) DLL bytes, re-capturing the backup if a Tekla update replaced our patch.
            var (pristine, refreshedBackup) = ResolvePristine(live, backup, statePath);
            if (refreshedBackup)
            {
                AppendLog("Обнаружена новая (обновлённая) версия SharingUIFeature.dll — эталон пересохранён.");
            }

            // 2) Patch in memory.
            byte[] patched = PatchAssembly(pristine, teklaBin, request.IdentityEmail, request.IdentityName, authToken, licenseToken);

            // 3) Replace the live DLL atomically (temp -> replace, with retries for transient locks).
            ReplaceFile(live, patched);
            AppendLog("Пропатченная SharingUIFeature.dll установлена.");

            // 4) Write the redirect config.
            WriteSharingConfiguration(teklaBin, request.ServerHost, request.ServerPort);
            AppendLog($"Записан {Path.Combine(teklaBin, "SharingConfiguration.xml")} -> {request.ServerHost}:{request.ServerPort}.");

            // 5) Persist state for status detection + service-pack-aware re-provisioning.
            var state = new ModelSharingState
            {
                PristineSha = ComputeSha(pristine),
                PatchedSha = ComputeSha(patched),
                IdentityEmail = request.IdentityEmail,
                IdentityName = request.IdentityName,
                ServerHost = request.ServerHost,
                ServerPort = request.ServerPort,
                AppliedUtc = DateTimeOffset.UtcNow
            };
            WriteState(statePath, state);

            AppendLog("Настройка Model Sharing завершена успешно.");
            return ModelSharingProvisionResult.Success(
                "Tekla на этом компьютере готова к Model Sharing. Пользователь: " + request.IdentityEmail +
                ". Откройте Tekla, затем File -> Sharing.");
        }
        catch (ModelSharingPatchException ex)
        {
            AppendLog("Ошибка патча: " + ex.Message);
            return ModelSharingProvisionResult.Fail(
                "Не удалось пропатчить SharingUIFeature.dll. Возможно, версия Tekla отличается от поддерживаемой.", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog("Ошибка доступа к файлам: " + ex.Message);
            return ModelSharingProvisionResult.Fail(
                "Не удалось обновить файлы Tekla. Закройте Tekla Structures (и программы, открывшие папку bin) и повторите.", ex.Message);
        }
        catch (Exception ex)
        {
            AppendLog("Непредвиденная ошибка: " + ex);
            return ModelSharingProvisionResult.Fail("Не удалось настроить Model Sharing на этом компьютере.", ex.Message);
        }
    }

    private (byte[] Pristine, bool RefreshedBackup) ResolvePristine(string live, string backup, string statePath)
    {
        if (!File.Exists(backup))
        {
            // First ever provisioning on this PC: the live DLL is the genuine Trimble pristine.
            File.Copy(live, backup);
            return (File.ReadAllBytes(backup), false);
        }

        var liveSha = ComputeSha(File.ReadAllBytes(live));
        var state = ReadState(statePath);

        if (state is not null && string.Equals(liveSha, state.PatchedSha, StringComparison.OrdinalIgnoreCase))
        {
            // Live DLL is exactly the patch we produced -> the backup is its matching pristine.
            return (File.ReadAllBytes(backup), false);
        }

        if (state is not null &&
            !string.Equals(liveSha, state.PristineSha, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(state.PristineSha))
        {
            // Live differs from both our patch and our recorded pristine -> a Tekla update replaced the file.
            // Re-capture the new pristine.
            File.Copy(live, backup, overwrite: true);
            return (File.ReadAllBytes(backup), true);
        }

        // No state (legacy/manual backup) or live already equals the recorded pristine: trust the existing backup.
        return (File.ReadAllBytes(backup), false);
    }

    private static void ReplaceFile(string targetPath, byte[] content)
    {
        for (var attempt = 1; attempt <= FileReplaceMaxAttempts; attempt++)
        {
            var tempPath = targetPath + ".structura-ms-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(tempPath, content);
                if (File.Exists(targetPath))
                {
                    var info = new FileInfo(targetPath);
                    if ((info.Attributes & System.IO.FileAttributes.ReadOnly) != 0)
                    {
                        info.Attributes &= ~System.IO.FileAttributes.ReadOnly;
                    }
                    File.Copy(tempPath, targetPath, overwrite: true);
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, targetPath);
                }
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < FileReplaceMaxAttempts)
            {
                TryDelete(tempPath);
                Thread.Sleep(TimeSpan.FromMilliseconds(300 * attempt));
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void WriteSharingConfiguration(string teklaBin, string serverHost, int serverPort)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<SharingConfiguration>\r\n" +
            "    <Parameter>\r\n" +
            "        <ServiceType>OnPremises</ServiceType>\r\n" +
            "        <ServerName>" + serverHost + "</ServerName>\r\n" +
            "        <ServerPort>" + serverPort.ToString() + "</ServerPort>\r\n" +
            "    </Parameter>\r\n" +
            "</SharingConfiguration>\r\n";
        var path = Path.Combine(teklaBin, "SharingConfiguration.xml");
        File.WriteAllText(path, xml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void AppendLog(string message)
    {
        try
        {
            File.AppendAllText(LogFilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break provisioning.
        }
    }

    private static string ComputeSha(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private static ModelSharingState? ReadState(string statePath)
    {
        try
        {
            if (!File.Exists(statePath))
            {
                return null;
            }
            return JsonSerializer.Deserialize<ModelSharingState>(File.ReadAllText(statePath));
        }
        catch
        {
            return null;
        }
    }

    private static void WriteState(string statePath, ModelSharingState state)
    {
        try
        {
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Non-fatal: status detection degrades gracefully without the sidecar.
        }
    }

    // ---- dnlib IL patch (ported from the standalone self-host patcher) ----

    private static byte[] PatchAssembly(byte[] pristine, string teklaBin, string email, string name, string authToken, string licenseToken)
    {
        var resolver = new AssemblyResolver { EnableTypeDefCache = true };
        resolver.PostSearchPaths.Add(teklaBin);
        var mod = ModuleDefMD.Load(pristine, new ModuleContext(resolver));
        resolver.AddToCache(mod);

        var users = mod.Find("Sharing.Users", true);
        if (users is null)
        {
            throw new ModelSharingPatchException("Тип Sharing.Users не найден.");
        }

        var userTd = ResolveType(mod, resolver, "SharingServiceInterface", "SharingServiceInterface.User");
        var luTd = ResolveType(mod, resolver, "Sharing", "Sharing.Interfaces.LoggedInUser");
        if (userTd is null || luTd is null)
        {
            throw new ModelSharingPatchException("Типы User/LoggedInUser не разрешились (проверьте папку bin Tekla).");
        }

        var userCtor = mod.Import(userTd.FindDefaultConstructor());
        var userSetEmail = mod.Import(userTd.FindMethod("set_Email"));
        var userSetName = mod.Import(userTd.FindMethod("set_Name"));
        var luCtor = mod.Import(luTd.FindDefaultConstructor());
        var luSetUser = mod.Import(luTd.FindMethod("set_User"));
        var luSetToken = mod.Import(luTd.FindMethod("set_Token"));

        var t1 = mod.GetTypeRefs().FirstOrDefault(t => t.Namespace == "System.Threading.Tasks" && t.Name == "Task`1");
        var t0 = mod.GetTypeRefs().FirstOrDefault(t => t.Namespace == "System.Threading.Tasks" && t.Name == "Task");
        IResolutionScope scope = t1 is not null ? t1.ResolutionScope : (t0 is not null ? t0.ResolutionScope : mod.CorLibTypes.AssemblyRef);
        ITypeDefOrRef task1TypeRef = t1 is not null ? t1 : new TypeRefUser(mod, "System.Threading.Tasks", "Task`1", scope);
        ITypeDefOrRef taskTypeRef = t0 is not null ? t0 : new TypeRefUser(mod, "System.Threading.Tasks", "Task", scope);

        var ctx = new PatchContext(mod, userCtor, userSetEmail, userSetName, luCtor, luSetUser, luSetToken, taskTypeRef, task1TypeRef, email, name, authToken);

        // (1) license: GetLicense(type) -> SharingLicense(type, Ok(1), token, default)
        var getLicense = users.Methods.FirstOrDefault(m => m.Name == "GetLicense" && m.MethodSig is not null && m.MethodSig.Params.Count == 2);
        if (getLicense is null)
        {
            throw new ModelSharingPatchException("Метод GetLicense не найден.");
        }

        IMethod? slCtor = null;
        TypeSig? nullableDt = null;
        foreach (var ins in getLicense.Body.Instructions)
        {
            if (ins.OpCode == OpCodes.Newobj && ins.Operand is IMethod im && im.Name == ".ctor" && im.DeclaringType?.Name == "SharingLicense")
            {
                slCtor = im;
                nullableDt = im.MethodSig.Params[3];
                break;
            }
        }
        if (slCtor is null || nullableDt is null)
        {
            throw new ModelSharingPatchException("Конструктор SharingLicense не найден.");
        }

        var lb = new CilBody { InitLocals = true };
        var loc = new Local(nullableDt);
        lb.Variables.Add(loc);
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldloca, loc));
        lb.Instructions.Add(Instruction.Create(OpCodes.Initobj, nullableDt.ToTypeDefOrRef()));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldstr, licenseToken));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ldloc, loc));
        lb.Instructions.Add(Instruction.Create(OpCodes.Newobj, slCtor));
        lb.Instructions.Add(Instruction.Create(OpCodes.Ret));
        lb.MaxStack = 8;
        getLicense.Body = lb;

        // (2) identity: sync wrappers return a synthetic LoggedInUser
        foreach (var n in new[] { "Login", "GetCurrentLoggedInUser" })
        {
            PatchMethod(users, n, 0, b => { EmitLoggedInUser(b, ctx); b.Instructions.Add(Instruction.Create(OpCodes.Ret)); });
        }

        // async login paths -> Task.FromResult(syntheticLU)
        foreach (var n in new[] { "GetCurrentUserAsync", "LoginAsync", "Login" })
        {
            foreach (var m in users.Methods.Where(x => x.Name == n && IsTaskOf(x, "LoggedInUser")))
            {
                var b = new CilBody { InitLocals = true };
                EmitLoggedInUser(b, ctx);
                b.Instructions.Add(Instruction.Create(OpCodes.Call, FromResult(ctx, LoggedInUserSig(ctx))));
                b.Instructions.Add(Instruction.Create(OpCodes.Ret));
                b.MaxStack = 8;
                m.Body = b;
            }
        }

        PatchMethod(users, "GetCurrentUser", 0, b => { EmitUser(b, ctx); b.Instructions.Add(Instruction.Create(OpCodes.Ret)); });
        PatchMethod(users, "get_IsUserLogged", -1, b => { b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); b.Instructions.Add(Instruction.Create(OpCodes.Ret)); });

        foreach (var m in users.Methods.Where(x => x.Name == "GetNewToken"))
        {
            var b = new CilBody();
            b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, authToken));
            b.Instructions.Add(Instruction.Create(OpCodes.Ret));
            b.MaxStack = 1;
            m.Body = b;
        }

        foreach (var m in users.Methods.Where(x => x.Name == "ReleaseLicenseAsync" && IsTaskOf(x, "Boolean")))
        {
            var b = new CilBody();
            b.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            b.Instructions.Add(Instruction.Create(OpCodes.Call, FromResult(ctx, mod.CorLibTypes.Boolean)));
            b.Instructions.Add(Instruction.Create(OpCodes.Ret));
            b.MaxStack = 1;
            m.Body = b;
        }

        using var ms = new MemoryStream();
        mod.Write(ms);
        return ms.ToArray();
    }

    private static bool IsTaskOf(MethodDef m, string innerName)
    {
        if (m.MethodSig?.RetType is not GenericInstSig gi || gi.GenericArguments.Count != 1)
        {
            return false;
        }
        return gi.GenericArguments[0].TypeName == innerName;
    }

    private static void PatchMethod(TypeDef t, string name, int realParamCount, Action<CilBody> emit)
    {
        var m = t.Methods.FirstOrDefault(x => x.Name == name &&
            (realParamCount < 0 || (x.MethodSig is not null && x.MethodSig.Params.Count == realParamCount)) &&
            !x.HasGenericParameters);
        if (m is null)
        {
            return;
        }
        var b = new CilBody { InitLocals = true };
        emit(b);
        b.MaxStack = 8;
        m.Body = b;
    }

    private static void EmitUser(CilBody b, PatchContext c)
    {
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, c.UserCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, c.Email));
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.UserSetEmail));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, c.Name));
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.UserSetName));
    }

    private static void EmitLoggedInUser(CilBody b, PatchContext c)
    {
        b.Instructions.Add(Instruction.Create(OpCodes.Newobj, c.LuCtor));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        EmitUser(b, c);
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.LuSetUser));
        b.Instructions.Add(Instruction.Create(OpCodes.Dup));
        b.Instructions.Add(Instruction.Create(OpCodes.Ldstr, c.AuthToken));
        b.Instructions.Add(Instruction.Create(OpCodes.Callvirt, c.LuSetToken));
    }

    private static IMethod FromResult(PatchContext c, TypeSig argSig)
    {
        var frSig = MethodSig.CreateStaticGeneric(1, new GenericInstSig(new ClassSig(c.Task1TypeRef), new GenericMVar(0)), new GenericMVar(0));
        var frRef = new MemberRefUser(c.Module, "FromResult", frSig, c.TaskTypeRef);
        return new MethodSpecUser(frRef, new GenericInstMethodSig(argSig));
    }

    private static TypeSig LoggedInUserSig(PatchContext c) => c.LuCtor.DeclaringType.ToTypeSig();

    private static TypeDef? ResolveType(ModuleDefMD mod, AssemblyResolver resolver, string asmName, string fullName)
    {
        var aref = mod.GetAssemblyRefs().FirstOrDefault(a => a.Name == asmName);
        if (aref is null)
        {
            return null;
        }
        var asm = resolver.Resolve(aref, mod);
        if (asm is null)
        {
            return null;
        }
        foreach (var m in asm.Modules)
        {
            var t = m.Find(fullName, true);
            if (t is not null)
            {
                return t;
            }
        }
        return null;
    }

    private sealed class PatchContext
    {
        public PatchContext(ModuleDefMD module, IMethod userCtor, IMethod userSetEmail, IMethod userSetName,
            IMethod luCtor, IMethod luSetUser, IMethod luSetToken, ITypeDefOrRef taskTypeRef, ITypeDefOrRef task1TypeRef,
            string email, string name, string authToken)
        {
            Module = module;
            UserCtor = userCtor;
            UserSetEmail = userSetEmail;
            UserSetName = userSetName;
            LuCtor = luCtor;
            LuSetUser = luSetUser;
            LuSetToken = luSetToken;
            TaskTypeRef = taskTypeRef;
            Task1TypeRef = task1TypeRef;
            Email = email;
            Name = name;
            AuthToken = authToken;
        }

        public ModuleDefMD Module { get; }
        public IMethod UserCtor { get; }
        public IMethod UserSetEmail { get; }
        public IMethod UserSetName { get; }
        public IMethod LuCtor { get; }
        public IMethod LuSetUser { get; }
        public IMethod LuSetToken { get; }
        public ITypeDefOrRef TaskTypeRef { get; }
        public ITypeDefOrRef Task1TypeRef { get; }
        public string Email { get; }
        public string Name { get; }
        public string AuthToken { get; }
    }
}

public sealed class ModelSharingProvisionRequest
{
    public string TeklaBin { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 9990;
    public string IdentityEmail { get; set; } = "";
    public string IdentityName { get; set; } = "";
}

public sealed class ModelSharingProvisionResult
{
    public bool IsSuccess { get; init; }
    public string Message { get; init; } = "";
    public string TechnicalDetails { get; init; } = "";

    public static ModelSharingProvisionResult Success(string message) => new() { IsSuccess = true, Message = message };

    public static ModelSharingProvisionResult Fail(string message, string technicalDetails = "") =>
        new() { IsSuccess = false, Message = message, TechnicalDetails = technicalDetails };
}

public sealed class ModelSharingStatus
{
    public string TeklaBin { get; set; } = "";
    public bool FeatureDllExists { get; set; }
    public bool ConfigExists { get; set; }
    public bool Provisioned { get; set; }
    public bool NeedsReapply { get; set; }
    public string IdentityEmail { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public DateTimeOffset? AppliedUtc { get; set; }
}

internal sealed class ModelSharingState
{
    public string PristineSha { get; set; } = "";
    public string PatchedSha { get; set; } = "";
    public string IdentityEmail { get; set; } = "";
    public string IdentityName { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public DateTimeOffset? AppliedUtc { get; set; }
}

public sealed class ModelSharingPatchException : Exception
{
    public ModelSharingPatchException(string message) : base(message)
    {
    }
}
