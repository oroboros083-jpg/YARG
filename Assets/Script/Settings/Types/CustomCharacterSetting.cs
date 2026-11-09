using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UniVRM10;
using YARG.Settings.Customization;
using YARG.Venue;
using YARG.Venue.Characters;

namespace YARG.Settings.Types
{
    public class CustomCharacterSetting : DropdownSetting<string>
    {
        private VenueCharacter.CharacterType _characterType;
        private Dictionary<string, string>   _fileToName = new();

        private const string CHARACTER_FOLDER = "characters";

        // Every slot's setting scans the same folder at startup, so each file's bundle is opened once and its type and name shared
        private static readonly Dictionary<string, (DateTime stamp, VenueCharacter.CharacterType? type, string name)> _fileInfoCache = new();

        public string CustomCharacterPath
        {
            get
            {
                var folder = Path.Combine(CustomContentManager.CustomizationDirectory, CHARACTER_FOLDER);

                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                return folder;
            }
        }

        public CustomCharacterSetting(string value, VenueCharacter.CharacterType characterType, Action<string> onChange = null) :
            base(value, onChange, localizable: false)
        {
            _characterType = characterType;
        }

        public override void UpdateValues()
        {
            _fileToName.Clear();
            _possibleValues.Clear();
            _possibleValues.Add(string.Empty);

            var folder = CustomCharacterPath;
            string[] files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.yargchar") : Array.Empty<string>();

            foreach (var file in files)
            {
                var (type, name) = ReadCharacterInfo(file);
                if (type == _characterType)
                {
                    _possibleValues.Add(file);
                    _fileToName[file] = name;
                }
            }
        }

        // Loads the AssetBundle and pulls the character's type and its name from the VrmInstance (the file name is the fallback display name)
        private static (VenueCharacter.CharacterType? type, string name) ReadCharacterInfo(string file)
        {
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_fileInfoCache.TryGetValue(file, out var cached) && cached.stamp == stamp)
            {
                return (cached.type, cached.name);
            }

            VenueCharacter.CharacterType? type = null;
            string name = null;

            var bundle = AssetBundle.LoadFromFile(file);
            if (bundle != null)
            {
                var character = bundle.LoadAsset<GameObject>(BundleBackgroundManager.CHARACTER_PREFAB_PATH.ToLowerInvariant());
                if (character != null)
                {
                    // A plain VenueCharacter (no VRM) is a valid character too; it just has no meta to name it
                    name = GetDisplayName(character.GetComponent<Vrm10Instance>(), file);

                    var venueCharacter = character.GetComponent<VenueCharacter>();
                    if (venueCharacter != null)
                    {
                        type = venueCharacter.Type;
                    }
                }

                bundle.Unload(true);
            }

            _fileInfoCache[file] = (stamp, type, name);
            return (type, name);
        }

        public static string GetDisplayName(Vrm10Instance vrmInstance, string file)
        {
            if (vrmInstance != null && vrmInstance.Vrm != null && vrmInstance.Vrm.Meta != null &&
                !string.IsNullOrEmpty(vrmInstance.Vrm.Meta.Name))
            {
                return vrmInstance.Vrm.Meta.Name;
            }

            return Path.GetFileNameWithoutExtension(file);
        }

        public override string ValueToString(string value)
        {
            return _fileToName.GetValueOrDefault(value, "None");
        }
    }
}