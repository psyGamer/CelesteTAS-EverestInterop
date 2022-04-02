using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using Monocle;
using MonoMod.Cil;
using TAS.Entities;
using TAS.Module;
using TAS.Utils;

namespace TAS.Input;

public static class ConsoleCommandHandler {
    private static readonly FieldInfo MovementCounter = typeof(Actor).GetFieldInfo("movementCounter");
    private static Vector2 resetRemainder;
    private static Vector2 initSpeed;

    [Load]
    private static void Load() {
        On.Celeste.Level.LoadNewPlayer += LevelOnLoadNewPlayer;
        On.Celeste.Player.IntroRespawnEnd += PlayerOnIntroRespawnEnd;
        IL.Celeste.NPC06_Theo_Plateau.Awake += NPC06_Theo_PlateauOnAwake;
        On.Celeste.Level.End += LevelOnEnd;
    }

    [Unload]
    private static void Unload() {
        On.Celeste.Level.LoadNewPlayer -= LevelOnLoadNewPlayer;
        On.Celeste.Player.IntroRespawnEnd -= PlayerOnIntroRespawnEnd;
        IL.Celeste.NPC06_Theo_Plateau.Awake -= NPC06_Theo_PlateauOnAwake;
        On.Celeste.Level.End -= LevelOnEnd;
    }

    private static Player LevelOnLoadNewPlayer(On.Celeste.Level.orig_LoadNewPlayer orig, Vector2 position, PlayerSpriteMode spriteMode) {
        Player player = orig(position, spriteMode);

        if (resetRemainder != Vector2.Zero) {
            MovementCounter.SetValue(player, resetRemainder);
            resetRemainder = Vector2.Zero;
        }

        return player;
    }

    private static void PlayerOnIntroRespawnEnd(On.Celeste.Player.orig_IntroRespawnEnd orig, Player self) {
        orig(self);

        if (initSpeed != Vector2.Zero && self.Scene != null) {
            self.Scene.OnEndOfFrame += () => {
                self.Speed = initSpeed;
                initSpeed = Vector2.Zero;
            };
        }
    }

    private static void NPC06_Theo_PlateauOnAwake(ILContext il) {
        ILCursor ilCursor = new(il);
        if (!ilCursor.TryGotoNext(ins => ins.MatchCallvirt<Scene>("Add"))) {
            return;
        }

        Instruction skipCs06Campfire = ilCursor.Next.Next;
        if (!ilCursor.TryGotoPrev(MoveType.After, ins => ins.MatchCall<Entity>("Awake"))) {
            return;
        }

        Vector2 startPoint = new(-176, 312);
        ilCursor.EmitDelegate<Func<bool>>(() => {
            Session session = Engine.Scene.GetSession();
            bool skip = TasSettings.Enabled && (session.GetFlag("campfire_chat") || session.RespawnPoint != startPoint);
            if (skip && Engine.Scene.GetLevel() is { } level && level.GetPlayer() is { } player
                && level.Entities.FindFirst<NPC06_Theo_Plateau>() is { } theo && level.Tracker.GetEntity<Bonfire>() is { } bonfire) {
                session.SetFlag("campfire_chat");
                level.Session.BloomBaseAdd = 1f;
                level.Bloom.Base = AreaData.Get(level).BloomBase + 1f;
                level.Session.Dreaming = true;
                level.Add(new StarJumpController());
                level.Add(new CS06_StarJumpEnd(theo, player, new Vector2(-4, 312), new Vector2(-184, 177.6818f)));
                level.Add(new FlyFeather(new Vector2(88, 256), shielded: false, singleUse: false));
                bonfire.Activated = false;
                bonfire.SetMode(Bonfire.Mode.Lit);
                theo.Position = new Vector2(-40, 312);
                theo.Sprite.Play("sleep");
                theo.Sprite.SetAnimationFrame(theo.Sprite.CurrentAnimationTotalFrames - 1);
                if (level.Session.RespawnPoint == startPoint) {
                    player.Position = new Vector2(-4, 312);
                    player.Facing = Facings.Left;
                }
            }

            return skip;
        });
        ilCursor.Emit(OpCodes.Brtrue, skipCs06Campfire);
    }

    // fix CrystallineHelper.ForceDashCrystal keeps forcing dash dir when console load during freeze frames
    private static void LevelOnEnd(On.Celeste.Level.orig_End orig, Level self) {
        orig(self);

        if (TasSettings.Enabled && ModUtils.GetType("CrystallineHelper", "vitmod.ForceDashCrystal") is { } forceDashCrystal) {
            forceDashCrystal.SetFieldValue("dirToUse", null);
        }
    }

