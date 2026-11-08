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

            // Load the AssetBundles and pull the character names from the VrmInstance (and use the filename as a fallback for the display name)
            foreach (var file in files)
            {
                var bundle = AssetBundle.LoadFromFile(file);
                if (bundle == null)
                {
                    continue;
                }

                var character = bundle.LoadAsset<GameObject>(BundleBackgroundManager.CHARACTER_PREFAB_PATH.ToLowerInvariant());
                if (character == null)
                {
                    bundle.Unload(true);
                    continue;
                }

                // A plain VenueCharacter (no VRM) is a valid character too; it just has no meta to name it
                var vrmInstance = character.GetComponent<Vrm10Instance>();
                var name = GetDisplayName(vrmInstance, file);

                var venueCharacter = character.GetComponent<VenueCharacter>();
                if (venueCharacter != null && venueCharacter.Type == _characterType)
                {
                    _possibleValues.Add(file);
                    _fileToName[file] = name;
                }

                bundle.Unload(true);
            }
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