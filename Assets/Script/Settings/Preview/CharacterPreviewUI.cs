using TMPro;
using UnityEngine;
using UniVRM10;
using YARG.Settings.Metadata;
using YARG.Settings.Types;

namespace YARG.Settings.Preview
{
    public class CharacterPreviewUI : MonoBehaviour
    {
        [SerializeField]
        private TextMeshProUGUI _characterName;
        [SerializeField]
        private TextMeshProUGUI _characterAuthor;

        public void Initialize(GameObject character)
        {
            if (character == null)
            {
                _characterName.SetText(string.Empty);
                _characterAuthor.SetText(string.Empty);
                return;
            }

            var vrmInstance = character.GetComponent<Vrm10Instance>();

            // A plain character has no VRM meta, so it is named after its file and has no author
            if (vrmInstance == null || vrmInstance.Vrm == null || vrmInstance.Vrm.Meta == null)
            {
                _characterName.SetText(CustomCharacterSetting.GetDisplayName(vrmInstance,
                    CharacterPreviewBuilder.CharacterFile ?? string.Empty));
                _characterAuthor.SetText(string.Empty);
                return;
            }

            var authors = vrmInstance.Vrm.Meta.Authors;

            var author = authors.Count switch
            {
                0 => "",
                1 => authors[0],
                _ => string.Join(", ", authors)
            };

            _characterName.SetText(vrmInstance.Vrm.Meta.Name);
            _characterAuthor.SetText(author);
        }

        public void Disable()
        {
            _characterName.SetText(string.Empty);
            _characterAuthor.SetText(string.Empty);
        }
    }
}