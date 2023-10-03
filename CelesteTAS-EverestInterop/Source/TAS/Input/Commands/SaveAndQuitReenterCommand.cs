using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Celeste;
using Mono.Cecil.Cil;
using Monocle;
using TAS.Module;
using TAS.Utils;

namespace TAS.Input.Commands; 

public static class SaveAndQuitReenterCommand {
    public enum SaveAndQuitReenterMode {
        Input,
        Simulate
    }
    
    public class LevelReenter : Scene {
        public LevelReenter(Session session) {
            AreaData.Get(session).RestoreASideAreaData();
        }
        
        public override void Begin() {
            base.Begin();
            
            Entity routine = new() {new Coroutine(Routine())};
            Add(routine);
            Add(new HudRenderer());
        }

        private IEnumerator Routine() {
            UserIO.SaveHandler(file: true, settings: true);
            while (UserIO.Saving) yield return null;
            while (SaveLoadIcon.OnScreen) yield return null;
            
            int slot = SaveData.Instance.FileSlot;
            var saveData = UserIO.Load<SaveData>(SaveData.GetFilename(slot));
            SaveData.Start(saveData, slot);
          
            LevelEnter.Go(SaveData.Instance.CurrentSession, fromSaveData: true);
        }
    }

    private static bool justPressedSnQ = false;
    public static SaveAndQuitReenterMode? LocalMode;
    public static SaveAndQuitReenterMode? GlobalModeParsing;
    public static SaveAndQuitReenterMode? GlobalModeRuntime;

    private static SaveAndQuitReenterMode Mode {
        get {
            if (LibTasHelper.Exporting) {
                return SaveAndQuitReenterMode.Input;
            }
            
            if (EnforceLegalCommand.EnabledWhenParsing) {
                return SaveAndQuitReenterMode.Input;
            }

            SaveAndQuitReenterMode? globalMode = ParsingCommand ? GlobalModeParsing : GlobalModeRuntime;
            return LocalMode ?? globalMode ?? SaveAndQuitReenterMode.Simulate;
        }
    }

    private static int ActiveFileSlot {
        get {
            if (LibTasHelper.Exporting) {
                return 0;
            }
            
            if (Engine.Scene is Overworld {Current: OuiFileSelect select}) {
                return select.SlotIndex;
            }

            return SaveData.Instance?.FileSlot ?? -1;
        }
    }

    private static bool preventClear = false;
    // Contains which slot was used for each command, to ensure that inputs before the current frame stay the same
    public static Dictionary<int, int> InsertedSlots = new();
    
    [Load]
    private static void Load() {
        typeof(Level)
            .GetNestedType("<>c__DisplayClass149_0", BindingFlags.NonPublic)
            .GetMethod("<Pause>b__8", BindingFlags.NonPublic | BindingFlags.Instance)
            .IlHook((cursor, _) => cursor.Emit(OpCodes.Ldc_I4_1)
                .Emit(OpCodes.Stsfld, typeof(SaveAndQuitReenterCommand).GetFieldInfo(nameof(justPressedSnQ))));
        
        typeof(Level).GetMethod("Update").IlHook((cursor, _) => cursor.Emit(OpCodes.Ldc_I4_0)
                .Emit(OpCodes.Stsfld, typeof(SaveAndQuitReenterCommand).GetFieldInfo(nameof(justPressedSnQ))));
    }

    [ClearInputs]
    private static void Clear() {
        if (preventClear) return;
        InsertedSlots.Clear();
    }

    [ClearInputs]
    [ParseFileEnd]
    private static void ParseFileEnd() {
        GlobalModeParsing = null;
    }

    [DisableRun]
    private static void DisableRun() {
        LocalMode = null;
        GlobalModeRuntime = null;
        justPressedSnQ = false;
    }
    
