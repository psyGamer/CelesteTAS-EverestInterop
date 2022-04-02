using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Celeste;
using Celeste.Mod;
using Microsoft.Xna.Framework;
using Monocle;
using TAS.Entities;
using TAS.Utils;

// ReSharper disable UnusedMember.Local

namespace TAS.Input;

public static class InputCommands {
    private static readonly object[] EmptyParameters = { };
    private static readonly Regex CheckSpaceRegex = new(@"^[^,]+?\s+[^,]", RegexOptions.Compiled);
    private static readonly Regex SpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Lazy<PropertyInfo> GunInputCursorPosition =
        new(() => ModUtils.GetType("Guneline", "Guneline.GunInput")?.GetProperty("CursorPosition"));

    private static readonly Lazy<MethodInfo> GunlineGunshot = new(() => ModUtils.GetType("Guneline", "Guneline.Guneline")?.GetMethod("Gunshot"));

    private static string[] Split(string line) {
        string trimLine = line.Trim();
        // Determined by the first separator
        string[] args = CheckSpaceRegex.IsMatch(trimLine) ? SpaceRegex.Split(trimLine) : trimLine.Split(',');
        return args.Select(text => text.Trim()).ToArray();
    }

    public static bool TryParseCommand(InputController inputController, string filePath, int fileLine, string lineText, int frame,
        int studioLine) {
        try {
            if (!string.IsNullOrEmpty(lineText) && char.IsLetter(lineText[0])) {
                string[] args = Split(lineText);
                string commandName = args[0];

                KeyValuePair<TasCommandAttribute, MethodInfo> pair = TasCommandAttribute.FindMethod(commandName);
                if (pair.Equals(default)) {
                    return false;
                }

                MethodInfo method = pair.Value;
                TasCommandAttribute attribute = pair.Key;

                string[] commandArgs = args.Skip(1).ToArray();

                ParameterInfo[] parameterInfos = method.GetParameters();
                object[] parameters = parameterInfos.Length switch {
                    4 => new object[] {commandArgs, studioLine, filePath, fileLine},
                    3 => new object[] {commandArgs, studioLine, filePath},
                    2 when parameterInfos[1].ParameterType == typeof(int) => new object[] {commandArgs, studioLine},
                    2 when parameterInfos[1].ParameterType == typeof(string) => new object[] {commandArgs, lineText.Trim()},
                    1 => new object[] {commandArgs},
                    0 => EmptyParameters,
                    _ => throw new ArgumentException()
                };

                Action commandCall = () => method.Invoke(null, parameters);
                Command command = new(attribute, frame, commandCall, commandArgs, filePath, studioLine);

                if (attribute.ExecuteTiming == ExecuteTiming.Parse) {
                    commandCall.Invoke();
                }

                if (!inputController.Commands.ContainsKey(frame)) {
                    inputController.Commands[frame] = new List<Command>();
                }

                inputController.Commands[frame].Add(command);

                //the play command needs to stop reading the current file when it's done to prevent recursion
                return command.Attribute.IsName("Play");
            }

            return false;
        } catch (Exception e) {
            e.LogException("Failed to parse command.");
            return false;
        }
    }


    private static readonly List<string> readCommandStack = new();

    public static void ClearReadCommandStack() {
        readCommandStack.Clear();
    }