    // "Console CommandType",
    // "Console CommandType CommandArgs",
    // "Console LoadCommand IDorSID",
    // "Console LoadCommand IDorSID Screen",
    // "Console LoadCommand IDorSID Screen Spawnpoint",
    // "Console LoadCommand IDorSID PositionX PositionY"
    // "Console LoadCommand IDorSID PositionX PositionY SpeedX SpeedY"
    [TasCommand("Console", LegalInMainGame = false)]
    private static void ConsoleCommand(string[] arguments, string commandText) {
        string commandName = arguments[0].ToLower(CultureInfo.InvariantCulture);
        string[] args = arguments.Skip(1).ToArray();
        if (commandName is "load" or "hard" or "rmx2") {
            LoadCommand(commandName, args);
        } else {
            List<string> commandHistory = Engine.Commands.GetFieldValue<List<string>>("commandHistory");
            commandHistory?.Insert(0, commandText.Substring(8));
            Engine.Commands.ExecuteCommand(commandName, args);
            if (commandHistory?.IsNotEmpty() == true) {
                commandHistory.RemoveAt(0);
            }
        }
    }

    private static void LoadCommand(string command, string[] args) {
        try {
            if (SaveData.Instance == null || !Manager.AllowUnsafeInput && SaveData.Instance.FileSlot != -1) {
                SaveData data = SaveData.Instance ?? UserIO.Load<SaveData>(SaveData.GetFilename(-1)) ?? new SaveData();
                if (SaveData.Instance?.FileSlot is { } slot && slot != -1) {
                    SaveData.TryDelete(-1);
                    SaveData.LoadedModSaveDataIndex = -1;
                    foreach (EverestModule module in Everest.Modules) {
                        if (module._Session != null) {
                            module._Session.Index = -1;
                        }

                        if (module._SaveData != null) {
                            module._SaveData.Index = -1;
                        }
                    }

                    SaveData.Instance = data;
                    SaveData.Instance.FileSlot = -1;
                    UserIO.SaveHandler(true, true);
                } else {
                    SaveData.Start(data, -1);
                }

                // Complete Prologue if incomplete and make sure the return to map menu item will be shown
                LevelSetStats stats = data.GetLevelSetStatsFor("Celeste");
                if (!data.Areas[0].Modes[0].Completed) {
                    data.Areas[0].Modes[0].Completed = true;
                    stats.UnlockedAreas++;
                }
            }

            AreaMode mode = command switch {
                "hard" => AreaMode.BSide,
                "rmx2" => AreaMode.CSide,
                _ => AreaMode.Normal
            };

            if (!TryGetAreaId(args[0], out int areaId)) {
                Toast.Show($"Map {args[0]} does not exist");
                Manager.DisableRunLater();
                return;
            }

            if (args.Length > 1) {
                if (!double.TryParse(args[1], out double x) || args.Length == 2) {
                    string screen = args[1];
                    if (screen.StartsWith("lvl_")) {
                        screen = screen.Substring(4);
                    }

                    if (args.Length > 2) {
                        int spawnpoint = int.Parse(args[2]);
                        Load(mode, areaId, screen, spawnpoint);
                    } else {
                        Load(mode, areaId, screen);
                    }
                } else if (args.Length > 2 && double.TryParse(args[2], out double y)) {
                    Vector2 position = new((int) Math.Round(x), (int) Math.Round(y));
                    Vector2 remainder = new((float) (x - position.X), (float) (y - position.Y));

                    Vector2 speed = Vector2.Zero;
                    if (args.Length > 3 && float.TryParse(args[3], out float speedX)) {
                        speed.X = speedX;
                    }

                    if (args.Length > 4 && float.TryParse(args[4], out float speedY)) {
                        speed.Y = speedY;
                    }

                    Load(mode, areaId, position, remainder, speed);
                }
            } else {
                Load(mode, areaId);
            }
        } catch (Exception e) {
            e.LogException($"Console {command} command failed.");
        }
    }

    private static bool TryGetAreaId(string id, out int areaId) {
        if (int.TryParse(id, out areaId)) {
            return areaId >= 0 && areaId < AreaData.Areas.Count;
        } else {
            AreaData areaData = AreaData.Get(id);
            areaId = areaData?.ID ?? -1;
            return areaData != null;
        }
    }

