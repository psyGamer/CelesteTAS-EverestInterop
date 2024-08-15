using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CelesteStudio.Util;
using Eto.Forms;
using StudioCommunication;

namespace CelesteStudio.Communication;

public static class CommunicationWrapper {
    public static bool Connected => comm is { Connected: true };

    public static event Action? ConnectionChanged;
    public static event Action<StudioState, StudioState>? StateUpdated;
    public static event Action<Dictionary<int, string>>? LinesUpdated;
    public static event Action<GameSettings>? SettingsChanged;

    private static CommunicationAdapterStudio? comm;

    private static StudioState state = new();
    private static Dictionary<HotkeyID, List<WinFormsKeys>> bindings = [];
    private static GameSettings settings = new();

    public static void Start() {
        if (comm != null) {
            Console.Error.WriteLine("Tried to start the communication adapter while already running!");
            return;
        }

        comm = new CommunicationAdapterStudio(OnConnectionChanged, OnStateChanged, OnLinesChanged, OnBindingsChanged, OnSettingsChanged);
    }
    public static void Stop() {
        if (comm == null) {
            Console.Error.WriteLine("Tried to stop the communication adapter while not running!");
            return;
        }

        comm.Dispose();
        comm = null;
    }

    private static void OnConnectionChanged() {
        Application.Instance.AsyncInvoke(() => ConnectionChanged?.Invoke());
    }
    private static void OnStateChanged(StudioState newState) {
        var prevState = state;
        state = newState;
        Application.Instance.AsyncInvoke(() => StateUpdated?.Invoke(prevState, newState));
    }
    private static void OnLinesChanged(Dictionary<int, string> updateLines) {
        Application.Instance.AsyncInvoke(() => LinesUpdated?.Invoke(updateLines));
    }
    private static void OnBindingsChanged(Dictionary<HotkeyID, List<WinFormsKeys>> newBindings) {
        bindings = newBindings;
        foreach (var pair in bindings) {
            Console.WriteLine($"{pair.Key}: {string.Join(" + ", pair.Value.Select(key => key.ToString()))}");
        }
    }
    private static void OnSettingsChanged(GameSettings newSettings) {
        settings = newSettings;
        Application.Instance.AsyncInvoke(() => SettingsChanged?.Invoke(newSettings));
    }

    public static void ForceReconnect() {
        comm?.ForceReconnect();
    }

    public static void SendPath(string path) {
        if (Connected) {
            comm!.WritePath(path);
        }
    }
    public static void SyncSettings() {
        if (Connected) {
            comm!.WriteSettings(settings);
        }
    }
    public static void SendHotkey(HotkeyID hotkey) {
        if (Connected) {
            comm!.WriteHotkey(hotkey, false);
        }
    }
    public static bool SendKeyEvent(Keys key, Keys modifiers, bool released) {
        var winFormsKey = key.ToWinForms();

        foreach (HotkeyID hotkey in bindings.Keys) {
            var bindingKeys = bindings[hotkey];
            if (bindingKeys.Count == 0) continue;

            // Require the key without any modifiers (or the modifier being the same as the key)
            if (bindingKeys.Count == 1) {
                if ((bindingKeys[0] == winFormsKey) &&
                    ((modifiers == Keys.None) ||
                     (modifiers == Keys.Shift && key is Keys.Shift or Keys.LeftShift or Keys.RightShift) ||
                     (modifiers == Keys.Control && key is Keys.Control or Keys.LeftControl or Keys.RightControl) ||
                     (modifiers == Keys.Alt && key is Keys.Alt or Keys.LeftAlt or Keys.RightAlt)))
                {
                    if (Connected) {
                        comm!.WriteHotkey(hotkey, released);
                    }
                    return true;
                }

                continue;
            }

            // Binding has > 1 keys
            foreach (var bind in bindingKeys) {
                if (bind == winFormsKey)
                    continue;

                if (bind is WinFormsKeys.Shift or WinFormsKeys.LShiftKey or WinFormsKeys.RShiftKey && modifiers.HasFlag(Keys.Shift))
                    continue;
                if (bind is WinFormsKeys.Control or WinFormsKeys.LControlKey or WinFormsKeys.RControlKey && modifiers.HasFlag(Keys.Control))
                    continue;
                if (bind is WinFormsKeys.Menu or WinFormsKeys.LMenu or WinFormsKeys.RMenu && modifiers.HasFlag(Keys.Alt))
                    continue;

                // If only labeled for-loops would exist...
                goto NextIter;
            }

            if (Connected) {
                comm!.WriteHotkey(hotkey, released);
            }
            return true;

            NextIter:; // Yes, that ";" is required..
        }

        return false;
    }