    // "Read, Path",
    // "Read, Path, StartLine",
    // "Read, Path, StartLine, EndLine"
    [TasCommand("Read", ExecuteTiming = ExecuteTiming.Parse)]
    private static void ReadCommand(string[] args, int studioLine, string currentFilePath, int fileLine) {
        if (args.Length == 0) {
            return;
        }

        string filePath = args[0];
        string fileDirectory = Path.GetDirectoryName(currentFilePath);
        FindTheFile();

        if (!File.Exists(filePath)) {
            // for compatibility with tas files downloaded from discord
            // discord will replace spaces in the file name with underscores
            filePath = args[0].Replace(" ", "_");
            FindTheFile();
        }

        if (!File.Exists(filePath)) {
            ToastAndLog($"\"Read, {string.Join(", ", args)}\" failed\nFile not found");
            Manager.DisableRunLater();
            return;
        } else if (Path.GetFullPath(filePath) == Path.GetFullPath(currentFilePath)) {
            ToastAndLog($"\"Read, {string.Join(", ", args)}\" failed\nDo not allow reading the file itself");
            Manager.DisableRunLater();
            return;
        }

        // Find starting and ending lines
        int startLine = 0;
        int endLine = int.MaxValue;
        if (args.Length > 1) {
            GetLine(args[1], filePath, out startLine);
            if (args.Length > 2) {
                GetLine(args[2], filePath, out endLine);
            }
        }

        string readCommandDetail = $"Read, {string.Join(", ", args)}: line {fileLine} of the file \"{currentFilePath}\"";
        if (readCommandStack.Contains(readCommandDetail)) {
            $"Multiple read commands lead to dead loops:\n{string.Join("\n", readCommandStack)}".Log(LogLevel.Warn);
            Toast.Show("Multiple read commands lead to dead loops\nPlease check log.txt for more details");
            Manager.DisableRunLater();
            return;
        }

        readCommandStack.Add(readCommandDetail);
        Manager.Controller.ReadFile(filePath, startLine, endLine, studioLine);
        if (readCommandStack.Count > 0) {
            readCommandStack.RemoveAt(readCommandStack.Count - 1);
        }

        void ToastAndLog(string text) {
            Toast.Show(text);
            text.Log();
        }

        void FindTheFile() {
            // Check for full and shortened Read versions
            if (fileDirectory != null) {
                // Path.Combine can handle the case when filePath is an absolute path
                string absoluteOrRelativePath = Path.Combine(fileDirectory, filePath);
                if (File.Exists(absoluteOrRelativePath)) {
                    filePath = absoluteOrRelativePath;
                } else {
                    string[] files = Directory.GetFiles(fileDirectory, $"{filePath}*.tas");
                    if (files.FirstOrDefault() is { } shortenedFilePath) {
                        filePath = shortenedFilePath;
                    }
                }
            }
        }
    }

    // "Play, StartLine",
    // "Play, StartLine, FramesToWait"
    [TasCommand("Play", ExecuteTiming = ExecuteTiming.Parse)]
    private static void PlayCommand(string[] args, int studioLine) {
        GetLine(args[0], InputController.TasFilePath, out int startLine);
        if (args.Length > 1 && int.TryParse(args[1], out _)) {
            Manager.Controller.AddFrames(args[1], studioLine);
        }

        if (startLine <= studioLine + 1) {
            "Play command does not allow playback from before the current line".Log(LogLevel.Warn);
            return;
        }

        Manager.Controller.ReadFile(InputController.TasFilePath, startLine, int.MaxValue, startLine - 1);
    }

    private static void GetLine(string labelOrLineNumber, string path, out int lineNumber) {
        if (!int.TryParse(labelOrLineNumber, out lineNumber)) {
            int curLine = 0;
            foreach (string readLine in File.ReadLines(path)) {
                curLine++;
                string line = readLine.Trim();
                if (line == $"#{labelOrLineNumber}") {
                    lineNumber = curLine;
                    return;
                }
            }

            lineNumber = int.MaxValue;
        }
    }

    [TasCommand("EnforceLegal", AliasNames = new[] {"EnforceMainGame"})]
    private static void EnforceLegalCommand() {
        Manager.EnforceLegal = true;
    }

    [TasCommand("Safe")]
    private static void SafeCommand() {
        Manager.AllowUnsafeInput = false;
    }

    [TasCommand("Unsafe")]
    private static void UnsafeCommand() {
        Manager.AllowUnsafeInput = true;
    }

    // Gun, x, y
    [TasCommand("Gun", LegalInMainGame = false)]
    private static void GunCommand(string[] args) {
        if (args.Length < 2) {
            return;
        }

        if (float.TryParse(args[0], out float x)
            && float.TryParse(args[1], out float y)
            && Engine.Scene.Tracker.GetEntity<Player>() is { } player
            && GunInputCursorPosition.Value != null
            && GunlineGunshot.Value != null
           ) {
            Vector2 pos = new(x, y);
            GunInputCursorPosition.Value.SetValue(null, pos);
            GunlineGunshot.Value.Invoke(null, new object[] {player, pos, Facings.Left});
        }
    }
}