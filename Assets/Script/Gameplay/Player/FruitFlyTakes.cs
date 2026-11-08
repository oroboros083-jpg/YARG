using System;
using System.IO;
using YARG.Core.Engine.Drums;
using YARG.Core.Logging;
using YARG.Core.Song;

namespace YARG.Gameplay.Player
{
    /// <summary>
    /// Finds the takes fly-brain rehearses for each song, at
    /// <c>&lt;rehearsals root&gt;/&lt;song folder name&gt;/&lt;instrument&gt;.json</c>.
    /// </summary>
    public static class FruitFlyTakes
    {
        // FLYBRAIN_REHEARSALS overrides the default, which is where fly-brain writes rehearsals on Andrew's machine.
        public static string RehearsalsRoot =>
            Environment.GetEnvironmentVariable("FLYBRAIN_REHEARSALS") ?? @"P:\CSW\fly-brain\rehearsals";

        public static string SongFolderName(SongEntry song)
        {
            var location = song.SortBasedLocation.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // A packed .sng song is a file, not a folder; its name without the extension is its folder name.
            return File.Exists(location) ? Path.GetFileNameWithoutExtension(location) : Path.GetFileName(location);
        }

        public static string TakePath(SongEntry song, string instrument) =>
            Path.Combine(RehearsalsRoot, SongFolderName(song), instrument + ".json");

        public static FlyTake LoadOrEmpty(SongEntry song, string instrument)
        {
            var path = TakePath(song, instrument);
            if (!File.Exists(path))
            {
                YargLogger.LogFormatWarning("No fruit fly take at {0}; the fly will sit this song out.", path);
                return FlyTake.Empty;
            }

            try
            {
                var take = FlyTake.Load(path);
                YargLogger.LogFormatInfo("Loaded fruit fly take {0} ({1} strikes).", path, take.Strikes.Count);
                return take;
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, $"Could not read fruit fly take {path}.");
                return FlyTake.Empty;
            }
        }
    }
}
