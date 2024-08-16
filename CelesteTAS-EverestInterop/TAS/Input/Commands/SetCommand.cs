using System;
using System.Linq;
using System.Reflection;
using Celeste;
using Celeste.Mod;
using JetBrains.Annotations;
using Monocle;
using TAS.EverestInterop;
using TAS.Utils;

namespace TAS.Input.Commands;

#nullable enable

public static class SetCommand {
    private static bool logToConsole;

    private static void ReportError(string message) {
        if (logToConsole) {
            $"Set Command Failed: {message}".ConsoleLog(LogLevel.Error);
        } else {
            AbortTas($"Set Command Failed: {message}");
        }
    }

    [Monocle.Command("set", "'set Settings/Level/Session/Entity value' | Example: 'set DashMode Infinite', 'set Player.Speed 325 -52.5' (CelesteTAS)"), UsedImplicitly]
    private static void ConsoleSet(string? arg1, string? arg2, string? arg3, string? arg4, string? arg5, string? arg6, string? arg7, string? arg8, string? arg9) {
        // TODO: Support arbitrary amounts of arguments
        string?[] args = [arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9];
        logToConsole = true;
        Set(args.TakeWhile(arg => arg != null).ToArray()!);
        logToConsole = false;
    }

    // Set, Setting, Value
    // Set, Mod.Setting, Value
    // Set, Entity.Field, Value
    // Set, Type.StaticMember, Value
    [TasCommand("Set", LegalInMainGame = false)]
    private static void Set(string[] args) {
        if (args.Length < 2) {
            ReportError("Target-template and value required");
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

        // Handle special cases
        if (baseTypes.Count == 1 && baseTypes[0] == typeof(Settings) || baseTypes[0] == typeof(SaveData) || baseTypes[0] == typeof(Assists)) {
            SetGameSetting(memberArgs[0], args[1..]);
            return;
        }
        if (baseTypes.Count == 1 &&
            baseTypes[0].IsSameOrSubclassOf(typeof(EverestModuleSettings)) &&
            Everest.Modules.FirstOrDefault(mod => mod.SettingsType == baseTypes[0]) is { } module &&
            module.Metadata.Name == "ExtendedVariantMode")
        {
            SetExtendedVariant(memberArgs[0], args[1..]);
            return;
        }

        foreach (var type in baseTypes) {
            (var targetType, bool success) = InfoTemplate.ResolveMemberType(type, memberArgs);
            if (!success) {
                ReportError($"Failed to find members '{string.Join('.', memberArgs)}' on type '{type}'");
                return;
            }

            (object?[] values, success, string errorMessage) = InfoTemplate.ResolveValues(args[1..], [targetType]);
            if (!success) {
                ReportError(errorMessage);
                return;
            }

            var instances = InfoTemplate.ResolveTypeInstances(type, entityId);
            success = InfoTemplate.SetMemberValues(type, instances, values[0], memberArgs);
            if (!success) {
                ReportError($"Failed to set members '{string.Join('.', memberArgs)}' on type '{type}' to '{values[0]}'");
                return;
            }
        }
    }

    private static void SetGameSetting(string settingName, string[] valueArgs) {
        object? settings = null;

        FieldInfo? field;
        if ((field = typeof(Settings).GetField(settingName)) != null) {
            settings = Settings.Instance;
        } else if ((field = typeof(SaveData).GetField(settingName)) != null) {
            settings = SaveData.Instance;
        } else if ((field = typeof(Assists).GetField(settingName)) != null) {
            settings = SaveData.Instance.Assists;
        }

        if (settings == null || field == null) {
            return;
        }

        (object?[] values, bool success, string errorMessage) = InfoTemplate.ResolveValues(valueArgs, [field.FieldType]);
        if (!success) {
            ReportError(errorMessage);
            return;
        }

        if (!HandleSpecialCases(settingName, values[0])) {
            field.SetValue(settings, values[0]);

            // Assists is a struct, so it needs to be re-assign
            if (settings is Assists assists) {
                SaveData.Instance.Assists = assists;
            }
        }

        if (settings is Assists variantAssists && !Equals(variantAssists, Assists.Default)) {
            SaveData.Instance.VariantMode = true;
            SaveData.Instance.AssistMode = false;
        }
    }
    private static void SetExtendedVariant(string variantName, string[] valueArgs) {
        var variant = new Lazy<object>(ExtendedVariantsUtils.ParseVariant(variantName));
        var variantType = ExtendedVariantsUtils.GetVariantType(variant);
        if (variantType is null) {
            ReportError($"Failed to resolve type for extended variant '{variantName}'");
            return;
        }

        (object?[] values, bool success, string errorMessage) = InfoTemplate.ResolveValues(valueArgs, [variantType]);
        if (!success) {
            ReportError(errorMessage);
            return;
        }

        ExtendedVariantsUtils.SetVariantValue(variant, values[0]);
    }

    /// Applies the setting, while handing special cases
    private static bool HandleSpecialCases(string settingName, object? value) {
        var player = Engine.Scene.Tracker.GetEntity<Player>();
        var saveData = SaveData.Instance;
        var settings = Settings.Instance;

        switch (settingName) {
            // Assists
            case "GameSpeed":
                saveData.Assists.GameSpeed = (int) value!;
                Engine.TimeRateB = saveData.Assists.GameSpeed / 10f;
                break;
            case "MirrorMode":
                saveData.Assists.MirrorMode = (bool) value!;
                Celeste.Input.MoveX.Inverted = Celeste.Input.Aim.InvertedX = saveData.Assists.MirrorMode;
                Celeste.Input.Feather.InvertedX = saveData.Assists.MirrorMode;
                break;
            case "PlayAsBadeline":
                saveData.Assists.PlayAsBadeline = (bool) value!;
                if (player != null) {
                    var mode = saveData.Assists.PlayAsBadeline
                        ? PlayerSpriteMode.MadelineAsBadeline
                        : player.DefaultSpriteMode;
                    if (player.Active) {
                        player.ResetSpriteNextFrame(mode);
                    } else {
                        player.ResetSprite(mode);
                    }
                }
                break;
            case "DashMode":
                saveData.Assists.DashMode = (Assists.DashModes) value!;
                if (player != null) {
                    player.Dashes = Math.Min(player.Dashes, player.MaxDashes);
                }
                break;

            // SaveData
            case "VariantMode":
                saveData.VariantMode = (bool) value!;
                saveData.AssistMode = false;
                if (!saveData.VariantMode) {
                    Assists assists = default;
                    assists.GameSpeed = 10;
                    ResetVariants(assists);
                }
                break;
            case "AssistMode":
                saveData.AssistMode = (bool) value!;
                saveData.VariantMode = false;
                if (!saveData.AssistMode) {
                    Assists assists = default;
                    assists.GameSpeed = 10;
                    ResetVariants(assists);
                }
                break;

            // Settings
            case "Rumble":
                settings.Rumble = (RumbleAmount) value!;
                Celeste.Input.Rumble(RumbleStrength.Medium, RumbleLength.Medium);
                break;
            case "GrabMode":
                settings.GrabMode = (GrabModes) value!;
                Celeste.Input.ResetGrab();
                break;
            case "Fullscreen":
            case "WindowScale":
            case "VSync":
            case "MusicVolume":
            case "SFXVolume":
            case "Language":
                // Intentional no-op. A TAS should not modify these user preferences
                break;
            default:
                return false;
        }

        return true;
    }

    public static void ResetVariants(Assists assists) {
        SaveData.Instance.Assists = assists;
        HandleSpecialCases(nameof(Assists.DashMode), assists.DashMode);
        HandleSpecialCases(nameof(Assists.GameSpeed), assists.GameSpeed);
        HandleSpecialCases(nameof(Assists.MirrorMode), assists.MirrorMode);
        HandleSpecialCases(nameof(Assists.PlayAsBadeline), assists.PlayAsBadeline);
    }
}