    private static void Load(AreaMode mode, int areaId, string screen = null, int? spawnPoint = null) {
        AreaKey areaKey = new(areaId, mode);
        Session session = AreaData.GetCheckpoint(areaKey, screen) != null ? new Session(areaKey, screen) : new Session(areaKey);

        if (screen != null) {
            session.Level = screen;
            session.FirstLevel = session.LevelData == session.MapData.StartLevel();
        }

        Vector2? startPosition = null;
        if (spawnPoint != null) {
            LevelData levelData = session.MapData.Get(screen);
            startPosition = levelData.Spawns[spawnPoint.Value];
        }

        session.StartedFromBeginning = spawnPoint == null && session.FirstLevel;
        EnterLevel(new LevelLoader(session, startPosition));
    }

    private static void Load(AreaMode mode, int areaId, Vector2 spawnPoint, Vector2 remainder, Vector2 speed) {
        AreaKey areaKey = new(areaId, mode);
        Session session = new(areaKey);
        session.Level = session.MapData.GetAt(spawnPoint)?.Name;
        if (AreaData.GetCheckpoint(areaKey, session.Level) != null) {
            session = new Session(areaKey, session.Level);
        }

        session.FirstLevel = false;
        session.StartedFromBeginning = false;
        session.RespawnPoint = spawnPoint;
        resetRemainder = remainder;
        initSpeed = speed;
        EnterLevel(new LevelLoader(session));
    }

    private static void EnterLevel(LevelLoader levelLoader) {
        // fix game crash when leaving a map exist TileGlitcher
        if (Engine.Scene is Level level && ModUtils.IsInstalled("PandorasBox")) {
            foreach (Entity entity in level.Entities) {
                if (entity.GetType().FullName == "Celeste.Mod.PandorasBox.TileGlitcher") {
                    entity.Active = false;
                }
            }
        }

        Engine.Scene = levelLoader;
    }

    public static string CreateConsoleCommand(bool simple) {
        if (Engine.Scene is not Level level) {
            return null;
        }

        AreaKey area = level.Session.Area;
        string mode = null;
        switch (area.Mode) {
            case AreaMode.Normal:
                mode = "load";
                break;
            case AreaMode.BSide:
                mode = "hard";
                break;
            case AreaMode.CSide:
                mode = "rmx2";
                break;
        }

        string id = area.ID <= 10 ? area.ID.ToString() : area.GetSID();
        string separator = id.Contains(" ") ? ", " : " ";
        List<string> values = new() {"console", mode, id};

        if (!simple) {
            Player player = level.Tracker.GetEntity<Player>();
            if (player == null) {
                values.Add(level.Session.Level);
            } else {
                double x = player.X;
                double y = player.Y;
                double subX = player.PositionRemainder.X;
                double subY = player.PositionRemainder.Y;

                string format = "0.".PadRight(CelesteTasSettings.MaxDecimals + 2, '#');
                values.Add((x + subX).ToString(format, CultureInfo.InvariantCulture));
                values.Add((y + subY).ToString(format, CultureInfo.InvariantCulture));

                if (player.Speed != Vector2.Zero) {
                    values.Add(player.Speed.X.ToString(CultureInfo.InvariantCulture));
                    values.Add(player.Speed.Y.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        return string.Join(separator, values);
    }

    [Monocle.Command("giveberry", "Gives player a red berry (CelesteTAS)")]
    private static void CmdGiveBerry() {
        if (Engine.Scene is Level level && level.Tracker.GetEntity<Player>() is { } player) {
            EntityData entityData = new() {
                Position = player.Position + new Vector2(0f, -16f),
                ID = new Random().Next(),
                Name = "strawberry"
            };
            EntityID gid = new(level.Session.Level, entityData.ID);
            Strawberry entity2 = new(entityData, Vector2.Zero, gid);
            level.Add(entity2);
        }
    }

    [Monocle.Command("clrsav", "clears save data on debug file (CelesteTAS)")]
    private static void CmdClearSave() {
        SaveData.TryDelete(-1);
        SaveData.Start(new SaveData {Name = "debug"}, -1);
        // Pretend that we've beaten Prologue.
        LevelSetStats stats = SaveData.Instance.GetLevelSetStatsFor("Celeste");
        stats.UnlockedAreas = 1;
        stats.AreasIncludingCeleste[0].Modes[0].Completed = true;
    }
}