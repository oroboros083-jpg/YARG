using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YARG.Gameplay;
using YARG.Helpers;
using YARG.Venue.VenueCamera;
using YARG.Venue.Characters;

#if UNITY_EDITOR
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
#endif

namespace YARG.Venue
{
    public class BundleBackgroundManager : MonoBehaviour
    {
        // DO NOT CHANGE THIS! It will break existing venues
        public const string BACKGROUND_PREFAB_PATH = "Assets/_Background.prefab";
        public const string CHARACTER_PREFAB_PATH = "Assets/_Character.prefab";
        public const string BACKGROUND_SHADER_BUNDLE_NAME = "_metal_shaders.bytes";
        public const string CHARACTER_SHADER_BUNDLE_NAME = "_character_metal_shaders.bytes";
        public const string BACKGOUND_OSX_MATERIAL_PREFIX = "_metal_";
        public const string BUNDLE_OSX_SUFFIX = "_metal.bytes";

        private const string VENUE_LAYER_NAME = "Venue";

        private int _venueLayerNumber = -1;

        // DO NOT CHANGE the name of this! I *know* it doesn't follow naming conventions, but it will also break existing
        // venues if we do change it.
        //
        // ReSharper disable once InconsistentNaming
        [SerializeField]
        private Camera mainCamera;

        [Space]
        [SerializeField]
        private VenueCharacter _replaceableVocalist;

        public AssetBundle       Bundle        { get; set; }
        public List<AssetBundle> ShaderBundles { get; set; } = new();
        public List<AssetBundle> CharacterBundles { get; set; } = new();

        private void Awake()
        {
            // Move object out of the way, so its effects don't collide with the tracks
            transform.position += Vector3.forward * 10_000f;
            _venueLayerNumber = LayerMask.NameToLayer(VENUE_LAYER_NAME);
        }

        public void SetupVenueCamera(GameObject bgInstance)
        {
            // If venue has a CameraManager, don't add VenueCameraRenderer, it will be taken care of
            if (bgInstance.GetComponentInChildren<CameraManager>() == null)
            {
                mainCamera.gameObject.AddComponent<VenueCameraRenderer>();
            }
        }

        public void LimitVenueLights(GameObject bgInstance)
        {
            if (_venueLayerNumber == -1)
            {
                return;
            }

            int venueLayer = 1 << _venueLayerNumber;

            Light[] lights = bgInstance.GetComponentsInChildren<Light>(true);

            foreach (var light in lights)
            {
                light.cullingMask = venueLayer;
            }
        }

        private void OnDestroy()
        {
            if (Bundle != null)
            {
                Bundle.Unload(true);
            }

            if (ShaderBundles.Count > 0)
            {
                foreach (var bundle in ShaderBundles)
                {
                    if (bundle != null)
                    {
                        bundle.Unload(true);
                    }
                }

                ShaderBundles.Clear();
            }

            if (CharacterBundles.Count > 0)
            {
                foreach (var bundle in CharacterBundles)
                {
                    if (bundle != null)
                    {
                        bundle.Unload(true);
                    }
                }

                CharacterBundles.Clear();
            }
        }

#if UNITY_EDITOR

        //
        // HUGE thanks to the people over at Trombone Champ and NyxTheShield for giving us this code.
        // This could not be done without them.
        //
        // Code to export a background from the editor.
        //

        private void Export(GameObject root, BackgroundHelper.ExportType type)
        {
            string defaultName = type == BackgroundHelper.ExportType.Character ? "character" : "bg";
            string extension = type == BackgroundHelper.ExportType.Character ? "yargchar" : "yarground";
            string title = type == BackgroundHelper.ExportType.Character ? "Export Character" : "Export Background";
            string path = EditorUtility.SaveFilePanel(title, string.Empty, defaultName, extension);

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                // The context menus have always collected shaders from the whole venue, even for a character
                ExportBundle(root, type, path, gameObject, requireMetalShaders: true);
                EditorUtility.DisplayDialog("Export Successful!", "Export Successful!", "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Export Unsuccessful", "Export failed. See console for more info.", "OK");
            }
        }