    #region Data

    public static GameSettings GameSettings => settings;

    public static int CurrentLine => Connected ? state.CurrentLine : -1;
    public static string CurrentLineSuffix => Connected ? state.CurrentLineSuffix : string.Empty;
    public static int CurrentFrameInTas => Connected ? state.CurrentFrameInTAS : -1;
    public static int TotalFrames => Connected ? state.TotalFrames : -1;
    public static int SaveStateLine => Connected ? state.SaveStateLine : -1;
    public static States TasStates => Connected ? state.tasStates : States.None;
    public static string GameInfo => Connected ? state.GameInfo : string.Empty;
    public static string LevelName => Connected ? state.LevelName : string.Empty;
    public static string ChapterTime => Connected ? state.ChapterTime : string.Empty;
    public static bool ShowSubpixelIndicator => Connected && state.ShowSubpixelIndicator;
    public static (float X, float Y) SubpixelRemainder => Connected ? state.SubpixelRemainder : (0.0f, 0.0f);

    public static string GetConsoleCommand(bool simple) {
        if (!Connected) {
            return string.Empty;
        }

        return (string?)comm!.RequestGameData(GameDataType.ConsoleCommand, simple).Result ?? string.Empty;
    }
    public static string GetModURL() {
        if (!Connected) {
            return string.Empty;
        }

        return (string?)comm!.RequestGameData(GameDataType.ModUrl).Result ?? string.Empty;
    }
    public static string GetModInfo() {
        if (!Connected) {
            return string.Empty;
        }

        return (string?)comm!.RequestGameData(GameDataType.ModInfo).Result ?? string.Empty;
    }
    public static string GetExactGameInfo() {
        if (!Connected) {
            return string.Empty;
        }

        return (string?)comm!.RequestGameData(GameDataType.ExactGameInfo).Result ?? string.Empty;
    }

    private static async Task<CommandAutoCompleteEntry[]> RequestAutoCompleteEntries(GameDataType gameDataType, string argsText, int index) {
        if (!Connected) {
            return [];
        }

        // This is pretty heavy computationally, so we need a higher timeout
        return (CommandAutoCompleteEntry[]?)await comm!.RequestGameData(gameDataType, (argsText, index), TimeSpan.FromSeconds(15)).ConfigureAwait(false) ?? [];
    }
    public static Task<CommandAutoCompleteEntry[]> RequestSetCommandAutoCompleteEntries(string argsText, int index) => RequestAutoCompleteEntries(GameDataType.SetCommandAutoCompleteEntries, argsText, index);
    public static Task<CommandAutoCompleteEntry[]> RequestInvokeCommandAutoCompleteEntries(string argsText, int index) => RequestAutoCompleteEntries(GameDataType.InvokeCommandAutoCompleteEntries, argsText, index);

    public static T? GetRawData<T>(string template, bool alwaysList = false) {
        if (!Connected) {
            return default;
        }

        return (T?)comm!.RequestGameData(GameDataType.RawInfo, (template, alwaysList), TimeSpan.FromSeconds(15), typeof(T)).Result ?? default;
    }

    public static async Task<GameState?> GetGameState() {
        if (!Connected) {
            return null;
        }

        return (GameState?)await comm!.RequestGameData(GameDataType.GameState).ConfigureAwait(false);
    }

    #endregion

    #region Actions

    public static string GetCustomInfoTemplate() {
        if (!Connected) {
            return string.Empty;
        }

        return (string?)comm!.RequestGameData(GameDataType.CustomInfoTemplate).Result ?? string.Empty;
    }
    public static void SetCustomInfoTemplate(string customInfoTemplate) {
        if (!Connected) {
            return;
        }

        comm!.WriteCustomInfoTemplate(customInfoTemplate);
    }

    public static void ClearWatchEntityInfo() {
        if (!Connected) {
            return;
        }

        comm!.WriteClearWatchEntityInfo();
    }

    public static void RecordTAS(string fileName) {
        if (!Connected) {
            return;
        }

        comm!.WriteRecordTAS(fileName);
    }

    #endregion
}
