using System;
using System.Collections.Concurrent;
using System.Linq;
using Celeste;
using Celeste.Mod;
using Celeste.Pico8;
using JetBrains.Annotations;
using Microsoft.Xna.Framework;
using Monocle;
using StudioCommunication;
using TAS.Communication;
using TAS.EverestInterop;
using TAS.Input;
using TAS.Module;
using TAS.Utils;

namespace TAS;

[AttributeUsage(AttributeTargets.Method), MeansImplicitUse]
internal class EnableRunAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method), MeansImplicitUse]
internal class DisableRunAttribute : Attribute;

public static class Manager {
    public enum State {
        /// No TAS is currently active
        Disabled,
        /// Plays the current TAS back at the specified PlaybackSpeed
        Running,
        /// Pauses the current TAS
        Paused,
        /// Advances the current TAS by 1 frame and resets back to Paused
        FrameAdvance,
        /// Forwards the TAS while paused
        SlowForward,
    }

    public static bool Running => CurrState != State.Disabled;
    public static bool FastForwarding => Running && PlaybackSpeed >= 5.0f;
    public static float PlaybackSpeed { get; private set; } = 1.0f;

    public static State CurrState, NextState;

    public static readonly InputController Controller = new();

    private static readonly ConcurrentQueue<Action> mainThreadActions = new();

    static Manager() {
        AttributeUtils.CollectMethods<EnableRunAttribute>();
        AttributeUtils.CollectMethods<DisableRunAttribute>();
    }

#if DEBUG
    // Hot-reloading support
    [Load]
    private static void RestoreStudioTasFilePath() {
        Controller.FilePath = Engine.Instance.GetDynamicDataInstance()
            .Get<string>("CelesteTAS_FilePath");

        Everest.Events.AssetReload.OnBeforeReload += OnAssetReload;
    }

    [Unload]
    private static void SaveStudioTasFilePath() {
        Engine.Instance.GetDynamicDataInstance()
            .Set("CelesteTAS_FilePath", Controller.FilePath);

        Everest.Events.AssetReload.OnBeforeReload -= OnAssetReload;

        Controller.Stop();
        Controller.Clear();
    }

    private static void OnAssetReload(bool silent) => DisableRun();
#endif

    public static void EnableRun()
    {
        if (Running) {
            return;
        }

        $"Starting TAS: {Controller.FilePath}".Log();
        Environment.StackTrace.Log(LogLevel.Verbose);

        CurrState = NextState = State.Running;
        PlaybackSpeed = 1.0f;

        AttributeUtils.Invoke<EnableRunAttribute>();
        Controller.Stop();
        Controller.RefreshInputs();
    }

    public static void DisableRun()
    {
        if (!Running) {
            return;
        }

        "Stopping TAS".Log();
        Environment.StackTrace.Log(LogLevel.Verbose);

        CurrState = NextState = State.Disabled;
        AttributeUtils.Invoke<DisableRunAttribute>();
        Controller.Stop();
    }

    /// Will start the TAS on the next update cycle
    public static void EnableRunLater() => NextState = State.Running;
    /// Will stop the TAS on the next update cycle
    public static void DisableRunLater() => NextState = State.Disabled;

    /// Updates the TAS itself
    public static void Update() {
        if (!Running && NextState == State.Running) {
            EnableRun();
        }
        if (Running && NextState == State.Disabled) {
            DisableRun();
        }

        CurrState = NextState;

        while (mainThreadActions.TryDequeue(out Action action)) {
            action.Invoke();
        }

        if (Running && CurrState != State.Paused && !IsLoading()) {
            if (Controller.HasFastForward) {
                NextState = State.Running;
            }

            if (!Controller.CanPlayback) {
                DisableRun();
                return;
            }

            Controller.AdvanceFrame();

            // Pause the TAS if breakpoint is not placed at the end
            if (Controller.Break) {
                Controller.NextLabelFastForward = null;
                NextState = State.Paused;
            }
        }
    }

