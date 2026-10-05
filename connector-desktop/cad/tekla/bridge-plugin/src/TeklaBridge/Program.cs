using System;
using System.Collections.Generic;
using System.IO;
using Tekla.Structures.Model;
using TeklaBridge.Infrastructure;

namespace TeklaBridge;

/// <summary>
/// Entry-point для TeklaBridge.exe. Читает команду из command.txt в
/// директории EXE, диспетчеризует на <see cref="BridgeCommands"/>,
/// пишет результат в result.txt. Mirrors decomp TeklaBridge.Program :84-126.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        _ = args; // dispatch через файл command.txt, аргументы не используются
        var store = new CommandFileStore(AppDomain.CurrentDomain.BaseDirectory);
        try
        {
            if (!store.TryReadCommand(out var command))
            {
                store.WriteResult("NO_COMMAND");
                return 0;
            }
            if (string.IsNullOrWhiteSpace(command))
            {
                store.WriteResult("EMPTY_COMMAND");
                return 0;
            }
            var model = new Model();
            if (!model.GetConnectionStatus())
            {
                store.WriteResult("ERROR: Tekla model is not connected");
                return 1;
            }
            var parts = command.Split(';');
            var action = parts[0].Trim().ToLowerInvariant();

            // Routing table — пока поддерживаем Step4 и Step5 (Step1-3 в прежнем
            // плагине были легаси-стадии для итеративного апдейта; web UI шлёт
            // только step4 / step5, остальные оставлены как ERROR в Step5 стабе).
            var router = new Dictionary<string, Func<Model, string[], string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["create_girder_step4"] = BridgeCommands.CreateGirderStep4,
                ["create_girder_step5"] = BridgeCommands.CreateGirderStep5,
            };

            if (!router.TryGetValue(action, out var handler))
            {
                store.WriteResult($"ERROR: unknown action '{action}'");
                return 1;
            }
            return handler(model, parts, store.ResultPath);
        }
        catch (Exception ex)
        {
            store.WriteResult("ERROR: " + ex.Message);
            return 1;
        }
    }
}
