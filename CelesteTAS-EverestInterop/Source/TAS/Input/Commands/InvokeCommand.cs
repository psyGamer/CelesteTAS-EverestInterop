using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Celeste;
using Celeste.Mod;
using JetBrains.Annotations;
using Microsoft.Xna.Framework;
using Monocle;
using TAS.EverestInterop;
using TAS.EverestInterop.InfoHUD;
using TAS.Utils;

namespace TAS.Input.Commands;

#nullable enable

public static class InvokeCommand {
    private static bool logToConsole;

    private static void ReportError(string message) {
        if (logToConsole) {
            $"Invoke Command Failed: {message}".ConsoleLog(LogLevel.Error);
        } else {
            AbortTas($"Invoke Command Failed: {message}");
        }
    }

    [Monocle.Command("invoke", "Invoke level/session/entity method. eg invoke Level.Pause; invoke Player.Jump (CelesteTAS)"), UsedImplicitly]
    private static void ConsoleInvoke(string? arg1, string? arg2, string? arg3, string? arg4, string? arg5, string? arg6, string? arg7, string? arg8, string? arg9) {
        // TODO: Support arbitrary amounts of arguments
        string?[] args = [arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9];
        logToConsole = true;
        Invoke(args.TakeWhile(arg => arg != null).ToArray()!);
        logToConsole = false;
    }

    // Invoke, Level.Method, Parameters...
    // Invoke, Session.Method, Parameters...
    // Invoke, Entity.Method, Parameters...
    // Invoke, Type.StaticMethod, Parameters...
    [TasCommand("Invoke", LegalInMainGame = false)]
    private static void Invoke(string[] args) {
        if (args.Length < 1) {
            ReportError("Target-template required");
            return;
        }

        string template = args[0];
        string[] templateArgs = template.Split('.');

        var baseTypes = InfoTemplate.ResolveBaseTypes(templateArgs, out var memberArgs, out var entityId);
        if (baseTypes.IsEmpty()) {
            ReportError($"Failed to find base type for template '{template}'");
            return;
        }
        if (memberArgs.IsEmpty()) {
            ReportError("No members specified");
            return;
        }

        foreach (var type in baseTypes) {
            (var method, bool success) = InfoTemplate.ResolveMemberMethod(type, memberArgs);
            if (!success) {
                ReportError($"Failed to find method '{string.Join('.', memberArgs)}' on type '{type}'");
                return;
            }

            (object?[] values, success, string errorMessage) = InfoTemplate.ResolveValues(args[1..], method!.GetParameters().Select(param => param.ParameterType).ToArray());
            if (!success) {
                ReportError(errorMessage);
                return;
            }

            var instances = InfoTemplate.ResolveTypeInstances(type, entityId);
            success = InfoTemplate.InvokeMemberMethods(type, instances, values, memberArgs);
            if (!success) {
                ReportError($"Failed to invoke method '{string.Join('.', memberArgs)}' on type '{type}' to with parameters '{string.Join(';', values)}'");
                return;
            }
        }
    }
}
