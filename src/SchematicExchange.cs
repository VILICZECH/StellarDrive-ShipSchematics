using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace ShipSchematics
{
    public sealed class ExchangeReport
    {
        public string Message;
        public bool LibraryChanged;
        public string SelectId;
    }

    // Import and export of .schematic files through the Windows file window.
    public static class SchematicExchange
    {
        const int Explorer = 0x80000;
        const int FileMustExist = 0x1000;
        const int PathMustExist = 0x800;
        const int NoChangeDir = 0x8;
        const int AllowMultiSelect = 0x200;
        const int OverwritePrompt = 0x2;
        const int HideReadOnly = 0x4;
        const int OpenBufferChars = 65536;
        const int SaveBufferChars = 1024;
        const long MaxFileBytes = 64L * 1024 * 1024;

        static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        static string _dialogError;

        public static string ShareFolder
        {
            get
            {
                string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string path = Path.Combine(documents, "StellarDrive Schematics");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        public static ExchangeReport PickAndImport()
        {
            string[] paths = OpenFiles();
            if (paths == null || paths.Length == 0)
                return _dialogError == null ? null : new ExchangeReport { Message = _dialogError };

            int added = 0;
            int already = 0;
            int pictures = 0;
            int failed = 0;
            int missing = 0;
            string selectId = null;
            string lastMessage = null;
            foreach (string path in paths)
            {
                ImportOutcome outcome = SchematicLibrary.ImportPackage(ReadPackage(path));
                lastMessage = outcome.Message;
                if (outcome.MissingParts)
                    missing++;
                if (outcome.Kind == ImportKind.Added)
                {
                    added++;
                    selectId = outcome.File.id;
                }
                else if (outcome.Kind == ImportKind.Picture)
                {
                    pictures++;
                    if (selectId == null)
                        selectId = outcome.File.id;
                }
                else if (outcome.Kind == ImportKind.Already)
                {
                    already++;
                }
                else
                {
                    failed++;
                }
            }

            return new ExchangeReport
            {
                LibraryChanged = added > 0 || pictures > 0,
                SelectId = selectId,
                Message = paths.Length == 1 ? lastMessage : Describe(added, already, pictures, failed, missing)
            };
        }

        public static string PickAndExport(SchematicFile file)
        {
            if (file == null)
                return "Select a ship to export.";
            string path = SaveFile(SafeFileName(file.name) + ".schematic");
            if (string.IsNullOrEmpty(path))
                return _dialogError;
            try
            {
                SchematicPackage package = SchematicLibrary.ToPackage(file);
                File.WriteAllText(path, JsonUtility.ToJson(package, true));
                return "Exported " + file.name + ".";
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not export " + file.name + ". " + ex.Message);
                return "Could not export that ship.";
            }
        }

        static SchematicPackage ReadPackage(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    MelonLogger.Warning(path + " is too large for a ship schematic.");
                    return null;
                }
                return JsonUtility.FromJson<SchematicPackage>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("Could not read " + path + ". " + ex.Message);
                return null;
            }
        }

        static string Describe(int added, int already, int pictures, int failed, int missing)
        {
            if (added == 0 && already == 0 && pictures == 0)
                return "None of those files is a ship schematic.";
            var parts = new List<string>();
            if (added > 0)
                parts.Add("Imported " + CountShips(added) + ".");
            if (already + pictures > 0)
                parts.Add(CountShips(already + pictures) + " already in the library.");
            if (pictures > 0)
                parts.Add("Added " + pictures + (pictures == 1 ? " picture." : " pictures."));
            if (missing > 0)
                parts.Add(CountShips(missing) + " use parts that are not installed.");
            if (failed > 0)
                parts.Add(failed + (failed == 1 ? " file" : " files") + " could not be read.");
            return string.Join(" ", parts.ToArray());
        }

        static string CountShips(int count)
        {
            return count + (count == 1 ? " ship" : " ships");
        }

        static string SafeFileName(string name)
        {
            name = (name ?? "").Trim();
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            name = name.Trim().TrimEnd('.');
            if (name.Length == 0)
                name = "Ship";
            if (name.Length > 80)
                name = name.Substring(0, 80);
            if (ReservedNames.Contains(name.Split('.')[0].Trim()))
                name = "_" + name;
            return name;
        }

        static string[] OpenFiles()
        {
            return WithDialog(() => RunDialog(false, "Import ship", null, OpenBufferChars));
        }

        static string SaveFile(string suggestedName)
        {
            string[] paths = WithDialog(() => RunDialog(true, "Export ship", suggestedName, SaveBufferChars));
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                return null;
            string path = paths[0];
            if (!path.EndsWith(".schematic", StringComparison.OrdinalIgnoreCase))
                path += ".schematic";
            return path;
        }

        // The file window is modal on the game's main thread: the game waits until it closes.
        static T WithDialog<T>(Func<T> show)
        {
            _dialogError = null;
            string cwd = Directory.GetCurrentDirectory();
            CursorLockMode locked = Cursor.lockState;
            bool visible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            try
            {
                return show();
            }
            finally
            {
                Directory.SetCurrentDirectory(cwd);
                Cursor.lockState = locked;
                Cursor.visible = visible;
            }
        }

        static string[] RunDialog(bool save, string title, string suggestedName, int chars)
        {
            IntPtr file = Marshal.AllocHGlobal(chars * 2);
            IntPtr filter = NativeFilter("Ship schematics\0*.schematic\0\0");
            IntPtr titleText = NativeText(title);
            IntPtr directory = NativeText(ShareFolder);
            IntPtr extension = NativeText("schematic");
            try
            {
                Marshal.Copy(new byte[chars * 2], 0, file, chars * 2);
                if (!string.IsNullOrEmpty(suggestedName))
                {
                    byte[] name = Encoding.Unicode.GetBytes(suggestedName + "\0");
                    Marshal.Copy(name, 0, file, Math.Min(name.Length, chars * 2 - 2));
                }
                var dialog = new OpenFileName
                {
                    structSize = Marshal.SizeOf(typeof(OpenFileName)),
                    owner = GetActiveWindow(),
                    filter = filter,
                    file = file,
                    maxFile = chars,
                    initialDir = directory,
                    title = titleText,
                    defExt = extension,
                    flags = save
                        ? Explorer | PathMustExist | NoChangeDir | OverwritePrompt | HideReadOnly
                        : Explorer | FileMustExist | PathMustExist | NoChangeDir | AllowMultiSelect | HideReadOnly
                };
                bool picked = save ? GetSaveFileName(ref dialog) : GetOpenFileName(ref dialog);
                if (!picked)
                {
                    NoteDialogError();
                    return null;
                }
                return save ? new[] { Marshal.PtrToStringUni(file) } : SplitFiles(file, chars);
            }
            finally
            {
                Free(file);
                Free(filter);
                Free(titleText);
                Free(directory);
                Free(extension);
            }
        }

        // One file comes back as its path. Several come back as the folder followed by the file names, each ended
        // by a null character and the list by two.
        static string[] SplitFiles(IntPtr buffer, int maxChars)
        {
            var chars = new char[maxChars];
            Marshal.Copy(buffer, chars, 0, maxChars);
            var parts = new List<string>();
            int start = 0;
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] != '\0')
                    continue;
                if (i == start)
                    break;
                parts.Add(new string(chars, start, i - start));
                start = i + 1;
            }
            if (parts.Count == 0)
                return null;
            if (parts.Count == 1)
                return new[] { parts[0] };
            var files = new string[parts.Count - 1];
            for (int i = 1; i < parts.Count; i++)
                files[i - 1] = Path.Combine(parts[0], parts[i]);
            return files;
        }

        static void NoteDialogError()
        {
            int error = CommDlgExtendedError();
            if (error == 0)
                return;
            MelonLogger.Warning("The file window failed (" + error + ").");
            _dialogError = "The file window failed.";
        }

        static IntPtr NativeText(string value)
        {
            return Marshal.StringToHGlobalUni(value ?? "");
        }

        static IntPtr NativeFilter(string filter)
        {
            byte[] data = Encoding.Unicode.GetBytes(filter);
            IntPtr buffer = Marshal.AllocHGlobal(data.Length + 2);
            Marshal.Copy(data, 0, buffer, data.Length);
            Marshal.WriteInt16(buffer, data.Length, 0);
            return buffer;
        }

        static void Free(IntPtr pointer)
        {
            if (pointer != IntPtr.Zero)
                Marshal.FreeHGlobal(pointer);
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW")]
        static extern bool GetOpenFileName(ref OpenFileName dialog);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetSaveFileNameW")]
        static extern bool GetSaveFileName(ref OpenFileName dialog);

        [DllImport("comdlg32.dll", EntryPoint = "CommDlgExtendedError")]
        static extern int CommDlgExtendedError();

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileName
        {
            public int structSize;
            public IntPtr owner;
            public IntPtr instance;
            public IntPtr filter;
            public IntPtr customFilter;
            public int maxCustomFilter;
            public int filterIndex;
            public IntPtr file;
            public int maxFile;
            public IntPtr fileTitle;
            public int maxFileTitle;
            public IntPtr initialDir;
            public IntPtr title;
            public int flags;
            public short fileOffset;
            public short fileExtension;
            public IntPtr defExt;
            public IntPtr customData;
            public IntPtr hook;
            public IntPtr templateName;
            public IntPtr reservedPtr;
            public int reservedInt;
            public int flagsEx;
        }
    }
}