    /// Updates everything around the TAS itself, like hotkeys, studio-communication, etc.
    public static void UpdateMeta() {
        Hotkeys.Update();
        SendStudioState();

        // Check if the TAS should be enabled / disabled
        // NOTE: Do not use Hotkeys.Restart.Pressed unless the fast forwarding optimization in Hotkeys.Update() is removed
        if (Hotkeys.StartStop.Released) {
            if (Running) {
                DisableRun();
            } else {
                EnableRun();
            }
            return;
        }

        if (Hotkeys.Restart.Released) {
            DisableRun();
            EnableRun();
            return;
        }

        if (Running && Hotkeys.FastForwardComment.Pressed) {
            Controller.FastForwardToNextLabel();
            return;
        }

        switch (CurrState) {
            case State.Running:
                if (Hotkeys.PauseResume.Pressed) {
                    NextState = State.Paused;
                }
                break;

            case State.FrameAdvance:
                NextState = State.Paused;
                break;

            case State.Paused:
                if (Hotkeys.PauseResume.Pressed) {
                    NextState = State.Running;
                } else if (Hotkeys.FrameAdvance.Pressed || Hotkeys.FastForward.Check) {
                    NextState = State.FrameAdvance;
                }
                break;

            case State.Disabled:
            default:
                break;
        }

        // Apply fast / slow forwarding
        switch (NextState)
        {
            case State.Paused or State.SlowForward when Hotkeys.SlowForward.Check:
                PlaybackSpeed = TasSettings.SlowForwardSpeed;
                NextState = State.SlowForward;
                break;
            case State.Paused or State.SlowForward:
                PlaybackSpeed = 1.0f;
                NextState = State.Paused;
                break;

            case State.Running when Hotkeys.FastForward.Check:
                PlaybackSpeed = TasSettings.FastForwardSpeed;
                break;
            case State.Running when Hotkeys.SlowForward.Check:
                PlaybackSpeed = TasSettings.SlowForwardSpeed;
                break;

            default:
                PlaybackSpeed = Controller.HasFastForward ? Controller.CurrentFastForward!.Speed : 1.0f;
                break;
        }
    }

    /// Queues an action to be performed on the main thread
    public static void AddMainThreadAction(Action action) {
        mainThreadActions.Enqueue(action);
    }

    /// TAS-execution is paused during loading screens
    public static bool IsLoading() {
        return Engine.Scene switch {
            Level level => level.IsAutoSaving() && level.Session.Level == "end-cinematic",
            SummitVignette summit => !summit.ready,
            Overworld overworld => overworld.Current is OuiFileSelect { SlotIndex: >= 0 } slot && slot.Slots[slot.SlotIndex].StartingGame ||
                                   overworld.Next is OuiChapterSelect && UserIO.Saving ||
                                   overworld.Next is OuiMainMenu && (UserIO.Saving || Everest._SavingSettings),
            Emulator emulator => emulator.game == null,
            _ => Engine.Scene is LevelExit or LevelLoader or GameLoader || Engine.Scene.GetType().Name == "LevelExitToLobby",
        };
    }

    private static void SendStudioState() {
        var remainder = Vector2.Zero;
        if (Engine.Scene is Level level && level.GetPlayer() is { } player) {
            remainder = player.movementCounter;
        } else if (Engine.Scene is Emulator emulator && emulator.game?.objects.FirstOrDefault(o => o is Classic.player) is Classic.player classicPlayer) {
            remainder = classicPlayer.rem;
        }

        InputFrame previous = Controller.Previous;
        StudioState state = new() {
            CurrentLine = previous?.Line ?? -1,
            CurrentLineSuffix = $"{Controller.CurrentFrameInInput + (previous?.FrameOffset ?? 0)}{previous?.RepeatString ?? ""}",
            CurrentFrameInTAS = Controller.CurrentFrameInTAS,
            TotalFrames = Controller.Inputs.Count,
            SaveStateLine = Savestates.StudioHighlightLine,
            tasStates = 0,
            GameInfo = GameInfo.StudioInfo,
            LevelName = GameInfo.LevelName,
            ChapterTime = GameInfo.ChapterTime,
            ShowSubpixelIndicator = TasSettings.InfoSubpixelIndicator && Engine.Scene is Level or Emulator,
            SubpixelRemainder = (remainder.X, remainder.Y),
        };
        CommunicationWrapper.SendState(state);
    }
}