        /// <summary>
        /// Builds <paramref name="root"/> into a .yargchar or .yarground at <paramref name="path"/> without any
        /// dialogs, so batch-mode tools can call it. Throws on failure.
        /// </summary>
        /// <param name="shaderRoot">
        /// The object whose materials anchor the MacOS Metal shader bundle; defaults to <paramref name="root"/>.
        /// </param>
        /// <param name="requireMetalShaders">
        /// When false, a missing MacOS build module skips the Metal shader bundle with a warning (the export then
        /// renders everywhere but MacOS) instead of failing.
        /// </param>
        public static void ExportBundle(GameObject root, BackgroundHelper.ExportType type, string path,
            GameObject shaderRoot = null, bool requireMetalShaders = false)
        {
            if (shaderRoot == null)
            {
                shaderRoot = root;
            }

            string fileName = Path.GetFileName(path);
            var metalAssetBundleName = fileName + BUNDLE_OSX_SUFFIX;
            var backgroundPath = BackgroundHelper.ExportType.Character == type ? CHARACTER_PREFAB_PATH : BACKGROUND_PREFAB_PATH;
            var assetPaths = new List<string>();

            GameObject clonedBackground = null;

            AssetDatabase.DisallowAutoRefresh();

            try
            {
                // First we'll collect all shaders and build a separate bundle out of them
                // for Mac as no other build target will include Metal shaders
                // And we want our background to work everywhere
                if (BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneOSX))
                {
                    if (BuildMetalShaderBundle(shaderRoot, metalAssetBundleName))
                    {
                        assetPaths.Add(Path.Combine("Assets/", metalAssetBundleName));
                    }
                }
                else if (requireMetalShaders)
                {
                    throw new FileNotFoundException(MISSING_MAC_MODULE_MESSAGE);
                }
                else
                {
                    Debug.LogWarning($"MacOS Build Support is not installed, so {fileName} has no Metal shaders and will not render on MacOS.");
                }

                assetPaths.Add(backgroundPath);

                clonedBackground = Instantiate(root);

                // We must examine anything that has the VenueCharacter component so we can deal with animations
                // properly. First we find them, then check for an AnimatorController and extract the layers and
                // animation states contained within. Then we assign them to a SerializedField on the VenueCharacter,
                // which will hopefully end up on the character when the AssetBundle is built.

                var characterComponents = clonedBackground.GetComponentsInChildren<VenueCharacter>();

                foreach (var character in characterComponents)
                {
                    var animator = character.GetComponent<Animator>();
                    if (animator == null)
                    {
                        continue;
                    }

                    // This should work since we're in the editor
                    var controller = animator.runtimeAnimatorController as AnimatorController;
                    if (controller == null)
                    {
                        continue;
                    }

                    var layerStates = new AnimationDictionary();
                    foreach (var layer in controller.layers)
                    {
                        var layerName = layer.name;
                        foreach (var state in layer.stateMachine.states)
                        {
                            layerStates.Add(layerName, state.state.name);
                        }
                    }

                    character.LayerStates = layerStates;
                }

                PrefabUtility.SaveAsPrefabAsset(clonedBackground.gameObject, backgroundPath);

                AssetBundleBuild assetBundleBuild = default;
                assetBundleBuild.assetBundleName = fileName;
                assetBundleBuild.assetNames = assetPaths.ToArray();

                BuildPipeline.BuildAssetBundles(Application.temporaryCachePath,
                    new[]
                    {
                        assetBundleBuild
                    }, BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    BuildTarget.StandaloneWindows);

                // Unity seems to save the file in lower case, which is a problem on Linux, as file systems are case sensitive there
                var builtPath = Path.Combine(Application.temporaryCachePath, fileName.ToLowerInvariant());
                if (!File.Exists(builtPath))
                {
                    throw new FileNotFoundException($"The asset bundle build did not produce {builtPath}. See console for more info.");
                }

                // If the file exists, delete it (to replace it)
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                File.Move(builtPath, path);
            }
            finally
            {
                foreach (var asset in assetPaths)
                {
                    AssetDatabase.DeleteAsset(asset);
                }

                AssetDatabase.AllowAutoRefresh();
                if (clonedBackground != null)
                {
                    DestroyImmediate(clonedBackground);
                }
            }
        }

        private const string MISSING_MAC_MODULE_MESSAGE =
            "MacOS Shader bundle failed to build. <a href=\"https://wiki.yarg.in/wiki/Venue_Creation\">Please ensure you have the \"MacOS Build Support (Mono)\" module installed.</a>";

        // Builds the MacOS Metal shader bundle into Assets/<metalAssetBundleName>; returns false if there were no materials
        private static bool BuildMetalShaderBundle(GameObject shaderRoot, string metalAssetBundleName)
        {
            // We use materials as "anchors" to make sure all required
            // shader variants are included
            var materialAssets = EditorUtility.CollectDependencies(new[] { shaderRoot })
                .OfType<Material>() // Only material dependencices
                .Select((mat, i) =>
                {
                    // Create a clone
                    var matClone = new Material(mat);
                    // Avoid name collision
                    matClone.name = BACKGOUND_OSX_MATERIAL_PREFIX + i.ToString() + mat.name;
                    // Drop all textures to not double resulting yarground in size
                    if (matClone.mainTexture != null)
                    {
                        matClone.mainTexture = Texture2D.whiteTexture;
                    }
                    foreach (var id in matClone.GetTexturePropertyNameIDs())
                    {
                        if (matClone.GetTexture(id) != null)
                        {
                            matClone.SetTexture(id, Texture2D.whiteTexture);
                        }
                    }
                    var assetPath = Path.Combine("Assets", matClone.name + ".mat");
                    AssetDatabase.CreateAsset(matClone, assetPath);

                    return assetPath;
                })
                .ToArray();

            try
            {
                if (materialAssets.Length == 0)
                {
                    return false;
                }

                var shaderAssets = EditorUtility.CollectDependencies(new[] { shaderRoot })
                    .OfType<Shader>().Select(AssetDatabase.GetAssetPath);

                var metalAssetBundleBuild = default(AssetBundleBuild);
                metalAssetBundleBuild.assetBundleName = metalAssetBundleName;
                metalAssetBundleBuild.assetNames = materialAssets.Concat(shaderAssets).ToArray();

                BuildPipeline.BuildAssetBundles(Application.temporaryCachePath,
                    new[]
                    {
                        metalAssetBundleBuild
                    }, BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    BuildTarget.StandaloneOSX);

                var filePath = Path.Combine(Application.temporaryCachePath, metalAssetBundleName);

                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException(MISSING_MAC_MODULE_MESSAGE);
                }

                var assetPath = Path.Combine(Application.dataPath, metalAssetBundleName);
                if (File.Exists(assetPath))
                {
                    File.Delete(assetPath);
                }
                File.Move(filePath, assetPath);
                AssetDatabase.ImportAsset(Path.Combine("Assets", metalAssetBundleName));
                return true;
            }
            finally
            {
                // Now delete our material clones
                foreach (var assetPath in materialAssets)
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
            }
        }

        [ContextMenu("Export Vocalist")]
        public void ExportCharacter()
        {
            var vocalist = _replaceableVocalist.gameObject;

            Export(vocalist, BackgroundHelper.ExportType.Character);
        }

        [ContextMenu("Export Background")]
        public void ExportBackground()
        {
            Export(gameObject, BackgroundHelper.ExportType.Background);
        }
#endif
    }
}