    [TasCommand("SaveAndQuitReenter", ExecuteTiming = ExecuteTiming.Parse | ExecuteTiming.Runtime)]
    private static void SaveAndQuitReenter(string[] args, int studioLine, string filePath, int fileLine) {
        LocalMode = null;

        if (args.IsNotEmpty()) {
            if (Enum.TryParse(args[0], true, out SaveAndQuitReenterMode value)) {
                LocalMode = value;
            } else if (ParsingCommand) {
                AbortTas("SaveAndQuitReenter command failed.\nMode must be Input or Simulate");
                return;
            }
        }

        if (ParsingCommand) {
            if (Mode == SaveAndQuitReenterMode.Simulate) {
                // Wait for the Save & Quit wipe
                Manager.Controller.AddFrames("32", studioLine);
            } else {
                if (SafeCommand.DisallowUnsafeInputParsing) {
                    AbortTas("\"SaveAndQuitReenter, Input\" requires unsafe inputs");
                    return;
                }
                
                int slot = ActiveFileSlot;
                if (InsertedSlots.TryGetValue(studioLine, out int prevSlot)) {
                    slot = prevSlot;
                }

                LibTasHelper.AddInputFrame("58");
                Manager.Controller.AddFrames("31", studioLine);
                Manager.Controller.AddFrames("14", studioLine);

                if (slot == -1) {
                    // Load debug slot
                    Manager.Controller.AddFrames("1,D", studioLine);
                    Manager.Controller.AddFrames("1,O", studioLine);
                    Manager.Controller.AddFrames("33", studioLine);
                } else {
                    // Get to the save files screen
                    Manager.Controller.AddFrames("1,O", studioLine);
                    Manager.Controller.AddFrames("56", studioLine);
                    // Alternate 1,D and 1,F,180 to select the slot
                    for (int i = 0; i < slot; i++) {
                        Manager.Controller.AddFrames(i % 2 == 0 ? "1,D" : "1,F,180", studioLine);
                    }
                    // Load the selected save file
                    Manager.Controller.AddFrames("1,O", studioLine);
                    Manager.Controller.AddFrames("14", studioLine);
                    Manager.Controller.AddFrames("1,O", studioLine);
                    Manager.Controller.AddFrames("1", studioLine);
                    LibTasHelper.AddInputFrame("32");
                }

                InsertedSlots[studioLine] = slot;
            }
            
            return;
        }

        if (!justPressedSnQ) {
            AbortTas("SaveAndQuitReenter must be exactly after pressing the \"Save & Quit\" button");
            return;
        }
        
        if (Engine.Scene is not Level level) {
            AbortTas("SaveAndQuitReenter can't be used outside levels");
            return;
        }

        if (Mode == SaveAndQuitReenterMode.Simulate) {
            // Replace the Save & Quit wipe with our work action
            level.Wipe.OnComplete = delegate {
                Engine.Scene = new LevelReenter(level.Session);
            };
        } else {
            // Re-insert inputs of the save file slot changed
            if (InsertedSlots.TryGetValue(studioLine, out int slot) && slot != ActiveFileSlot) {
                InsertedSlots[studioLine] = ActiveFileSlot;
                // Avoid clearing our InsertedSlots info
                preventClear = true;
                Manager.Controller.NeedsReload = true;
                Manager.Controller.RefreshInputs(enableRun: false);
                preventClear = false;
            }
        }
    }
    
    [TasCommand("SaveAndQuitReenterMode", ExecuteTiming = ExecuteTiming.Parse | ExecuteTiming.Runtime)]
    private static void StunPauseCommandMode(string[] args) {
        if (args.IsNotEmpty() && Enum.TryParse(args[0], true, out SaveAndQuitReenterMode value)) {
            if (ParsingCommand) {
                GlobalModeParsing = value;
            } else {
                GlobalModeRuntime = value;
            }
        } else if (ParsingCommand) {
            AbortTas("SaveAndQuitReenterMode command failed.\nMode must be Input or Simulate");
        }
    }
}