// Song Stone Loader - puts your own music into the Ayleid Song Stone (Skyrim SE mod by MarcsMods).
//
// Drop songs, folders or .zip files on the window (or on the .exe). A folder or zip becomes a playlist named after it;
// single songs go into the playlist picked in the box. Songs are COPIED into the music folder (your originals are left alone), then
// every new song is converted to Skyrim's .xwm format (volume evened out), given a song slot
// (Data\Sound\FX\SongStone\S021.xwm ... S300.xwm) and written to the song list the game reads
// (Data\SKSE\Plugins\StorageUtilData\SongStone\Music.json: playlists, titles = file names, song lengths).
// Songs converted before are kept as they are, so adding a few later is quick.
// One of each song: TIDY MY MUSIC (and a pop-up when an added folder brings several versions of a song) keeps one
// version and moves the rest to a Spare versions folder; a song the mod's starter pack already has plays from there.
//
// Converting uses the free Creation Kit's own tools (Steam > Library > Tools > Skyrim Special Edition: Creation Kit):
// Tools\LipGen\LipGenerator\ffmpeg.exe and Tools\Audio\xWMAEncode.exe. Copies next to this .exe work too.
//
// Build: C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:SongStone.ico
//        /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:WPF\PresentationCore.dll /r:WPF\WindowsBase.dll /r:System.Xaml.dll
//        /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:Microsoft.VisualBasic.dll (Recycle Bin)
//        /out:"Song Stone Loader.exe" SongStoneLoader.cs   (WPF = the Framework folder's WPF subfolder; used for song previews)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Song Stone Loader")]
[assembly: AssemblyDescription("Adds your own music to the Ayleid Song Stone (Skyrim Special Edition mod)")]
[assembly: AssemblyCompany("MarcsMods")]
[assembly: AssemblyProduct("Ayleid Song Stone")]
[assembly: AssemblyCopyright("MarcsMods 2026")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace SongStone
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LoaderForm(args));
        }
    }

    // ------------------------------------------------------------------------------------------- settings
    static class Settings
    {
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ayleid Song Stone"); } }
        static string File_ { get { return Path.Combine(Folder, "settings.txt"); } }

        public static string Get(string key)
        {
            try
            {
                if (!File.Exists(File_)) return null;
                foreach (var l in File.ReadAllLines(File_))
                {
                    int i = l.IndexOf('=');
                    if (i > 0 && l.Substring(0, i) == key) return l.Substring(i + 1);
                }
            }
            catch { }
            return null;
        }

        public static void Set(string key, string value)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var lines = File.Exists(File_) ? File.ReadAllLines(File_).Where(l => !l.StartsWith(key + "=")).ToList() : new List<string>();
                lines.Add(key + "=" + value);
                File.WriteAllLines(File_, lines);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------------------------------- tags
    // A song can carry several tags (Calm, Riding, Fight...); every tag becomes a playlist in the stone, next to the
    // folder playlists. Saved with the music as 'Skyrim Ayleid Song Stone tags.txt' (named so nobody takes it for junk -
    // user 2026-10-13; older names are renamed on the first start): a note line, a '#tags=' line with the tag order, then
    // 'path inside the music folder<TAB>Tag|Tag<TAB>fingerprint' per song.
    static class Tags
    {
        public static readonly string[] Presets = { "Calm", "Tavern", "Town", "Home", "Riding", "Wilderness", "Dungeon", "Fight" };
        static readonly object gate = new object();
        static List<string> all = new List<string>(Presets);
        static Dictionary<string, List<string>> ofSong = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> hashOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // song fingerprint per tagged file
        static bool loaded;
        const string FileName = "Skyrim Ayleid Song Stone tags.txt";
        const string BackupName = "Skyrim Ayleid Song Stone tags backup";
        const string Note = "# Skyrim mod Ayleid Song Stone - your song tags. Please don't delete or edit this file: Song Stone Loader keeps it (a backup is in Documents\\Ayleid Song Stone).";
        static string FileIn(string musicDir) { return Path.Combine(musicDir, FileName); }

        // Tags take days to set, so they're kept safe twice over (user 2026-10-13: "music is easy to replace but the tags,
        // that's the one"): every save also writes a copy to Documents\Ayleid Song Stone (often backed up by OneDrive) plus
        // one dated copy per day (the last 7 kept); a music folder that lost its tags file gets it back from there; and each
        // tagged song's fingerprint is kept with its tags, so songs moved to other folders or downloaded again find their
        // tags again (Rehome).
        static string backupOverride;   // tests only
        public static string BackupDir { get { return backupOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ayleid Song Stone"); } }
        static string BackupFile { get { return Path.Combine(BackupDir, BackupName + ".txt"); } }
        public static bool Restored;   // this start put the tags file back from the backup

        // earlier loaders (all 2026-10-13) called them tags.txt, then 'Ayleid Song Stone tags.txt'; the backups
        // 'Tags backup*.txt', then 'Ayleid Song Stone tags backup*.txt'
        static void OldNames(string musicDir)
        {
            try
            {
                foreach (var oldName in new[] { "Ayleid Song Stone tags.txt", "tags.txt" })
                {
                    string old = Path.Combine(musicDir, oldName);
                    if (!File.Exists(FileIn(musicDir)) && File.Exists(old)) File.Move(old, FileIn(musicDir));
                }
                if (!Directory.Exists(BackupDir)) return;
                foreach (var oldName in new[] { "Ayleid Song Stone tags backup", "Tags backup" })
                    foreach (var f in Directory.GetFiles(BackupDir, oldName + "*.txt"))
                    {
                        string to = Path.Combine(BackupDir, BackupName + Path.GetFileName(f).Substring(oldName.Length));
                        if (!File.Exists(to)) File.Move(f, to);
                    }
            }
            catch { }
        }

        public static void Load(string musicDir)
        {
            lock (gate)
            {
                loaded = true;
                all = new List<string>(Presets);
                ofSong = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                hashOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    OldNames(musicDir);
                    if (!File.Exists(FileIn(musicDir)) && File.Exists(BackupFile))
                    {
                        Directory.CreateDirectory(musicDir);
                        File.Copy(BackupFile, FileIn(musicDir));
                        Restored = true;
                    }
                    if (!File.Exists(FileIn(musicDir))) return;
                    foreach (var l in File.ReadAllLines(FileIn(musicDir)))
                    {
                        if (l.StartsWith("#tags="))
                        {
                            foreach (var t in l.Substring(6).Split('|').Select(x => x.Trim()))
                                if (t != "" && !all.Contains(t, StringComparer.OrdinalIgnoreCase)) all.Add(t);
                            continue;
                        }
                        var c = l.Split('\t');   // file <TAB> tags [<TAB> fingerprint]
                        if (c.Length < 2 || c[0] == "") continue;
                        var tags = c[1].Split('|').Select(x => x.Trim()).Where(x => x != "").ToList();
                        if (tags.Count == 0) continue;
                        ofSong[c[0]] = tags;
                        if (c.Length > 2 && c[2].Trim() != "") hashOf[c[0]] = c[2].Trim();
                    }
                }
                catch { }
            }
        }

        public static void EnsureLoaded(string musicDir) { bool l; lock (gate) l = loaded; if (!l) Load(musicDir); }

        public static void Save(string musicDir)
        {
            lock (gate)
            {
                try
                {
                    var lines = new List<string> { Note, "#tags=" + string.Join("|", all) };   // the note has no TAB: skipped when read
                    lines.AddRange(ofSong.Where(kv => kv.Value.Count > 0).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(kv => kv.Key + "\t" + string.Join("|", kv.Value) + "\t" + HashFor(musicDir, kv.Key)));
                    File.WriteAllLines(FileIn(musicDir), lines);
                    Backup(lines);
                }
                catch { }
            }
        }

        static string HashFor(string musicDir, string rel)   // the song's fingerprint now, else the one kept from before
        {
            string full = Path.Combine(musicDir, rel), h = null;
            try { if (File.Exists(full)) h = Engine.HashOf(full, false); } catch { }
            if (h != null) hashOf[rel] = h;
            return hashOf.TryGetValue(rel, out h) ? h : "";
        }

        static void Backup(List<string> lines)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);
                File.WriteAllLines(BackupFile, lines);
                File.WriteAllLines(Path.Combine(BackupDir, BackupName + " " + DateTime.Now.ToString("yyyy-MM-dd") + ".txt"), lines);
                foreach (var old in Directory.GetFiles(BackupDir, BackupName + " ????-??-??.txt").OrderByDescending(x => x, StringComparer.Ordinal).Skip(7)) File.Delete(old);
            }
            catch { }
        }

        // Tagged songs whose file is gone (moved to another playlist, or the music downloaded again) find their tags
        // again: by fingerprint (the same file anywhere in the music), else by file name when only one file has it.
        // Tags nothing matches are kept, in case the song comes back later. Returns how many songs got their tags back.
        public static int Rehome(string musicDir, List<KeyValuePair<string, string>> fileHash)   // (file, fingerprint)
        {
            lock (gate)
            {
                var rels = fileHash.Select(kv => new KeyValuePair<string, string>(Rel(musicDir, kv.Key), kv.Value)).ToList();
                var present = new HashSet<string>(rels.Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
                int moved = 0;
                foreach (var rel in ofSong.Keys.Where(r => !present.Contains(r)).ToList())
                {
                    string h;
                    hashOf.TryGetValue(rel, out h);
                    var byHash = h == null ? new List<string>() : rels.Where(kv => kv.Value == h).Select(kv => kv.Key).ToList();
                    var byName = rels.Where(kv => string.Equals(Path.GetFileName(kv.Key), Path.GetFileName(rel), StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
                    string to = byHash.Count > 0 ? byHash[0] : byName.Count == 1 ? byName[0] : null;
                    if (to == null) continue;
                    List<string> t;
                    if (!ofSong.TryGetValue(to, out t)) { t = new List<string>(); ofSong[to] = t; }
                    foreach (var x in ofSong[rel]) if (!t.Contains(x, StringComparer.OrdinalIgnoreCase)) t.Add(x);
                    ofSong.Remove(rel);
                    hashOf.Remove(rel);
                    if (h != null) hashOf[to] = h;
                    moved++;
                }
                return moved;
            }
        }

        public static List<string> All { get { lock (gate) return new List<string>(all); } }
        public static void AddTag(string tag) { lock (gate) if (!all.Contains(tag, StringComparer.OrdinalIgnoreCase)) all.Add(tag); }

        public static List<string> Of(string rel)
        {
            lock (gate) { List<string> t; return ofSong.TryGetValue(rel, out t) ? new List<string>(t) : new List<string>(); }
        }

        public static void Set(string rel, string tag, bool on)
        {
            lock (gate)
            {
                List<string> t;
                if (!ofSong.TryGetValue(rel, out t)) { t = new List<string>(); ofSong[rel] = t; }
                t.RemoveAll(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
                if (on) t.Add(tag);
            }
        }

        public static void Forget(string rel) { lock (gate) { ofSong.Remove(rel); hashOf.Remove(rel); } }   // a removed song's tags

        public static string Rel(string musicDir, string file)
        {
            string root = musicDir.TrimEnd('\\') + "\\";
            return file.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? file.Substring(root.Length) : file;
        }
    }

    // ------------------------------------------------------------------------------------------- the converter
    class Engine
    {
        public static readonly string[] Formats = { ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma", ".xwm" };
        public const string Loose = "My Songs";
        const int FirstSlot = 21, LastSlot = 300, Bitrate = 96000;
        // every song is evened out to this loudness (LUFS). 2026-10-12: -18 -> -14, the stone was too quiet; songs converted
        // at another level are converted again (Music.loader.loudness remembers the level they were made at)
        const string Loudness = "-14";
        public const double MinSecs = 40;   // shorter = a jingle, loop or video-editing cut, not a jukebox song (MacLeod's packs have lots)

        // SHA1 per song file, remembered by path + size + date, so a rescan doesn't re-read every song
        static readonly object hashGate = new object();
        static Dictionary<string, string[]> hashes;
        static string HashFile { get { return Path.Combine(Settings.Folder, "hashes.txt"); } }

        public static string HashOf(string file, bool compute)
        {
            var fi = new FileInfo(file);
            string size = fi.Length.ToString(), date = fi.LastWriteTimeUtc.Ticks.ToString();
            lock (hashGate)
            {
                if (hashes == null)
                {
                    hashes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    try { if (File.Exists(HashFile)) foreach (var l in File.ReadAllLines(HashFile)) { var c = l.Split('\t'); if (c.Length == 4) hashes[c[0]] = c; } } catch { }
                }
                string[] e;
                if (hashes.TryGetValue(file, out e) && e[1] == size && e[2] == date) return e[3];
            }
            if (!compute) return null;
            string h = Sha1(file);
            lock (hashGate) hashes[file] = new[] { file, size, date, h };
            return h;
        }

        public static void SaveHashes()
        {
            lock (hashGate)
            {
                if (hashes == null) return;
                try { Directory.CreateDirectory(Settings.Folder); File.WriteAllLines(HashFile, hashes.Values.Where(v => File.Exists(v[0])).Select(v => string.Join("\t", v))); } catch { }
            }
        }

        // song lengths the converter measured, by song hash (for the songs view)
        public static Dictionary<string, double> Lengths(string skyrim)
        {
            var d = new Dictionary<string, double>();
            try
            {
                string f = Path.Combine(skyrim, @"Data\SKSE\Plugins\StorageUtilData\SongStone\Music.loader.csv");
                if (File.Exists(f))
                    foreach (var l in File.ReadAllLines(f))
                    {
                        var c = l.Split(',');
                        double s;
                        if (c.Length >= 3 && double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s)) d[c[0]] = s;
                    }
            }
            catch { }
            return d;
        }

        public Action<string> Log = delegate { };
        public Action<string> Progress = delegate { };   // the big status line (not the log)
        public volatile bool Cancel;
        Process current;

        // Mac zips carry '._name.mp3' junk copies and a __MACOSX folder: not music
        public static bool IsMusic(string path) { return Formats.Contains(Path.GetExtension(path).ToLowerInvariant()) && !Path.GetFileName(path).StartsWith("._") && path.IndexOf("__MACOSX", StringComparison.OrdinalIgnoreCase) < 0; }

        public static string FindSkyrim()
        {
            string saved = Settings.Get("skyrim");
            if (saved != null && File.Exists(Path.Combine(saved, "SkyrimSE.exe"))) return saved;
            foreach (var k in new[] { @"SOFTWARE\WOW6432Node\Bethesda Softworks\Skyrim Special Edition", @"SOFTWARE\Bethesda Softworks\Skyrim Special Edition" })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(k))
                    {
                        var p = key == null ? null : key.GetValue("installed path") as string;
                        if (p != null && File.Exists(Path.Combine(p, "SkyrimSE.exe"))) return p.TrimEnd('\\');
                    }
                }
                catch { }
            }
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var steam = key == null ? null : key.GetValue("SteamPath") as string;
                    if (steam != null)
                    {
                        var libs = new List<string> { steam };
                        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                        if (File.Exists(vdf))
                            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                        foreach (var l in libs)
                        {
                            var p = Path.Combine(l, @"steamapps\common\Skyrim Special Edition");
                            if (File.Exists(Path.Combine(p, "SkyrimSE.exe"))) return p;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        static string FindTool(string name, string skyrim)
        {
            var places = new List<string> { AppDomain.CurrentDomain.BaseDirectory };
            if (skyrim != null) { places.Add(Path.Combine(skyrim, @"Tools\LipGen\LipGenerator")); places.Add(Path.Combine(skyrim, @"Tools\Audio")); }
            foreach (var p in places) { var f = Path.Combine(p, name); if (File.Exists(f)) return f; }
            return null;
        }

        // An album folder (every song in it starts with a track number: "02 Concerning Hobbits", "1-02 ...", "02. ...",
        // "02 - ...") loses the numbers from its titles; a lone "99 Luftballons" among other names keeps its name.
        static readonly Regex TrackNo = new Regex(@"^\s*(\d{1,2}-)?\d{1,3}(\s*[-.)_]\s*|\s+)(?=\S)");
        static readonly Dictionary<string, KeyValuePair<DateTime, bool>> albumDirs = new Dictionary<string, KeyValuePair<DateTime, bool>>(StringComparer.OrdinalIgnoreCase);
        public static string SongTitle(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (IsAlbum(Path.GetDirectoryName(file) ?? ""))
            {
                string t = TrackNo.Replace(name, "");
                if (t.Trim() != "") name = t;
            }
            return CleanTitle(name);
        }
        static bool IsAlbum(string dir)
        {
            if (!Directory.Exists(dir)) return false;
            DateTime stamp = Directory.GetLastWriteTimeUtc(dir);   // changes when songs are added or removed
            lock (albumDirs)
            {
                KeyValuePair<DateTime, bool> c;
                if (albumDirs.TryGetValue(dir, out c) && c.Key == stamp) return c.Value;
                var names = Directory.GetFiles(dir).Where(IsMusic).Select(Path.GetFileNameWithoutExtension).ToList();
                bool album = names.Count >= 2 && names.All(n => TrackNo.IsMatch(n));
                albumDirs[dir] = new KeyValuePair<DateTime, bool>(stamp, album);
                return album;
            }
        }

        public static string CleanTitle(string name)
        {
            var sb = new StringBuilder();
            foreach (var ch in name.Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && ch >= 0x20 && ch <= 0x7E) sb.Append(ch == '"' ? '\'' : ch);
            return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        }

        static string Sha1(string file)
        {
            using (var s = File.OpenRead(file))
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
        }

        // a slot file Skyrim has open can't be replaced or deleted
        static bool Busy(string f)
        {
            if (!File.Exists(f)) return false;
            try { using (File.Open(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return false; } catch { return true; }
        }

        static string Json(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        // the strings / numbers of one "key": [ ... ] list in the song list files the converters write
        public static List<string> JsonStrings(string json, string key)
        {
            string inner = Regex.Match(json, "\"" + key + "\": \\[(.*?)\\]").Groups[1].Value;
            return Regex.Matches(inner, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<Match>().Select(m => m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")).ToList();
        }
        static List<double> JsonNumbers(string json, string key)
        {
            var r = new List<double>();
            foreach (var x in Regex.Match(json, "\"" + key + "\": \\[(.*?)\\]").Groups[1].Value.Split(','))
            { double d; if (double.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.Add(d); }
            return r;
        }

        class Song { public string Title; public string File; public int Slot; public double Secs; }

        // the mod's starter pack (Starter.json, slots 1-20)
        static List<Song> StarterSongs(string jsonDir)
        {
            var list = new List<Song>();
            try
            {
                string json = File.ReadAllText(Path.Combine(jsonDir, "Starter.json"));
                var titles = JsonStrings(json, "titles"); var slots = JsonNumbers(json, "slots"); var secs = JsonNumbers(json, "secs");
                for (int i = 0; i < titles.Count && i < slots.Count && i < secs.Count; i++) list.Add(new Song { Title = titles[i], Slot = (int)slots[i], Secs = secs[i] });
            }
            catch { }
            return list;
        }

        // a song of yours that IS a starter song (same name, same length to a split second - e.g. the same Kevin
        // MacLeod track) plays from the starter's slot, so it isn't in the stone twice
        static int StarterSlot(List<Song> starter, string title, double secs)
        {
            string key = Versions.Key(title);
            foreach (var s in starter) if (Versions.Key(s.Title) == key && Math.Abs(s.Secs - secs) < 0.6) return s.Slot;
            return 0;
        }
        class Playlist { public string Name; public List<string> Files = new List<string>(); public List<string> Hashes = new List<string>(); }

        // Returns the summary line, or null when it stopped early.
        public string Run(string musicDir, string skyrim)
        {
            string data = Path.Combine(skyrim, "Data");
            string ffmpeg = FindTool("ffmpeg.exe", skyrim), xwma = FindTool("xWMAEncode.exe", skyrim);
            if (ffmpeg == null || xwma == null)
            {
                Log("The music converters weren't found.");
                Log("Install the free Creation Kit: Steam > Library > Tools > 'Skyrim Special Edition: Creation Kit', then try again.");
                Log("(Or put ffmpeg.exe and xWMAEncode.exe next to Song Stone Loader.exe.)");
                return null;
            }
            string soundDir = Path.Combine(data, @"Sound\FX\SongStone");
            string jsonDir = Path.Combine(data, @"SKSE\Plugins\StorageUtilData\SongStone");
            Directory.CreateDirectory(soundDir);
            Directory.CreateDirectory(jsonDir);
            string cacheFile = Path.Combine(jsonDir, "Music.loader.csv");
            string loudFile = Path.Combine(jsonDir, "Music.loader.loudness");
            bool relevel = File.Exists(cacheFile) && (!File.Exists(loudFile) || File.ReadAllText(loudFile).Trim() != Loudness);

            // what's in the music folder: every folder is a playlist, loose songs go to "My Songs"
            var playlists = new List<Playlist>();
            foreach (var d in Directory.GetDirectories(musicDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var pl = new Playlist { Name = CleanTitle(Path.GetFileName(d)) };
                pl.Files.AddRange(Directory.GetFiles(d, "*", SearchOption.AllDirectories).Where(IsMusic).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                if (pl.Files.Count > 0 && pl.Name != "") playlists.Add(pl);
            }
            var loose = Directory.GetFiles(musicDir).Where(IsMusic).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (loose.Count > 0) { var existing = playlists.FirstOrDefault(p => p.Name == Loose); if (existing == null) { existing = new Playlist { Name = Loose }; playlists.Add(existing); } existing.Files.AddRange(loose); }
            if (playlists.Count == 0) { Log("No songs in the music folder yet - drop some on the window."); return null; }

            // songs converted earlier (their slot file must still be there)
            var cache = new Dictionary<string, Song>();
            if (File.Exists(cacheFile))
                foreach (var l in File.ReadAllLines(cacheFile))
                {
                    var c = l.Split(',');
                    int slot; double secs;
                    if (c.Length >= 3 && int.TryParse(c[1], out slot) && double.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out secs)
                        && (slot == 0 || File.Exists(Path.Combine(soundDir, string.Format("S{0:D3}.xwm", slot)))))
                        cache[c[0]] = new Song { Slot = slot, Secs = secs };
                }

            // one slot per distinct song (the same file in two playlists is one song)
            var songs = new Dictionary<string, Song>();
            var order = new List<string>();
            var fileHash = new List<KeyValuePair<string, string>>();
            int total = playlists.Sum(p => p.Files.Count), seen = 0;
            foreach (var pl in playlists)
                foreach (var f in pl.Files)
                {
                    if (Cancel) return null;
                    if (++seen % 4 == 1 || seen == total) Progress(string.Format("Checking songs... {0} of {1}", seen, total));
                    string h = HashOf(f, true);
                    fileHash.Add(new KeyValuePair<string, string>(f, h));
                    if (!songs.ContainsKey(h)) { songs[h] = new Song { Title = SongTitle(f), File = f }; order.Add(h); }
                    if (!pl.Hashes.Contains(h)) pl.Hashes.Add(h);
                }
            SaveHashes();

            // tags: every tag is a playlist too (a song can have several; a folder with the same name merges with it)
            Tags.EnsureLoaded(musicDir);
            int rehomed = Tags.Rehome(musicDir, fileHash);
            if (rehomed > 0)
            {
                Tags.Save(musicDir);
                Log(string.Format("Tags found their songs again: {0} song{1} moved or downloaded again.", rehomed, rehomed == 1 ? "" : "s"));
            }
            foreach (var tag in Tags.All)
            {
                var hs = new List<string>();
                foreach (var kv in fileHash)
                    if (Tags.Of(Tags.Rel(musicDir, kv.Key)).Contains(tag, StringComparer.OrdinalIgnoreCase) && !hs.Contains(kv.Value)) hs.Add(kv.Value);
                if (hs.Count == 0) continue;
                var same = playlists.FirstOrDefault(p => string.Equals(p.Name, tag, StringComparison.OrdinalIgnoreCase));
                if (same == null) playlists.Add(new Playlist { Name = tag, Hashes = hs });
                else foreach (var h in hs) if (!same.Hashes.Contains(h)) same.Hashes.Add(h);
            }
            var used = new HashSet<int>();
            var shortClips = new HashSet<string>();
            var starter = StarterSongs(jsonDir);
            var inStarter = new HashSet<string>();   // songs that play from a starter slot
            foreach (var h in order)
            {
                if (!cache.ContainsKey(h)) continue;
                if (cache[h].Slot == 0 || cache[h].Secs < MinSecs) { shortClips.Add(h); songs[h].Secs = cache[h].Secs; continue; }
                int st = StarterSlot(starter, songs[h].Title, cache[h].Secs);
                if (st > 0) { songs[h].Slot = st; songs[h].Secs = cache[h].Secs; inStarter.Add(h); continue; }
                if (cache[h].Slot < FirstSlot) { cache.Remove(h); continue; }   // the starter pack changed: convert it after all
                songs[h].Slot = cache[h].Slot; songs[h].Secs = cache[h].Secs; used.Add(cache[h].Slot);
            }
            int next = FirstSlot;
            var dropped = new List<string>();
            foreach (var h in order)
            {
                if (songs[h].Slot != 0 || shortClips.Contains(h)) continue;
                while (next <= LastSlot && (used.Contains(next) || Busy(Path.Combine(soundDir, string.Format("S{0:D3}.xwm", next))))) next++;
                if (next > LastSlot) { dropped.Add(Path.GetFileName(songs[h].File)); continue; }
                songs[h].Slot = next; used.Add(next);
            }

            // convert the new ones
            string tmp = Path.Combine(Path.GetTempPath(), "SongStoneLoader.wav");
            var todo = order.Where(h => songs[h].Slot != 0 && (!cache.ContainsKey(h) || (relevel && songs[h].Slot >= FirstSlot && !inStarter.Contains(h)))).ToList();
            if (relevel && todo.Count > 0) Log("New loudness level: converting your songs again so they all play louder.");
            var failed = new List<string>();
            int done = 0, made = 0;
            foreach (var h in todo)
            {
                if (Cancel) return null;
                var s = songs[h];
                Log(string.Format("[{0}/{1}] {2}", ++done, todo.Count, Path.GetFileName(s.File)));
                string outFile = Path.Combine(soundDir, string.Format("S{0:D3}.xwm", s.Slot));
                try { File.Delete(tmp); } catch { }
                string err = RunTool(ffmpeg, string.Format("-hide_banner -nostdin -y -i \"{0}\" -vn -af loudnorm=I=" + Loudness + ":TP=-1.5:LRA=11 -ar 44100 -ac 2 -c:a pcm_s16le \"{1}\"", s.File, tmp));
                var m = Regex.Match(err ?? "", @"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)");
                if (!File.Exists(tmp) || !m.Success) { failed.Add(Path.GetFileName(s.File) + " (couldn't read it)"); s.Slot = 0; continue; }
                s.Secs = Math.Round(int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), 2);
                if (s.Secs < MinSecs) { shortClips.Add(h); s.Slot = 0; continue; }   // its slot is freed for the next song
                int st = StarterSlot(starter, s.Title, s.Secs);
                if (st > 0) { s.Slot = st; inStarter.Add(h); continue; }
                try { File.Delete(outFile); } catch { }   // a song converted again: the old file must not pass for the new one
                RunTool(xwma, string.Format("-b {0} \"{1}\" \"{2}\"", Bitrate, tmp, outFile));
                if (!File.Exists(outFile) || new FileInfo(outFile).Length < 1000) { failed.Add(Path.GetFileName(s.File) + " (converting failed)"); s.Slot = 0; continue; }
                made++;
            }
            try { File.Delete(tmp); } catch { }
            if (Cancel) return null;

            // remove slot files no song uses any more
            var keep = new HashSet<int>(order.Where(h => songs[h].Slot != 0).Select(h => songs[h].Slot));
            int removed = 0, busy = 0;
            for (int n = FirstSlot; n <= LastSlot; n++)
            {
                string f = Path.Combine(soundDir, string.Format("S{0:D3}.xwm", n));
                if (keep.Contains(n) || !File.Exists(f)) continue;
                try { File.Delete(f); removed++; }
                catch { busy++; }   // Skyrim is running and has it open: unused anyway, cleared on a later run
            }

            // the song list for the game (PapyrusUtil JSON)
            var final = order.Where(h => songs[h].Slot != 0 && !inStarter.Contains(h)).ToList();
            var plNames = new List<string>();
            var plLists = new List<string>();
            foreach (var pl in playlists)
            {
                var slots = pl.Hashes.Where(h => songs[h].Slot != 0).Select(h => songs[h].Slot.ToString()).Distinct().ToList();
                if (slots.Count == 0) continue;
                plLists.Add(string.Format("\"pl{0}\": [{1}]", plNames.Count, string.Join(", ", slots)));
                plNames.Add(pl.Name);
            }
            var inv = CultureInfo.InvariantCulture;
            var json = new StringBuilder();
            json.Append("{\n  \"stringList\": {\n");
            // names start with a space: Papyrus keeps ONE spelling per word for all scripts (case-insensitive), so a
            // playlist called "Town" showed as "town" because another mod's script had the word "town" first
            json.Append("    \"playlists\": [" + string.Join(", ", plNames.Select(n => Json(" " + n))) + "],\n");
            json.Append("    \"titles\": [" + string.Join(", ", final.Select(h => Json(songs[h].Title))) + "]\n  },\n");
            json.Append("  \"intList\": {\n    \"slots\": [" + string.Join(", ", final.Select(h => songs[h].Slot.ToString())) + "]");
            foreach (var p in plLists) json.Append(",\n    " + p);
            json.Append("\n  },\n  \"floatList\": {\n    \"secs\": [" + string.Join(", ", final.Select(h => songs[h].Secs.ToString("0.00", inv))) + "]\n  }\n}\n");
            File.WriteAllText(Path.Combine(jsonDir, "Music.json"), json.ToString(), new UTF8Encoding(false));
            File.WriteAllLines(cacheFile, final.Concat(inStarter).Concat(shortClips).Select(h => string.Format(inv, "{0},{1},{2:0.00}", h, shortClips.Contains(h) ? 0 : songs[h].Slot, songs[h].Secs)));
            File.WriteAllText(loudFile, Loudness);

            if (inStarter.Count > 0) Log(string.Format("{0} song{1} already in the stone's starter pack - it plays that copy.", inStarter.Count, inStarter.Count == 1 ? " is" : "s are"));
            if (shortClips.Count > 0) Log(string.Format("Skipped {0} clip{1} shorter than {2} seconds (jingles, loops, cuts).", shortClips.Count, shortClips.Count == 1 ? "" : "s", MinSecs));
            if (removed > 0) Log(string.Format("Removed {0} song files no longer used.", removed));
            if (busy > 0) Log(string.Format("{0} unused song file{1} still open in Skyrim - cleared next time.", busy, busy == 1 ? "" : "s"));
            foreach (var d in dropped) Log(string.Format("NOT ADDED (the stone holds {0} songs): {1}", LastSlot - FirstSlot + 1, d));
            foreach (var f in failed) Log("NOT ADDED: " + f);
            return string.Format("{0} song{1} in {2} playlist{3} ({4} new).", final.Count, final.Count == 1 ? "" : "s", plNames.Count, plNames.Count == 1 ? "" : "s", made);
        }

        string RunTool(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi))
            {
                current = p;
                var outTask = p.StandardOutput.ReadToEndAsync();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit();
                current = null;
                return err + outTask.Result;
            }
        }

        public void Stop()
        {
            Cancel = true;
            try { var p = current; if (p != null && !p.HasExited) p.Kill(); } catch { }
        }
    }

    // ------------------------------------------------------------------------------------------- versions of one song
    // Music packs often hold a song many times: loops, single instruments, other speeds, video-editing cuts
    // ("Drums of the Deep - 103 Loop Strings"). A title is a version when everything after a " - " (or inside a
    // closing "(...)") is version words and numbers, so "Artist - Title" names are left alone. TIDY MY MUSIC keeps one
    // version of each song per playlist and moves the rest (and clips too short to play) to a Spare versions folder.
    static class Versions
    {
        static readonly HashSet<string> Words = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "full", "loop", "loops", "looped", "atmosphere", "atmos", "ambience", "ambient", "strings", "string", "percussion", "perc",
            "drums", "drum", "melody", "bonus", "guitar", "lute", "zither", "winds", "wind", "alto", "bass", "crumhorn", "penny", "whistle",
            "flute", "harp", "piano", "choir", "organ", "brass", "horns", "metals", "cut", "cuts", "take", "extra", "version", "ver", "alt",
            "alternate", "alternative", "instrumental", "inst", "stem", "stems", "only", "no", "mix", "remix", "edit", "short", "long",
            "extended", "underscore", "sting", "stinger", "good", "bpm", "part", "pt", "and" };

        public static string SpareDir { get { return Path.Combine(Settings.Folder, "Spare versions"); } }

        static bool AllVersionWords(string s)
        {
            var toks = Regex.Split(s.Trim(), @"[\s\-_,.()\[\]#&+]+").Where(t => t != "").ToList();
            if (toks.Count == 0) return false;
            foreach (var t in toks)
                if (!Words.Contains(t) && !Regex.IsMatch(t, @"^(\d+(bpm|k)?|[a-h]|v\d+)$", RegexOptions.IgnoreCase)) return false;
            return true;
        }

        // "Drums of the Deep - 103 Loop Strings" -> "Drums of the Deep"; "Master of the Feast (Lute)" -> "Master of the Feast"
        public static string SongName(string title)
        {
            string t = title.Trim();
            var m = Regex.Match(t, @"^(.*\S)\s*\(([^()]*)\)$");
            while (m.Success && AllVersionWords(m.Groups[2].Value)) { t = m.Groups[1].Value; m = Regex.Match(t, @"^(.*\S)\s*\(([^()]*)\)$"); }
            for (int i = t.IndexOf(" - "); i > 0; i = t.IndexOf(" - ", i + 3))
                if (AllVersionWords(t.Substring(i + 3))) return t.Substring(0, i).Trim();
            return t;
        }

        public static string Key(string title) { return SongName(title).ToLowerInvariant(); }
        public static string Suffix(string title) { return title.Substring(Math.Min(title.Length, SongName(title).Length)).TrimStart(' ', '-').Trim('(', ')', ' '); }
        static string Title(string f) { return Engine.SongTitle(f); }

        // the version that stays: a Full one, else the plain title, else one that isn't a loop or cut; then the longest
        static int Rank(string suffix)
        {
            string s = suffix.ToLowerInvariant();
            bool loop = s.Contains("loop"), cut = Regex.IsMatch(s, @"\b(cuts?|stingers?)\b");
            if (Regex.IsMatch(s, @"\bfull\b") && !loop && !cut) return 0;
            if (s == "") return 1;
            return loop || cut ? 3 : 2;
        }

        static double Len(string f, Dictionary<string, double> lengths)
        {
            double s; string h = Engine.HashOf(f, false);
            return h != null && lengths.TryGetValue(h, out s) ? s : 0;
        }

        public class Group { public string Name, Playlist, Keep; public List<string> Spare = new List<string>(); }
        public class Plan
        {
            public List<Group> Groups = new List<Group>();
            public List<string> Clips = new List<string>();
            public int Count { get { return Groups.Sum(g => g.Spare.Count) + Clips.Count; } }
        }

        // playlists = folder names to look in (null = the whole music folder, loose songs too)
        public static Plan Find(string musicDir, ICollection<string> playlists, Dictionary<string, double> lengths)
        {
            var plan = new Plan();
            var sets = new List<KeyValuePair<string, List<string>>>();
            foreach (var d in Directory.GetDirectories(musicDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                if (playlists == null || playlists.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                    sets.Add(new KeyValuePair<string, List<string>>(Path.GetFileName(d), Directory.GetFiles(d, "*", SearchOption.AllDirectories).Where(Engine.IsMusic).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()));
            if (playlists == null) sets.Add(new KeyValuePair<string, List<string>>(Engine.Loose, Directory.GetFiles(musicDir).Where(Engine.IsMusic).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()));
            foreach (var set in sets)
            {
                var clips = set.Value.Where(f => { double s = Len(f, lengths); return s > 0 && s < Engine.MinSecs; }).ToList();
                plan.Clips.AddRange(clips);
                foreach (var g in set.Value.Except(clips).GroupBy(f => Key(Title(f))))
                {
                    var files = g.ToList();
                    if (files.Count < 2) continue;
                    string keep = files.OrderBy(f => Rank(Suffix(Title(f)))).ThenByDescending(f => Len(f, lengths)).ThenBy(f => f, StringComparer.OrdinalIgnoreCase).First();
                    var grp = new Group { Name = SongName(Title(keep)), Playlist = set.Key, Keep = keep };
                    grp.Spare.AddRange(files.Where(f => f != keep));
                    plan.Groups.Add(grp);
                }
            }
            return plan;
        }

        // moves the spare versions and clips out of the music folder; a moved version's tags go to the one that stays
        public static int Apply(string musicDir, Plan plan)
        {
            int moved = 0;
            foreach (var g in plan.Groups)
            {
                string keepRel = Tags.Rel(musicDir, g.Keep);
                foreach (var f in g.Spare)
                {
                    foreach (var t in Tags.Of(Tags.Rel(musicDir, f))) if (!Tags.Of(keepRel).Contains(t, StringComparer.OrdinalIgnoreCase)) Tags.Set(keepRel, t, true);
                    if (MoveOut(musicDir, f)) moved++;
                }
            }
            foreach (var f in plan.Clips) if (MoveOut(musicDir, f)) moved++;
            Tags.Save(musicDir);
            foreach (var d in Directory.GetDirectories(musicDir, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }   // folders left empty
            return moved;
        }

        static bool MoveOut(string musicDir, string f)
        {
            try
            {
                string rel = Tags.Rel(musicDir, f);
                string dest = Path.Combine(SpareDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                for (int k = 2; File.Exists(dest); k++)
                    dest = Path.Combine(Path.GetDirectoryName(Path.Combine(SpareDir, rel)), string.Format("{0} ({1}){2}", Path.GetFileNameWithoutExtension(f), k, Path.GetExtension(f)));
                File.Move(f, dest);
                foreach (var t in Tags.Of(rel)) Tags.Set(rel, t, false);
                return true;
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------------------------------- the modern Windows folder picker
    static class FolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogRCW { }
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
        }
        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

        public static string Pick(IWin32Window owner, string title)
        {
            try
            {
                var dlg = (IFileDialog)new FileOpenDialogRCW();
                dlg.SetOptions(0x20 | 0x40);   // pick folders, file system only
                dlg.SetTitle(title);
                dlg.SetOkButtonLabel("Add this folder");
                try { IShellItem music; SHCreateItemFromParsingName(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), IntPtr.Zero, typeof(IShellItem).GUID, out music); dlg.SetDefaultFolder(music); } catch { }
                if (dlg.Show(owner.Handle) != 0) return null;
                IShellItem item; dlg.GetResult(out item);
                IntPtr p; item.GetDisplayName(0x80058000, out p);   // SIGDN_FILESYSPATH
                string path = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p);
                return path;
            }
            catch
            {
                using (var old = new FolderBrowserDialog { Description = title })
                    return old.ShowDialog(owner) == DialogResult.OK ? old.SelectedPath : null;
            }
        }
    }

    // ------------------------------------------------------------------------------------------- the 'done' chime
    // Our own soft two-note crystal ting (made here, not a file), so it sounds the same whatever the Windows sound scheme.
    static class Chime
    {
        static byte[] wav;
        public static void Play()
        {
            try { if (wav == null) wav = Make(); new System.Media.SoundPlayer(new MemoryStream(wav)).Play(); } catch { }
        }
        static byte[] Make()
        {
            const int rate = 44100;
            int n = (int)(rate * 1.4);
            double[] freq = { 880.0, 1318.51 }, start = { 0.0, 0.16 };
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / rate, v = 0;
                for (int k = 0; k < freq.Length; k++)
                {
                    double tt = t - start[k];
                    if (tt < 0) continue;
                    double env = Math.Exp(-tt * 3.5) * Math.Min(1.0, tt * 300);   // quick strike, slow ring
                    v += env * (0.7 * Math.Sin(2 * Math.PI * freq[k] * tt) + 0.2 * Math.Sin(2 * Math.PI * freq[k] * 2.0 * tt) + 0.1 * Math.Sin(2 * Math.PI * freq[k] * 3.01 * tt));
                }
                w.Write((short)(Math.Max(-1.0, Math.Min(1.0, v * 0.3)) * 32767));
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    // ------------------------------------------------------------------------------------------- Windows' drag picture
    // Without this, the picture of what you're dragging from Explorer is drawn UNDER our always-on-top window.
    static class DragImage
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDropTargetHelper
        {
            void DragEnter(IntPtr hwndTarget, System.Runtime.InteropServices.ComTypes.IDataObject dataObject, ref POINT pt, int effect);
            void DragLeave();
            void DragOver(ref POINT pt, int effect);
            void Drop(System.Runtime.InteropServices.ComTypes.IDataObject dataObject, ref POINT pt, int effect);
            void Show(bool show);
        }
        [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")] class DragDropHelper { }
        static IDropTargetHelper helper;
        static IDropTargetHelper H { get { try { if (helper == null) helper = (IDropTargetHelper)new DragDropHelper(); } catch { } return helper; } }
        static System.Runtime.InteropServices.ComTypes.IDataObject Com(DragEventArgs e) { return e.Data as System.Runtime.InteropServices.ComTypes.IDataObject; }

        public static void Enter(IntPtr hwnd, DragEventArgs e) { try { var d = Com(e); var p = new POINT { X = e.X, Y = e.Y }; if (d != null && H != null) H.DragEnter(hwnd, d, ref p, (int)e.Effect); } catch { } }
        public static void Over(DragEventArgs e) { try { var p = new POINT { X = e.X, Y = e.Y }; if (H != null) H.DragOver(ref p, (int)e.Effect); } catch { } }
        public static void Leave() { try { if (H != null) H.DragLeave(); } catch { } }
        public static void Drop(DragEventArgs e) { try { var d = Com(e); var p = new POINT { X = e.X, Y = e.Y }; if (d != null && H != null) H.Drop(d, ref p, (int)e.Effect); } catch { } }
    }

    // ------------------------------------------------------------------------------------------- the window
    class LoaderForm : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);
        static void DarkScrollBars(Control c) { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { } }   // Windows 10 1809+ / 11; harmless elsewhere
        // the empty end of a list's header bar isn't owner-drawn: Windows paints it light unless the header gets the dark theme
        static void DarkHeader(ListView l) { try { IntPtr h = SendMessage(l.Handle, 0x101F, 0, 0); if (h != IntPtr.Zero) SetWindowTheme(h, "DarkMode_ItemsView", null); } catch { } }
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        // Skyrim-ish palette (everything is drawn here, no game artwork or fonts)
        static readonly Color Bg = Color.FromArgb(14, 14, 14), PanelC = Color.FromArgb(20, 20, 20), Rule = Color.FromArgb(92, 92, 88);
        static readonly Color TextC = Color.FromArgb(232, 228, 218), Dim = Color.FromArgb(140, 137, 128), Hi = Color.FromArgb(255, 252, 240), Sel = Color.FromArgb(38, 38, 36);
        readonly Font fTitle, fBig, fHead, fBody, fSmall, fButton;

        readonly string musicDir = Path.Combine(Settings.Folder, "Music");
        readonly string[] startPaths;
        Panel bar, drop, logHolder;
        ComboBox combo;
        ListView list;
        TextBox log;
        string status = "Ready.";
        bool hover;
        Engine engine;
        Thread worker;
        volatile bool again;

        // the SONGS & TAGS view
        TableLayoutPanel grid;
        ListView songList;
        Panel details;
        Label dTitle, dMeta, dHint, viewPlaylists, viewSongs;
        FlowLayoutPanel tagFlow;
        TextBox newTag;
        bool songsMode, settingTags;
        System.Windows.Media.MediaPlayer player;   // previews through Windows' own player (MP3, M4A, FLAC, WAV, WMA)
        string playing;
        readonly System.Windows.Forms.Timer tagTimer = new System.Windows.Forms.Timer { Interval = 1500 };   // tag clicks settle, then the stone's list updates

        static Font F(string family, float size)
        {
            using (var fc = new InstalledFontCollection())
                if (fc.Families.Any(f => f.Name == family)) return new Font(family, size);
            return new Font("Segoe UI", size);
        }

        public LoaderForm(string[] args)
        {
            startPaths = args;
            Directory.CreateDirectory(musicDir);
            fTitle = F("Bahnschrift Condensed", 15); fBig = F("Bahnschrift Condensed", 19); fHead = F("Bahnschrift SemiCondensed", 9.5f);
            fBody = F("Bahnschrift SemiCondensed", 11); fSmall = F("Bahnschrift SemiCondensed", 9.5f); fButton = F("Bahnschrift Condensed", 12);

            Text = "Song Stone Loader";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size((int)(wa.Width * 0.4), (int)(wa.Height * 0.42));   // about a sixth of the screen
            MinimumSize = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;   // always on top, so it stays visible while you drag songs from Explorer
            BackColor = Bg; ForeColor = TextC; Font = fBody;
            AllowDrop = true; DoubleBuffered = true;
            Padding = new Padding(1);
            Paint += (s, e) => e.Graphics.DrawRectangle(new Pen(Rule), 0, 0, Width - 1, Height - 1);
            Tags.Load(musicDir);
            BuildUi();
            tagTimer.Tick += (s, e) => { tagTimer.Stop(); Convert(); };
            HandleCreated += (s, e) => { foreach (var c in new Control[] { list, songList, log, details.Controls[0] }) DarkScrollBars(c); DarkHeader(list); DarkHeader(songList); };
            Shown += (s, e) =>
            {
                RefreshList();
                AddLog("Music folder: " + musicDir);
                if (Tags.Restored) AddLog("Your tags were missing - put them back from the backup in " + Tags.BackupDir);
                KeepFavourites();
                if (startPaths != null && startPaths.Length > 0) AddedSongs(AddPaths(startPaths));
                else
                {
                    // Skyrim (or the game folder) reinstalled: the game's converted copies and song list are gone, but the
                    // music folder lives outside the game - convert it all back without being asked
                    string sky = Engine.FindSkyrim();
                    if (sky != null && !File.Exists(Path.Combine(sky, @"Data\SKSE\Plugins\StorageUtilData\SongStone\Music.json"))
                        && Directory.EnumerateFiles(musicDir, "*", SearchOption.AllDirectories).Any(Engine.IsMusic))
                    {
                        AddLog("Your songs aren't in the game (reinstalled?) - putting them back from your music folder.");
                        SetStatus("Putting your songs back into the game...");
                        Convert();
                    }
                }
            };
            FormClosing += OnClosing;
        }

        // ---------------------------------------------------------------- drawing helpers
        static void Spaced(Graphics g, string s, Font font, Color color, float x, float y, float track, bool centre)
        {
            var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;   // else a space measures 0
            float w = 0;
            foreach (char ch in s) w += g.MeasureString(ch.ToString(), font, 1000, fmt).Width + track;
            w -= track;
            if (centre) x -= w / 2;
            using (var b = new SolidBrush(color))
                foreach (char ch in s) { g.DrawString(ch.ToString(), font, b, x, y, fmt); x += g.MeasureString(ch.ToString(), font, 1000, fmt).Width + track; }
        }

        static void RuleLine(Graphics g, float x1, float x2, float y, bool diamond)
        {
            float w = x2 - x1;
            if (w <= 2) return;
            using (var lg = new LinearGradientBrush(new RectangleF(x1, y - 1, w, 2), Bg, Bg, 0f))
            {
                var blend = new ColorBlend(3);
                blend.Colors = new[] { Color.FromArgb(0, Rule), Rule, Color.FromArgb(0, Rule) };
                blend.Positions = new[] { 0f, 0.5f, 1f };
                lg.InterpolationColors = blend;
                g.FillRectangle(lg, x1, y, w, 1);
            }
            if (diamond)
            {
                float cx = x1 + w / 2;
                var pts = new[] { new PointF(cx, y - 4), new PointF(cx + 4, y), new PointF(cx, y + 4), new PointF(cx - 4, y) };
                using (var b = new SolidBrush(Bg)) g.FillPolygon(b, pts);
                using (var p = new Pen(Rule)) g.DrawPolygon(p, pts);
            }
        }

        static void Diamond(Graphics g, float cx, float cy, float r, Color c)
        {
            using (var b = new SolidBrush(c))
                g.FillPolygon(b, new[] { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) });
        }

        void DragWindow(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, 2, 0);   // WM_NCLBUTTONDOWN on the caption = Windows moves the window
        }

        // ---------------------------------------------------------------- layout
        void BuildUi()
        {
            // title strip: drag the window by it; minimise and close on the right
            bar = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Bg };
            bar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Spaced(e.Graphics, "SONG STONE LOADER", fTitle, TextC, bar.Width / 2f, 12, 4, true);
                RuleLine(e.Graphics, 40, bar.Width - 40, bar.Height - 6, true);
            };
            bar.MouseDown += DragWindow;
            var close = CapButton("\u2715", 44); close.Click += (s, e) => Close();
            var min = CapButton("\u2013", 82); min.Click += (s, e) => WindowState = FormWindowState.Minimized;
            bar.Resize += (s, e) => { foreach (Control c in bar.Controls) c.Left = bar.Width - (int)c.Tag; };

            grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 10, 18, 12), ColumnCount = 2, RowCount = 4, BackColor = Bg };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            Controls.Add(grid);
            Controls.Add(bar);   // added last so it docks above the grid
            grid.MouseDown += DragWindow;

            // the drop zone: a framed panel with diamond corners
            drop = new DoubleBufferedPanel { Dock = DockStyle.Fill, BackColor = PanelC, AllowDrop = true, Margin = new Padding(0, 0, 0, 6) };
            drop.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                Color c = hover ? Hi : Rule;
                using (var p = new Pen(c)) g.DrawRectangle(p, 8, 8, drop.Width - 17, drop.Height - 17);
                using (var p = new Pen(Color.FromArgb(40, c))) g.DrawRectangle(p, 12, 12, drop.Width - 25, drop.Height - 25);
                foreach (var pt in new[] { new PointF(8, 8), new PointF(drop.Width - 9, 8), new PointF(8, drop.Height - 9), new PointF(drop.Width - 9, drop.Height - 9) }) Diamond(g, pt.X, pt.Y, 4, c);
                float mid = drop.Height / 2f;
                if (drop.Height < 130)   // the slim version in the songs view: heading + status only
                {
                    Spaced(g, "DROP SONGS OR FOLDERS HERE", fBig, hover ? Hi : TextC, drop.Width / 2f, mid - 28, 3, true);
                    using (var b = new SolidBrush(status == "Ready." ? Dim : Hi)) using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                        g.DrawString(status, fBody, b, new RectangleF(20, mid + 6, drop.Width - 40, 26), sf);
                    return;
                }
                Spaced(g, "DROP SONGS OR FOLDERS HERE", fBig, hover ? Hi : TextC, drop.Width / 2f, mid - 30, 3, true);
                RuleLine(g, drop.Width * 0.3f, drop.Width * 0.7f, mid + 6, true);
                Spaced(g, "A FOLDER BECOMES A PLAYLIST  -  OR USE THE ADD BUTTONS BELOW", fSmall, Dim, drop.Width / 2f, mid + 16, 1.5f, true);
                using (var b = new SolidBrush(Hi)) using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(status, fBody, status == "Ready." ? new SolidBrush(Dim) : b, new RectangleF(20, Math.Min(mid + 40, drop.Height - 40), drop.Width - 40, 26), sf);
            };
            grid.Controls.Add(drop, 0, 0); grid.SetColumnSpan(drop, 2);

            // single songs go into ...
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Bg };
            row.MouseDown += DragWindow;
            var lbl = new Label { Text = "SINGLE SONGS GO INTO", Font = fHead, AutoSize = true, Margin = new Padding(0, 13, 10, 0), ForeColor = Dim };
            combo = new ComboBox { Width = 240, Text = Engine.Loose, Margin = new Padding(0, 8, 0, 0), BackColor = PanelC, ForeColor = TextC, FlatStyle = FlatStyle.Flat, Font = fBody };
            row.Controls.Add(lbl); row.Controls.Add(combo);
            viewPlaylists = ViewLabel("PLAYLISTS", false); viewPlaylists.Margin = new Padding(36, 13, 0, 0);
            var sep = new Label { Text = "|", Font = fHead, AutoSize = true, ForeColor = Rule, Margin = new Padding(8, 13, 8, 0) };
            viewSongs = ViewLabel("SONGS & TAGS", true);
            row.Controls.Add(viewPlaylists); row.Controls.Add(sep); row.Controls.Add(viewSongs);
            grid.Controls.Add(row, 0, 1); grid.SetColumnSpan(row, 2);

            // playlists
            list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, AllowDrop = true,
                BackColor = PanelC, ForeColor = TextC, BorderStyle = BorderStyle.None, Margin = new Padding(0, 0, 10, 0), Font = fBody, OwnerDraw = true };
            list.Columns.Add("PLAYLISTS", 180); list.Columns.Add("SONGS", 70);
            list.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(PanelC)) e.Graphics.FillRectangle(b, e.Bounds);
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float x = e.ColumnIndex == 1 ? e.Bounds.Right - 8 - e.Graphics.MeasureString(e.Header.Text, fHead).Width : e.Bounds.X + 8;
                using (var b = new SolidBrush(Dim)) e.Graphics.DrawString(e.Header.Text, fHead, b, x, e.Bounds.Y + 5);
                if (e.ColumnIndex == 0) RuleLine(e.Graphics, 0, list.ClientSize.Width, e.Bounds.Bottom - 1, false);
            };
            list.DrawItem += (s, e) => { };
            list.DrawSubItem += (s, e) =>
            {
                var g = e.Graphics; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                bool on = e.Item.Selected;
                using (var b = new SolidBrush(on ? Sel : PanelC)) g.FillRectangle(b, e.Bounds);
                if (e.ColumnIndex == 0)
                {
                    if (on) Diamond(g, e.Bounds.X + 8, e.Bounds.Y + e.Bounds.Height / 2f, 3, Hi);
                    using (var b = new SolidBrush(on ? Hi : TextC)) g.DrawString(e.SubItem.Text, fBody, b, e.Bounds.X + 16, e.Bounds.Y + 2);
                }
                else
                {
                    float w = g.MeasureString(e.SubItem.Text, fBody).Width;
                    using (var b = new SolidBrush(Dim)) g.DrawString(e.SubItem.Text, fBody, b, e.Bounds.Right - 8 - w, e.Bounds.Y + 2);
                }
            };
            list.Resize += (s, e) => { if (list.Width > 120) list.Columns[0].Width = list.ClientSize.Width - list.Columns[1].Width - 2; };
            var leftHost = new Panel { Dock = DockStyle.Fill, BackColor = PanelC, Margin = new Padding(0, 0, 10, 0) };
            list.Margin = new Padding(0);
            leftHost.Controls.Add(list);
            BuildSongList();
            leftHost.Controls.Add(songList);
            grid.Controls.Add(leftHost, 0, 2);

            // the log sits in a clipping panel a little narrower than the text box, so its light Windows scroll bar is
            // hidden (the mouse wheel still scrolls it, and new lines scroll it to the end)
            logHolder = new Panel { Dock = DockStyle.Fill, BackColor = PanelC, Margin = new Padding(0) };
            log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = PanelC, ForeColor = Dim,
                BorderStyle = BorderStyle.None, Font = fSmall, Location = new Point(10, 8) };
            logHolder.Resize += (s, e) => log.Size = new Size(logHolder.Width - 10 + SystemInformation.VerticalScrollBarWidth, logHolder.Height - 14);
            logHolder.Controls.Add(log);
            var rightHost = new Panel { Dock = DockStyle.Fill, BackColor = PanelC, Margin = new Padding(0) };
            rightHost.Controls.Add(logHolder);
            BuildDetails();
            rightHost.Controls.Add(details);
            grid.Controls.Add(rightHost, 1, 2);

            // buttons + status
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 10, 0, 0), BackColor = Bg };
            bottom.MouseDown += DragWindow;
            var addSongs = MakeButton("ADD SONGS"); addSongs.Click += (s, e) => AddSongsClick();
            var addFolder = MakeButton("ADD A FOLDER"); addFolder.Click += (s, e) => AddFolderClick();
            // dropping converts by itself; RESCAN is for songs changed by hand in the music folder
            var convert = MakeButton("RESCAN FOLDER"); convert.Click += (s, e) => Convert();
            var open = MakeButton("OPEN MUSIC FOLDER"); open.Click += (s, e) => Process.Start("explorer.exe", "\"" + musicDir + "\"");
            var tidy = MakeButton("TIDY MY MUSIC"); tidy.Click += (s, e) => TidyClick();
            bottom.Controls.Add(addSongs); bottom.Controls.Add(addFolder); bottom.Controls.Add(open); bottom.Controls.Add(convert); bottom.Controls.Add(tidy);
            grid.Controls.Add(bottom, 0, 3); grid.SetColumnSpan(bottom, 2);

            viewPlaylists.ForeColor = Hi;   // starts in the playlists view
            foreach (Control c in new Control[] { this, drop, list, songList })
            {
                c.DragEnter += OnDragEnter;
                c.DragOver += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; DragImage.Over(e); };
                c.DragLeave += (s, e) => { hover = false; drop.Invalidate(); DragImage.Leave(); };
                c.DragDrop += OnDragDrop;
            }
        }

        class DoubleBufferedPanel : Panel { public DoubleBufferedPanel() { DoubleBuffered = true; ResizeRedraw = true; } }

        // ---------------------------------------------------------------- the SONGS & TAGS view
        Label ViewLabel(string text, bool songs)
        {
            var l = new Label { Text = text, Font = fHead, AutoSize = true, ForeColor = Dim, Cursor = Cursors.Hand, Margin = new Padding(0, 13, 0, 0), UseMnemonic = false };
            l.Click += (s, e) => SetMode(songs);
            l.MouseEnter += (s, e) => l.ForeColor = Hi;
            l.MouseLeave += (s, e) => l.ForeColor = songsMode == songs ? Hi : Dim;
            return l;
        }

        void SetMode(bool songs)
        {
            songsMode = songs;
            list.Visible = !songs; logHolder.Visible = !songs;
            songList.Visible = songs; details.Visible = songs;
            grid.RowStyles[0].Height = songs ? 20 : 42;   // a slimmer drop zone gives the song list room
            grid.ColumnStyles[0].Width = songs ? 56 : 42;
            grid.ColumnStyles[1].Width = songs ? 44 : 58;
            grid.RowStyles[2].Height = songs ? 80 : 58;
            viewPlaylists.ForeColor = songs ? Dim : Hi; viewSongs.ForeColor = songs ? Hi : Dim;
            drop.Invalidate();
            if (songs) RefreshSongs(); else { StopPlay(); RefreshList(); }
        }

        void BuildSongList()
        {
            songList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, AllowDrop = true,
                MultiSelect = false, HideSelection = false, BackColor = PanelC, ForeColor = TextC, BorderStyle = BorderStyle.None, Font = fBody, OwnerDraw = true, Visible = false };
            songList.Columns.Add("SONG", 200); songList.Columns.Add("TIME", 56); songList.Columns.Add("TAGS", 120);
            songList.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(PanelC)) e.Graphics.FillRectangle(b, e.Bounds);
                e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var b = new SolidBrush(Dim)) e.Graphics.DrawString(e.Header.Text, fHead, b, e.Bounds.X + (e.ColumnIndex == 0 ? 8 : 2), e.Bounds.Y + 5);
                if (e.ColumnIndex == 0) RuleLine(e.Graphics, 0, songList.ClientSize.Width, e.Bounds.Bottom - 1, false);
            };
            songList.DrawItem += (s, e) => { };
            songList.DrawSubItem += (s, e) =>
            {
                var g = e.Graphics; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                bool on = e.Item.Selected, isPlaying = string.Equals((string)e.Item.Tag, playing, StringComparison.OrdinalIgnoreCase);
                using (var b = new SolidBrush(on ? Sel : PanelC)) g.FillRectangle(b, e.Bounds);
                int pad = e.ColumnIndex == 0 ? 16 : 2;
                var r = new RectangleF(e.Bounds.X + pad, e.Bounds.Y + 2, e.Bounds.Width - pad - 2, e.Bounds.Height - 2);
                using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                {
                    if (e.ColumnIndex == 0)
                    {
                        if (on || isPlaying) Diamond(g, e.Bounds.X + 8, e.Bounds.Y + e.Bounds.Height / 2f, 3, isPlaying ? Color.FromArgb(126, 196, 255) : Hi);   // playing = welkynd blue
                        using (var b = new SolidBrush(on ? Hi : TextC)) g.DrawString(e.SubItem.Text, fBody, b, r, sf);
                    }
                    else using (var b = new SolidBrush(Dim)) g.DrawString(e.SubItem.Text, e.ColumnIndex == 2 ? fSmall : fBody, b, r, sf);
                }
            };
            songList.Resize += (s, e) => FitSongColumns();
            songList.SelectedIndexChanged += (s, e) => ShowSong();
            songList.DoubleClick += (s, e) => { var f = SelectedSong(); if (f != null) Play(f); };
            songList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) { e.Handled = true; RemoveClick(); } };
        }

        void FitSongColumns()
        {
            int w = songList.ClientSize.Width;
            if (w < 200) return;
            songList.Columns[1].Width = 52; songList.Columns[2].Width = (int)(w * 0.26); songList.Columns[0].Width = w - 52 - songList.Columns[2].Width - 10;   // a few px spare, else a sideways scroll bar shows
        }

        string SelectedSong() { return songList != null && songList.SelectedItems.Count > 0 ? (string)songList.SelectedItems[0].Tag : null; }

        void BuildDetails()
        {
            details = new Panel { Dock = DockStyle.Fill, BackColor = PanelC, Visible = false };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = PanelC, Padding = new Padding(12, 8, 8, 4) };
            dTitle = new Label { Font = F("Bahnschrift Condensed", 15), ForeColor = Hi, AutoSize = false, Height = 28, AutoEllipsis = true, Margin = new Padding(0) };
            dMeta = new Label { Font = fSmall, ForeColor = Dim, AutoSize = false, Height = 22, AutoEllipsis = true, Margin = new Padding(0, 0, 0, 6) };
            var playRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = PanelC, Margin = new Padding(0, 0, 0, 8) };
            var play = MakeButton("▶  PLAY"); play.Click += (s, e) => { var f = SelectedSong(); if (f != null) Play(f); };
            var stop = MakeButton("■  STOP"); stop.Click += (s, e) => StopPlay();
            var remove = MakeButton("✕  REMOVE"); remove.Click += (s, e) => RemoveClick();
            playRow.Controls.Add(play); playRow.Controls.Add(stop); playRow.Controls.Add(remove);
            var head = new Label { Text = "TAGS  -  CLICK ALL THAT FIT", Font = fHead, ForeColor = Dim, AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
            tagFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = PanelC, Margin = new Padding(0) };
            var newRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = PanelC, Margin = new Padding(0, 6, 0, 4) };
            newTag = new TextBox { Width = 150, BackColor = Bg, ForeColor = TextC, BorderStyle = BorderStyle.FixedSingle, Font = fBody, Margin = new Padding(0, 4, 8, 0) };
            newTag.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; NewTagClick(); } };
            var add = MakeButton("+ NEW TAG"); add.Click += (s, e) => NewTagClick();
            newRow.Controls.Add(newTag); newRow.Controls.Add(add);
            dHint = new Label { Text = "Each tag becomes a playlist in the stone.",
                Font = fSmall, ForeColor = Dim, AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            flow.Controls.AddRange(new Control[] { dTitle, dMeta, playRow, head, tagFlow, newRow, dHint });
            details.Controls.Add(flow);
            details.Resize += (s, e) =>
            {
                int w = Math.Max(100, details.ClientSize.Width - 28);
                dTitle.Width = w; dMeta.Width = w;
                tagFlow.MaximumSize = new Size(w, 0); dHint.MaximumSize = new Size(w, 0);
            };
            BuildTagButtons();
        }

        void BuildTagButtons()
        {
            tagFlow.SuspendLayout();
            tagFlow.Controls.Clear();
            foreach (var tag in Tags.All)
            {
                var cb = new CheckBox { Appearance = Appearance.Button, FlatStyle = FlatStyle.Flat, AutoSize = false, Font = fButton, BackColor = Bg, ForeColor = Dim,
                    Tag = tag, Margin = new Padding(0, 0, 6, 6), TextAlign = ContentAlignment.MiddleCenter };
                var size = TextRenderer.MeasureText("\u25C6  " + tag.ToUpperInvariant(), fButton);
                cb.Size = new Size(size.Width + 22, size.Height + 12);
                cb.FlatAppearance.CheckedBackColor = Sel; cb.FlatAppearance.MouseOverBackColor = Sel;
                StyleTag(cb);
                cb.CheckedChanged += (s, e) => TagToggled(cb);
                tagFlow.Controls.Add(cb);
            }
            tagFlow.ResumeLayout();
            ShowSong();
        }

        static void StyleTag(CheckBox cb)
        {
            cb.Text = (cb.Checked ? "◆  " : "◇  ") + ((string)cb.Tag).ToUpperInvariant();
            cb.ForeColor = cb.Checked ? Hi : Dim;
            cb.FlatAppearance.BorderColor = cb.Checked ? Hi : Rule;
        }

        void TagToggled(CheckBox cb)
        {
            StyleTag(cb);
            if (settingTags) return;
            string f = SelectedSong();
            if (f == null) { settingTags = true; cb.Checked = false; StyleTag(cb); settingTags = false; return; }
            string rel = Tags.Rel(musicDir, f);
            Tags.Set(rel, (string)cb.Tag, cb.Checked);
            Tags.Save(musicDir);
            songList.SelectedItems[0].SubItems[2].Text = string.Join(", ", Tags.Of(rel));
            songList.Invalidate();
            tagTimer.Stop(); tagTimer.Start();   // the stone's list updates once the clicking settles
        }

        // REMOVE (or the Delete key): the song's file goes to the Recycle Bin (so it can be brought back), its tags are
        // forgotten, a playlist folder left empty goes too, and the stone's list is updated. The same song in another
        // playlist stays there (user 2026-10-13: doubles after adding a zip of songs already in the music).
        void RemoveClick()
        {
            string f = SelectedSong();
            if (f == null) return;
            if (worker != null && worker.IsAlive) { SetStatus("Wait for the converting to finish, then remove."); return; }
            string rel = Tags.Rel(musicDir, f);
            string folder = Path.GetDirectoryName(rel);
            string list = string.IsNullOrEmpty(folder) ? Engine.Loose : folder;
            string h = Engine.HashOf(f, false);
            var others = h == null ? new List<string>() : Directory.GetFiles(musicDir, "*", SearchOption.AllDirectories)
                .Where(x => Engine.IsMusic(x) && !string.Equals(x, f, StringComparison.OrdinalIgnoreCase) && Engine.HashOf(x, false) == h)
                .Select(x => { string d = Path.GetDirectoryName(Tags.Rel(musicDir, x)); return string.IsNullOrEmpty(d) ? Engine.Loose : d; })
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var t = new StringBuilder();
            t.AppendFormat("Take '{0}' out of the playlist '{1}'?\r\n\r\n", Engine.SongTitle(f), list);
            if (others.Count > 0)
                t.AppendFormat("The same song stays in {0}: {1}.\r\n\r\n", others.Count == 1 ? "this other playlist" : "these other playlists", string.Join(", ", others));
            t.Append("The file goes to the Recycle Bin, so you can get it back from there:\r\n   " + f + "\r\n\r\n");
            t.Append("In the game: open Mod Configuration > Ayleid Song Stone and press Reload songs (or restart the game).");
            using (var d = new TidyDialog(this, "REMOVE A SONG", t.ToString(), "REMOVE", "CANCEL"))
                if (d.ShowDialog(this) != DialogResult.OK) return;
            bool wasPlaying = string.Equals(f, playing, StringComparison.OrdinalIgnoreCase);
            if (wasPlaying) StopPlay();
            int at = songList.SelectedIndices.Count > 0 ? songList.SelectedIndices[0] : 0;
            try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
            catch (Exception ex) { AddLog("! couldn't remove " + Path.GetFileName(f) + ": " + ex.Message); return; }
            Tags.Forget(rel);
            Tags.Save(musicDir);
            string dir = Path.GetDirectoryName(f);
            try { if (!string.Equals(dir.TrimEnd('\\'), musicDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            AddLog("Removed " + Engine.SongTitle(f) + " from '" + list + "' (it's in the Recycle Bin)");
            RefreshList();
            RefreshSongs();
            if (songList.Items.Count > 0)   // the next song in the list is picked, ready for another remove
            {
                foreach (ListViewItem it in songList.SelectedItems) it.Selected = false;
                var next = songList.Items[Math.Min(at, songList.Items.Count - 1)];
                next.Selected = true; next.Focused = true; next.EnsureVisible();
                if (wasPlaying) Play((string)next.Tag);   // sorting by ear: listen, tag or remove, the next one plays
            }
            Convert();
        }

        void NewTagClick()
        {
            string t = Regex.Replace(newTag.Text ?? "", "[^A-Za-z0-9 '\\-]", "").Trim();
            if (t.Length > 24) t = t.Substring(0, 24).Trim();
            if (t == "") return;
            if (t == t.ToLowerInvariant()) t = char.ToUpperInvariant(t[0]) + t.Substring(1);
            Tags.AddTag(t);
            Tags.Save(musicDir);
            newTag.Text = "";
            BuildTagButtons();
            if (SelectedSong() != null)
                foreach (CheckBox cb in tagFlow.Controls) if (string.Equals((string)cb.Tag, t, StringComparison.OrdinalIgnoreCase)) cb.Checked = true;
        }

        void ShowSong()
        {
            if (details == null || songList == null || tagFlow == null) return;
            string f = SelectedSong();
            settingTags = true;
            var tags = f == null ? new List<string>() : Tags.Of(Tags.Rel(musicDir, f));
            foreach (CheckBox cb in tagFlow.Controls) { cb.Checked = tags.Contains((string)cb.Tag, StringComparer.OrdinalIgnoreCase); StyleTag(cb); }
            settingTags = false;
            if (f == null) { dTitle.Text = "No songs yet"; dMeta.Text = "Drop some on the window, then tag them here."; return; }
            dTitle.Text = Engine.SongTitle(f);
            string folder = Path.GetDirectoryName(Tags.Rel(musicDir, f));
            string time = songList.SelectedItems[0].SubItems[1].Text;
            dMeta.Text = (string.IsNullOrEmpty(folder) ? Engine.Loose : folder) + "   ·   " + time
                + (string.Equals(f, playing, StringComparison.OrdinalIgnoreCase) ? "   ·   ♪ PLAYING" : "");
        }

        void RefreshSongs()
        {
            string skyrim = Engine.FindSkyrim();
            var lengths = skyrim != null ? Engine.Lengths(skyrim) : new Dictionary<string, double>();
            string sel = SelectedSong();
            bool unknown = false;
            ListViewItem selItem = null;
            songList.BeginUpdate(); songList.Items.Clear();
            foreach (var f in Directory.GetFiles(musicDir, "*", SearchOption.AllDirectories).Where(Engine.IsMusic).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string h = Engine.HashOf(f, false);
                if (h == null) unknown = true;
                double secs = 0;
                bool known = h != null && lengths.TryGetValue(h, out secs);
                if (known && secs < Engine.MinSecs) continue;   // clips aren't in the stone
                var it = new ListViewItem(Engine.SongTitle(f)) { Tag = f };
                it.SubItems.Add(known ? string.Format("{0}:{1:00}", (int)secs / 60, (int)secs % 60) : "--:--");
                it.SubItems.Add(string.Join(", ", Tags.Of(Tags.Rel(musicDir, f))));
                songList.Items.Add(it);
                if (string.Equals(f, sel, StringComparison.OrdinalIgnoreCase)) selItem = it;
            }
            songList.EndUpdate();
            FitSongColumns();
            if (selItem == null && songList.Items.Count > 0) selItem = songList.Items[0];
            if (selItem != null) { selItem.Selected = true; selItem.Focused = true; selItem.EnsureVisible(); }
            ShowSong();
            if (unknown && (worker == null || !worker.IsAlive)) Convert();   // first look after an update: learn the songs once, then lengths show
        }

        void Play(string f)
        {
            try
            {
                if (player == null)
                {
                    player = new System.Windows.Media.MediaPlayer();
                    player.MediaEnded += (s, e) => { playing = null; songList.Invalidate(); ShowSong(); };
                    player.MediaFailed += (s, e) => { AddLog("! can't play this one: " + e.ErrorException.Message); playing = null; songList.Invalidate(); ShowSong(); };
                }
                player.Open(new Uri(f));
                player.Volume = 0.8;
                player.Play();
                playing = f;
            }
            catch (Exception ex) { AddLog("! can't play " + Path.GetFileName(f) + ": " + ex.Message); playing = null; }
            songList.Invalidate();
            ShowSong();
        }

        void StopPlay()
        {
            try { if (player != null) { player.Stop(); player.Close(); } } catch { }
            playing = null;
            if (songList != null) { songList.Invalidate(); ShowSong(); }
        }

        Label CapButton(string t, int right)
        {
            var b = new Label { Text = t, Font = F("Bahnschrift SemiCondensed", 14), ForeColor = Dim, BackColor = Bg, Size = new Size(36, 34),
                TextAlign = ContentAlignment.MiddleCenter, Tag = right, Location = new Point(bar.Width - right, 6) };
            b.MouseEnter += (s, e) => b.ForeColor = Hi;
            b.MouseLeave += (s, e) => b.ForeColor = Dim;
            bar.Controls.Add(b);
            return b;
        }

        Button MakeButton(string t)
        {
            var b = new Button { Text = t, Font = fButton, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Bg, ForeColor = TextC,
                Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 0, 10, 0) };
            b.FlatAppearance.BorderColor = Rule; b.FlatAppearance.MouseOverBackColor = Sel; b.FlatAppearance.MouseDownBackColor = Sel;
            b.MouseEnter += (s, e) => { b.ForeColor = Hi; b.FlatAppearance.BorderColor = Hi; };
            b.MouseLeave += (s, e) => { b.ForeColor = TextC; b.FlatAppearance.BorderColor = Rule; };
            return b;
        }

        // ---------------------------------------------------------------- music folder
        void AddLog(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(AddLog), s); return; }
            log.AppendText(s + "\r\n");
            var m = Regex.Match(s, @"^\[(\d+)/(\d+)\]");
            if (m.Success) SetStatus(string.Format("Converting song {0} of {1}...", m.Groups[1].Value, m.Groups[2].Value));
        }

        // songs per playlist that actually play in the stone (from the song list the converter wrote), so skipped
        // clips don't count; a playlist not converted yet shows its number of files
        static Dictionary<string, int> StoneCounts()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string skyrim = Engine.FindSkyrim();
                if (skyrim == null) return d;
                string file = Path.Combine(skyrim, @"Data\SKSE\Plugins\StorageUtilData\SongStone\Music.json");
                if (!File.Exists(file)) return d;
                string json = File.ReadAllText(file);
                string names = Regex.Match(json, "\"playlists\": \\[(.*?)\\]").Groups[1].Value;
                var list = Regex.Matches(names, "\"((?:[^\"\\\\]|\\\\.)*)\"").Cast<Match>().Select(m => m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")).ToList();
                for (int i = 0; i < list.Count; i++)
                {
                    var m = Regex.Match(json, "\"pl" + i + "\": \\[(.*?)\\]");
                    d[list[i].Trim()] = m.Success ? m.Groups[1].Value.Split(',').Count(x => x.Trim() != "") : 0;
                }
            }
            catch { }
            return d;
        }

        void RefreshList()
        {
            var stone = StoneCounts();
            list.BeginUpdate(); list.Items.Clear();
            var names = new List<string>();
            foreach (var d in Directory.GetDirectories(musicDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                int n = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Count(Engine.IsMusic);
                string key = Engine.CleanTitle(Path.GetFileName(d));
                var it = new ListViewItem(Path.GetFileName(d)); it.SubItems.Add((stone.ContainsKey(key) ? stone[key] : n).ToString()); list.Items.Add(it);
                names.Add(Path.GetFileName(d));
            }
            int looseN = Directory.GetFiles(musicDir).Count(Engine.IsMusic);
            if (looseN > 0) { var it = new ListViewItem(Engine.Loose + " (loose)"); it.SubItems.Add((stone.ContainsKey(Engine.Loose) ? stone[Engine.Loose] : looseN).ToString()); list.Items.Add(it); }
            // tag playlists (no folder of their own)
            var folderKeys = new HashSet<string>(names.Select(Engine.CleanTitle), StringComparer.OrdinalIgnoreCase);
            foreach (var tag in Tags.All)
                if (!folderKeys.Contains(tag) && stone.ContainsKey(tag) && stone[tag] > 0)
                { var it = new ListViewItem("◆ " + tag + " (tag)"); it.SubItems.Add(stone[tag].ToString()); list.Items.Add(it); }
            list.EndUpdate();
            string keep = combo.Text;
            combo.Items.Clear();
            if (!names.Contains(Engine.Loose)) combo.Items.Add(Engine.Loose);
            foreach (var n in names) combo.Items.Add(n);
            combo.Text = keep;
        }

        void SetStatus(string s) { status = s; drop.Invalidate(); }

        void AddSongsClick()
        {
            string name = SafeName(combo.Text);
            using (var dlg = new OpenFileDialog
            {
                Multiselect = true, Title = "Add songs to the playlist '" + name + "'",
                Filter = "Music and .zip files|*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wav;*.wma;*.xwm;*.zip|All files|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
            })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddedSongs(AddPaths(dlg.FileNames));
            }
        }

        void AddFolderClick()
        {
            string path = FolderPicker.Pick(this, "Pick a folder of songs - it becomes a playlist with the folder's name");
            if (path != null) AddedSongs(AddPaths(new[] { path }));
        }

        static string SafeName(string s)
        {
            s = Regex.Replace(s ?? "", "[\\\\/:*?\"<>|]", "").Trim();
            return s == "" ? Engine.Loose : s;
        }

        static int CopySong(string file, string destDir)
        {
            Directory.CreateDirectory(destDir);
            var fi = new FileInfo(file);
            string dest = Path.Combine(destDir, fi.Name);
            if (File.Exists(dest))
            {
                if (new FileInfo(dest).Length == fi.Length) return 0;   // already there
                for (int k = 2; ; k++)   // a different song with the same name: "Name (2)", unless it's already there as one
                {
                    dest = Path.Combine(destDir, string.Format("{0} ({1}){2}", Path.GetFileNameWithoutExtension(fi.Name), k, fi.Extension));
                    if (!File.Exists(dest)) break;
                    if (new FileInfo(dest).Length == fi.Length) return 0;
                }
            }
            File.Copy(file, dest);
            return 1;
        }

        // ---------------------------------------------------------------- one version of each song
        static Dictionary<string, double> LengthsNow() { string sky = Engine.FindSkyrim(); return sky != null ? Engine.Lengths(sky) : new Dictionary<string, double>(); }

        void TidyClick()
        {
            if (worker != null && worker.IsAlive) { SetStatus("Wait for the converting to finish, then tidy."); return; }
            var plan = Versions.Find(musicDir, null, LengthsNow());
            if (plan.Count == 0) { SetStatus("Nothing to tidy: every song is in your music once."); return; }
            if (!AskTidy(plan, false)) return;
            DoTidy(plan);
            Convert();
        }

        // after adding songs: a folder that came with several versions of a song (a music pack) gets the same offer
        void AddedSongs(int n)
        {
            if (n <= 0) return;
            if (worker == null || !worker.IsAlive)
            {
                var plan = Versions.Find(musicDir, lastInto, LengthsNow());
                if (plan.Groups.Count > 0 && AskTidy(plan, true)) DoTidy(plan);
            }
            Convert();
        }

        void DoTidy(Versions.Plan plan)
        {
            StopPlay();
            int moved = Versions.Apply(musicDir, plan);
            AddLog(string.Format("Tidied: moved {0} file{1} to {2}", moved, moved == 1 ? "" : "s", Versions.SpareDir));
            Tags.Load(musicDir);
            RefreshList();
            if (songsMode) RefreshSongs();
        }

        bool AskTidy(Versions.Plan plan, bool justAdded)
        {
            var t = new StringBuilder();
            t.Append(justAdded ? "Some of the songs you just added come in several versions" : "Some songs are in your music in several versions");
            t.Append(" (loops, single instruments, other speeds, short cuts).\r\n\r\n");
            if (plan.Groups.Count > 0)
            {
                t.Append("Keep ONE version of each song - the one named here - and move the others out:\r\n\r\n");
                foreach (var g in plan.Groups)
                {
                    string keep = Versions.Suffix(Engine.SongTitle(g.Keep));
                    t.AppendFormat("   ◆  {0}{1}  -  keeps {2}, moves {3}\r\n", g.Name,
                        string.Equals(g.Name, g.Playlist, StringComparison.OrdinalIgnoreCase) ? "" : " (in '" + g.Playlist + "')",
                        keep == "" ? "the plain one" : "'" + keep + "'", g.Spare.Count);
                }
            }
            if (plan.Clips.Count > 0)
                t.AppendFormat("{0}   ◆  {1} short clip{2} under {3} seconds - they never play in the stone\r\n", plan.Groups.Count > 0 ? "" : "Move out:\r\n\r\n",
                    plan.Clips.Count, plan.Clips.Count == 1 ? "" : "s", Engine.MinSecs);
            t.AppendFormat("\r\n{0} files move to:\r\n   {1}\r\n\r\n", plan.Count, Versions.SpareDir);
            t.Append("Nothing is deleted. To use a moved file again, put it back in your music folder and press RESCAN FOLDER.\r\n");
            t.Append("Tags on a moved version go to the version that stays.");
            using (var d = new TidyDialog(this, justAdded ? "SEVERAL VERSIONS OF A SONG" : "TIDY MY MUSIC", t.ToString(),
                justAdded ? "KEEP ONE OF EACH" : "TIDY", justAdded ? "KEEP THEM ALL" : "CANCEL"))
                return d.ShowDialog(this) == DialogResult.OK;
        }

        // the pop-up, in the window's own style
        class TidyDialog : Form
        {
            public TidyDialog(LoaderForm owner, string title, string text, string yes, string no)
            {
                FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; TopMost = true;
                BackColor = Bg; ForeColor = TextC; Font = owner.fBody; Padding = new Padding(1);
                Size = new Size(Math.Max(560, (int)(owner.Width * 0.92)), Math.Max(420, (int)(owner.Height * 0.9)));
                Paint += (s, e) => e.Graphics.DrawRectangle(new Pen(Rule), 0, 0, Width - 1, Height - 1);
                var head = new Panel { Dock = DockStyle.Top, Height = 50, BackColor = Bg };
                head.Paint += (s, e) =>
                {
                    e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    Spaced(e.Graphics, title, owner.fTitle, TextC, head.Width / 2f, 12, 4, true);
                    RuleLine(e.Graphics, 40, head.Width - 40, head.Height - 6, true);
                };
                var holder = new Panel { Dock = DockStyle.Fill, BackColor = PanelC, Padding = new Padding(0) };
                var body = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BackColor = PanelC, ForeColor = TextC,
                    BorderStyle = BorderStyle.None, Font = owner.fBody, Text = text, Location = new Point(18, 12), TabStop = false };
                holder.Resize += (s, e) => body.Size = new Size(holder.Width - 24, holder.Height - 20);
                holder.Controls.Add(body);
                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(14, 12, 4, 0), BackColor = Bg };
                var noB = owner.MakeButton(no); noB.DialogResult = DialogResult.Cancel;
                var yesB = owner.MakeButton(yes); yesB.DialogResult = DialogResult.OK;
                buttons.Controls.Add(noB); buttons.Controls.Add(yesB);
                var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 0), BackColor = Bg };
                frame.Controls.Add(holder);
                Controls.Add(frame); Controls.Add(buttons); Controls.Add(head);
                AcceptButton = yesB; CancelButton = noB;
                head.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
                HandleCreated += (s, e) => DarkScrollBars(body);
                Shown += (s, e) => { body.SelectionLength = 0; yesB.Focus(); };
            }
        }

        string[] lastInto = new string[0];   // the playlists the last add put songs into

        // The game keeps the player's favourites (song titles) in Data\SKSE\Plugins\StorageUtilData\SongStone\Favourites.json,
        // which a fresh Skyrim install ("nuking" the game folder) wipes. Every start keeps a copy next to the music folder
        // and puts it back when the game's copy is gone.
        void KeepFavourites()
        {
            try
            {
                string sky = Engine.FindSkyrim();
                if (sky == null) return;
                string game = Path.Combine(sky, @"Data\SKSE\Plugins\StorageUtilData\SongStone\Favourites.json");
                string kept = Path.Combine(Settings.Folder, "Favourites.json");
                string docs = Path.Combine(Tags.BackupDir, "Favourites backup.json");   // Documents: often backed up by OneDrive
                if (File.Exists(game)) { File.Copy(game, kept, true); try { Directory.CreateDirectory(Tags.BackupDir); File.Copy(game, docs, true); } catch { } }
                else if (!File.Exists(kept) && File.Exists(docs)) { Directory.CreateDirectory(Path.GetDirectoryName(game)); File.Copy(docs, game); AddLog("Put your favourites back into the game (from Documents)."); }
                else if (File.Exists(kept))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(game));
                    File.Copy(kept, game);
                    AddLog("Put your favourites back into the game.");
                }
            }
            catch (Exception ex) { AddLog("! favourites: " + ex.Message); }
        }

        // A .zip (album downloads and music packs come zipped) goes in like a dropped folder: its songs become a playlist
        // named after the zip. A zip holding a single song goes into the single-songs playlist like a loose song.
        // Only music files come out (no pictures, text or Mac junk), each by its own name - never a path from inside the zip.
        int AddZip(string zip, List<string> into)
        {
            SetStatus("Unzipping " + Path.GetFileName(zip) + "...");
            drop.Update();
            string tmp = Path.Combine(Path.GetTempPath(), "SongStoneZip-" + Guid.NewGuid().ToString("N"));
            try
            {
                var songs = new List<string>();
                using (var z = ZipFile.OpenRead(zip))
                    foreach (var en in z.Entries)
                    {
                        string file = string.Concat(en.Name.Split(Path.GetInvalidFileNameChars()));
                        if (file.Length == 0 || en.FullName.IndexOf("__MACOSX", StringComparison.OrdinalIgnoreCase) >= 0 || !Engine.IsMusic(file)) continue;
                        string dir = Path.Combine(tmp, songs.Count.ToString());   // a folder each: albums inside can share song names
                        Directory.CreateDirectory(dir);
                        en.ExtractToFile(Path.Combine(dir, file));
                        songs.Add(Path.Combine(dir, file));
                    }
                if (songs.Count == 0)
                {
                    AddLog("- no songs in " + Path.GetFileName(zip));
                    SetStatus("No songs in " + Path.GetFileName(zip) + ".");
                    return 0;
                }
                string name = SafeName(songs.Count == 1 ? combo.Text : Path.GetFileNameWithoutExtension(zip));
                int n = songs.Sum(s => CopySong(s, Path.Combine(musicDir, name)));
                AddLog(string.Format("+ {0} song{1} from {2} into playlist '{3}'", n, n == 1 ? "" : "s", Path.GetFileName(zip), name) + (songs.Count > n ? string.Format(" ({0} were already there)", songs.Count - n) : ""));
                if (n > 0 && !into.Contains(name)) into.Add(name);
                if (n == 0) SetStatus("Nothing new in " + Path.GetFileName(zip) + ".");
                return n;
            }
            catch (Exception ex)   // a damaged zip, or one still downloading
            {
                AddLog("! " + Path.GetFileName(zip) + " can't be opened (" + ex.Message + ") - is it damaged or still downloading?");
                SetStatus("Can't open " + Path.GetFileName(zip) + " - is it damaged or still downloading?");
                return 0;
            }
            finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { } }
        }

        int AddPaths(string[] paths)
        {
            int added = 0;
            var into = new List<string>();
            foreach (var p in paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        string name = SafeName(Path.GetFileName(p.TrimEnd('\\')));
                        var songs = Directory.GetFiles(p, "*", SearchOption.AllDirectories).Where(Engine.IsMusic).ToList();
                        int n = songs.Sum(s => CopySong(s, Path.Combine(musicDir, name)));
                        AddLog(string.Format("+ {0} songs into playlist '{1}'", n, name) + (songs.Count > n ? string.Format(" ({0} were already there)", songs.Count - n) : ""));
                        added += n;
                        if (n > 0 && !into.Contains(name)) into.Add(name);
                    }
                    else if (File.Exists(p))
                    {
                        string ext = Path.GetExtension(p).ToLowerInvariant();
                        if (ext == ".zip") { added += AddZip(p, into); continue; }
                        if (ext == ".7z" || ext == ".rar")
                        {
                            AddLog("- " + Path.GetFileName(p) + ": unzip it first (right-click it > Extract All, or use 7-Zip), then drop the folder here");
                            SetStatus("Unzip " + Path.GetFileName(p) + " first, then drop the folder here.");
                            continue;
                        }
                        if (!Engine.IsMusic(p)) { AddLog("- skipped " + Path.GetFileName(p) + " (not a music file)"); continue; }
                        string name = SafeName(combo.Text);
                        int n = CopySong(p, Path.Combine(musicDir, name));
                        AddLog(n > 0 ? string.Format("+ {0} into '{1}'", Path.GetFileName(p), name) : string.Format("= {0} is already in '{1}'", Path.GetFileName(p), name));
                        added += n;
                        if (n > 0 && !into.Contains(name)) into.Add(name);
                    }
                }
                catch (Exception ex) { AddLog("! " + Path.GetFileName(p) + ": " + ex.Message); }
            }
            RefreshList();
            if (added > 0) SetStatus(string.Format("Added {0} song{1} to '{2}' - converting...", added, added == 1 ? "" : "s", string.Join("', '", into)));
            lastInto = into.ToArray();
            return added;
        }

        // ---------------------------------------------------------------- converting (on a worker thread)
        void Convert()
        {
            if (worker != null && worker.IsAlive) { again = true; SetStatus("Converting... (your new songs are next)"); return; }
            string skyrim = Engine.FindSkyrim();
            if (skyrim == null)
            {
                using (var dlg = new FolderBrowserDialog { Description = "Where is Skyrim Special Edition installed? (the folder with SkyrimSE.exe)" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK || !File.Exists(Path.Combine(dlg.SelectedPath, "SkyrimSE.exe"))) { AddLog("Skyrim Special Edition wasn't found."); return; }
                    skyrim = dlg.SelectedPath;
                    Settings.Set("skyrim", skyrim);
                }
            }
            engine = new Engine { Log = AddLog, Progress = s => BeginInvoke(new Action(() => SetStatus(s))) };
            if (!status.StartsWith("Added")) SetStatus("Checking songs...");
            AddLog("--- converting");
            worker = new Thread(() =>
            {
                string result = null;
                try { result = engine.Run(musicDir, skyrim); }
                catch (Exception ex) { AddLog("! " + ex.Message); }
                BeginInvoke(new Action(() => Finished(result)));
            }) { IsBackground = true };
            worker.Start();
        }

        void Finished(string result)
        {
            RefreshList();
            if (songsMode) RefreshSongs();
            if (result != null)
            {
                SetStatus("Done: " + result);
                Chime.Play();
                AddLog(result);
                if (Process.GetProcessesByName("SkyrimSE").Length > 0) AddLog("Skyrim is running: in game, open Mod Configuration > Ayleid Song Stone and press Reload songs.");
                else AddLog("Done. Start the game and ask an innkeeper about the song stone.");
            }
            else if (engine == null || !engine.Cancel) SetStatus("Stopped - see the notes on the right.");
            if (again) { again = false; Convert(); }
        }

        // ---------------------------------------------------------------- drag and drop, closing
        void OnDragEnter(object s, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; hover = true; drop.Invalidate(); }
            else e.Effect = DragDropEffects.None;
            DragImage.Enter(Handle, e);
        }

        void OnDragDrop(object s, DragEventArgs e)
        {
            hover = false; drop.Invalidate();
            DragImage.Drop(e);
            var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null) return;
            Cursor = Cursors.WaitCursor;
            int n = AddPaths(paths);
            Cursor = Cursors.Default;
            if (n > 0) BeginInvoke(new Action(() => AddedSongs(n)));   // after the drop returns, so Explorer isn't held while the pop-up shows
            else if (status == "Ready.") SetStatus("Nothing new to add.");
        }

        void OnClosing(object s, FormClosingEventArgs e)
        {
            StopPlay();
            if (tagTimer.Enabled && (worker == null || !worker.IsAlive))   // tags clicked just before closing: update the stone's list first
            {
                tagTimer.Stop();
                Cursor = Cursors.WaitCursor;
                try { var sky = Engine.FindSkyrim(); if (sky != null) new Engine().Run(musicDir, sky); } catch { }
            }
            if (worker == null || !worker.IsAlive) return;
            if (MessageBox.Show(this, "Still converting. Stop and close? (Run it again later to finish.)", "Song Stone Loader", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
            { e.Cancel = true; return; }
            engine.Stop();
            worker.Join(3000);
        }
    }
}
