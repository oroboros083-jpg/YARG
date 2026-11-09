using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using YARG.Helpers;
using YARG.Venue;
using YARG.Venue.Characters;
using AnimationStateType = YARG.Venue.Characters.VenueCharacter.AnimationStateType;

namespace YARG.Editor.FlyBand
{
    /// <summary>
    /// Turns the fly band's Blender exports (fly_&lt;slot&gt;.json manifest, .fbx, albedo and mask .png) into
    /// VenueCharacter prefabs and exports each as a .yargchar; with -flyVenue it also builds the stage around them
    /// into flyband.yarground (FlyBandBuilder.Venue.cs).
    ///
    /// Batch: Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod YARG.Editor.FlyBand.FlyBandBuilder.BuildFromCommandLine
    ///        -flyManifests &lt;folder of fly_*.json&gt; [-flyOut &lt;folder for .yargchar and .yarground&gt;]
    ///        [-flyVenue &lt;stage.json&gt;] [-flyCheck]
    /// </summary>
    public static partial class FlyBandBuilder
    {
        public const string ASSET_ROOT = "Assets/Authoring/Venue/Unique/FlyBand";
        public const string DEFAULT_MANIFEST_DIR = @"P:\output\flyband\yarg";

        private const string BASE_LAYER = "Base";
        private const string SPEED_PARAMETER = "SpeedAdjustment";

        // Fixed crossfade back to a layer's hold state once a one-shot (a strum, a hit) has played out
        private const float RETURN_TRANSITION_SECONDS = 0.05f;

        [MenuItem("YARG/Fly Band/Build Characters From Manifests...")]
        public static void BuildFromMenu()
        {
            var dir = EditorUtility.OpenFolderPanel("Fly band manifest folder", DEFAULT_MANIFEST_DIR, string.Empty);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            try
            {
                var built = Build(dir, dir);
                EditorUtility.DisplayDialog("Fly band", "Built:\n" + string.Join("\n", built), "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Fly band", "Build failed. See console for more info.", "OK");
            }
        }

        public static void BuildFromCommandLine()
        {
            var manifestDir = GetArg("-flyManifests") ?? DEFAULT_MANIFEST_DIR;
            var outDir = GetArg("-flyOut") ?? manifestDir;
            var venueManifest = GetArg("-flyVenue");
            bool check = Environment.GetCommandLineArgs().Contains("-flyCheck");

            int exitCode = 0;
            try
            {
                var built = Build(manifestDir, outDir);
                Debug.Log("[FlyBand] Built " + string.Join(", ", built));

                // The venue places the character prefabs just built, so it always comes after them
                string venuePath = null;
                if (venueManifest != null)
                {
                    venuePath = Path.Combine(outDir, VENUE_FILE_NAME);
                    BuildVenue(venueManifest, venuePath);
                }

                if (check)
                {
                    bool ok = FlyBandCheck.CheckAll(manifestDir, outDir);
                    if (venuePath != null)
                    {
                        ok &= FlyBandCheck.CheckVenue(venueManifest, venuePath);
                    }
                    exitCode = ok ? 0 : 2;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                exitCode = 1;
            }

            EditorApplication.Exit(exitCode);
        }

        /// <summary>Builds every fly_*.json in <paramref name="manifestDir"/>; returns the .yargchar paths written.</summary>
        public static List<string> Build(string manifestDir, string outDir)
        {
            var manifests = Directory.GetFiles(manifestDir, "fly_*.json").OrderBy(f => f).ToArray();
            if (manifests.Length == 0)
            {
                throw new FileNotFoundException($"No fly_*.json manifests in {manifestDir}");
            }

            Directory.CreateDirectory(outDir);

            var written = new List<string>();
            foreach (var manifestPath in manifests)
            {
                var manifest = FlyManifest.Load(manifestPath);
                var prefab = BuildCharacter(manifest);

                var outPath = Path.Combine(outDir, $"fly_{manifest.Slot}.yargchar");
                BundleBackgroundManager.ExportBundle(prefab, BackgroundHelper.ExportType.Character, outPath);
                Debug.Log($"[FlyBand] Wrote {outPath}");
                written.Add(outPath);
            }

            return written;
        }

        public static string SlotFolder(string slot) => $"{ASSET_ROOT}/{slot}";
        public static string PrefabPath(string slot) => $"{SlotFolder(slot)}/fly_{slot}.prefab";

        /// <summary>Imports one fly's files and builds its materials, masks, controller and prefab; returns the prefab.</summary>
        public static GameObject BuildCharacter(FlyManifest manifest)
        {
            var slot = manifest.Slot;
            var folder = SlotFolder(slot);
            EnsureFolder(folder);

            var fbxPath = CopyIn(manifest.Resolve(manifest.Fbx), folder);
            var albedoPath = CopyIn(manifest.Resolve(manifest.Albedo), folder);
            var maskPath = CopyIn(manifest.Resolve(manifest.Mask), folder);

            ConfigureTexture(albedoPath, true);
            ConfigureTexture(maskPath, false);

            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath);
            var mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);

            var bodyMaterial = BuildMaterial($"{folder}/fly_{slot}_body.mat", albedo, mask, false);
            var wingMaterial = BuildMaterial($"{folder}/fly_{slot}_wing.mat", albedo, mask, true);

            var materials = new Dictionary<string, Material>
            {
                { $"fly_{slot}_body", bodyMaterial },
                { $"fly_{slot}_wing", wingMaterial },
            };

            ConfigureModel(fbxPath, manifest, materials);

            var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .ToDictionary(c => c.name);

            var missing = manifest.States.Keys.Where(s => !clips.ContainsKey(s)).ToList();
            if (missing.Count > 0)
            {
                throw new InvalidDataException(
                    $"{fbxPath} has no take for state(s) {string.Join(", ", missing)}; it has {string.Join(", ", clips.Keys)}");
            }

            foreach (var clip in clips.Values.Where(c => Mathf.Abs(c.frameRate - manifest.Fps) > 0.01f))
            {
                Debug.LogWarning($"[FlyBand] {fbxPath}: clip {clip.name} runs at {clip.frameRate} fps, manifest says {manifest.Fps}; set the Blender scene fps before export");
            }

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();

            var masks = BuildMasks(folder, slot, model, manifest);
            var controller = BuildController($"{folder}/fly_{slot}.controller", manifest, clips, masks);

            return BuildPrefab(PrefabPath(slot), manifest, model, avatar, controller, materials);
        }

        private static string CopyIn(string source, string folder)
        {
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("Manifest names a file that does not exist", source);
            }

            var assetPath = $"{folder}/{Path.GetFileName(source)}";
            File.Copy(source, Path.GetFullPath(assetPath), true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        private static void ConfigureTexture(string path, bool sRGB)
        {
            var importer = (TextureImporter) AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = sRGB;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        private static Material BuildMaterial(string path, Texture2D albedo, Texture2D mask, bool wing)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetTexture("_BaseMap", albedo);
            material.SetTexture("_MetallicGlossMap", mask);
            material.SetFloat("_WorkflowMode", 1f); // metallic
            material.SetFloat("_Metallic", 1f);
            material.SetFloat("_Smoothness", 1f); // scales the mask's alpha
            material.SetFloat("_SmoothnessTextureChannel", 0f); // smoothness from the metallic map's alpha

            // The wings are see-through at alpha 0.6 (Blender's look), so they render alpha-blended and double-sided
            material.SetColor("_BaseColor", new Color(1f, 1f, 1f, wing ? 0.6f : 1f));
            material.SetFloat("_Surface", wing ? 1f : 0f);
            material.SetFloat("_Blend", 0f); // alpha
            material.SetFloat("_Cull", wing ? 0f : 2f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_ZWrite", wing ? 0f : 1f);

            ValidateUrpMaterial(material);

            EditorUtility.SetDirty(material);
            return material;
        }

        // Lets URP's own Lit ShaderGUI set keywords, blend modes and the render queue from the properties above
        private static void ValidateUrpMaterial(Material material)
        {
            var guiType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Rendering.Universal.ShaderGUI.LitShader", false))
                .FirstOrDefault(t => t != null);

            if (guiType != null && Activator.CreateInstance(guiType) is ShaderGUI gui)
            {
                gui.ValidateMaterial(material);
                return;
            }

            Debug.LogWarning("[FlyBand] URP LitShader GUI not found; setting the material keywords by hand");
            bool transparent = material.GetFloat("_Surface") > 0.5f;
            material.EnableKeyword("_METALLICSPECGLOSSMAP");
            if (transparent)
            {
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetFloat("_SrcBlend", (float) UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float) UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.renderQueue = (int) UnityEngine.Rendering.RenderQueue.Transparent;
                material.SetOverrideTag("RenderType", "Transparent");
            }
            else
            {
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.SetFloat("_SrcBlend", (float) UnityEngine.Rendering.BlendMode.One);
                material.SetFloat("_DstBlend", (float) UnityEngine.Rendering.BlendMode.Zero);
                material.renderQueue = (int) UnityEngine.Rendering.RenderQueue.Geometry;
                material.SetOverrideTag("RenderType", "Opaque");
            }
        }

        private static void ConfigureModel(string fbxPath, FlyManifest manifest, Dictionary<string, Material> materials)
        {
            var importer = (ModelImporter) AssetImporter.GetAtPath(fbxPath);

            importer.animationType = ModelImporterAnimationType.Generic;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true;
            importer.animationCompression = ModelImporterAnimationCompression.Off; // the hand holds are 2-frame poses
            importer.importBlendShapes = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;

            foreach (var pair in materials)
            {
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), pair.Key), pair.Value);
            }

            // The takes are only known after a first import with animation on
            importer.clipAnimations = Array.Empty<ModelImporterClipAnimation>();
            importer.SaveAndReimport();

            var clips = new List<ModelImporterClipAnimation>();
            var seen = new HashSet<string>();
            foreach (var take in importer.defaultClipAnimations)
            {
                // Blender names a take "<armature>|<action>"; the state is the action
                var state = take.takeName.Split('|').Last();
                if (!manifest.States.TryGetValue(state, out var info))
                {
                    Debug.LogWarning($"[FlyBand] {fbxPath}: take {take.takeName} is not a manifest state; skipped");
                    continue;
                }

                if (!seen.Add(state))
                {
                    Debug.LogWarning($"[FlyBand] {fbxPath}: second take for {state} ({take.takeName}) skipped");
                    continue;
                }

                take.name = state;
                take.loopTime = info.Loop;
                take.loopPose = false;
                take.lockRootRotation = false;
                take.lockRootHeightY = false;
                take.lockRootPositionXZ = false;

                int frames = Mathf.RoundToInt(take.lastFrame - take.firstFrame);
                if (frames != info.Frames)
                {
                    Debug.LogWarning($"[FlyBand] {fbxPath}: take {take.takeName} spans {frames} frames, manifest says {info.Frames}");
                }

                clips.Add(take);
            }

            importer.clipAnimations = clips.ToArray();
            importer.SaveAndReimport();
        }

        private static Dictionary<string, AvatarMask> BuildMasks(string folder, string slot, GameObject model, FlyManifest manifest)
        {
            var transforms = model.GetComponentsInChildren<Transform>(true);
            var byName = new Dictionary<string, Transform>();
            foreach (var t in transforms)
            {
                byName.TryAdd(t.name, t);
            }

            var masks = new Dictionary<string, AvatarMask>();
            foreach (var layer in manifest.Layers)
            {
                if (!manifest.States.Values.Any(s => s.Layer == layer.Key))
                {
                    AssetDatabase.DeleteAsset($"{folder}/fly_{slot}_{layer.Key}.mask");
                    continue;
                }

                var unknown = layer.Value.Where(b => !byName.ContainsKey(b)).ToList();
                if (unknown.Count > 0)
                {
                    throw new InvalidDataException(
                        $"Layer {layer.Key} names bone(s) {string.Join(", ", unknown)} that fly_{slot}.fbx does not have");
                }

                var bones = new HashSet<string>(layer.Value);
                var path = $"{folder}/fly_{slot}_{layer.Key}.mask";
                var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
                if (mask == null)
                {
                    mask = new AvatarMask();
                    AssetDatabase.CreateAsset(mask, path);
                }

                // A Generic mask lists every transform of the model, with only this layer's bones switched on
                var paths = transforms
                    .Where(t => t != model.transform)
                    .Select(t => (path: AnimationUtility.CalculateTransformPath(t, model.transform), active: bones.Contains(t.name)))
                    .ToList();

                mask.transformCount = paths.Count;
                for (int i = 0; i < paths.Count; i++)
                {
                    mask.SetTransformPath(i, paths[i].path);
                    mask.SetTransformActive(i, paths[i].active);
                }

                EditorUtility.SetDirty(mask);
                masks[layer.Key] = mask;
            }

            AssetDatabase.SaveAssets();
            return masks;
        }

        private static AnimatorController BuildController(string path, FlyManifest manifest,
            Dictionary<string, AnimationClip> clips, Dictionary<string, AvatarMask> masks)
        {
            AssetDatabase.DeleteAsset(path);
            var controller = new AnimatorController();
            AssetDatabase.CreateAsset(controller, path);

            // YARG sets SpeedAdjustment to fit the Playing loop to the song tempo; 1 plays it as authored until then
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = SPEED_PARAMETER,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = 1f,
            });

            var layerNames = new List<string> { BASE_LAYER };
            layerNames.AddRange(manifest.Layers.Keys.Where(l => l != BASE_LAYER));

            foreach (var layerName in layerNames)
            {
                // A layer with no states (the guitarist's feet) would only hold the base pose, so it is left out
                if (!manifest.States.Values.Any(s => s.Layer == layerName))
                {
                    Debug.Log($"[FlyBand] fly_{manifest.Slot}: layer {layerName} has no states; not added");
                    continue;
                }

                var stateMachine = new AnimatorStateMachine
                {
                    name = layerName,
                    hideFlags = HideFlags.HideInHierarchy,
                };
                AssetDatabase.AddObjectToAsset(stateMachine, controller);

                var layer = new AnimatorControllerLayer
                {
                    name = layerName,
                    stateMachine = stateMachine,
                    defaultWeight = 1f,
                    blendingMode = AnimatorLayerBlendingMode.Override,
                    avatarMask = layerName == BASE_LAYER ? null : masks[layerName],
                };
                controller.AddLayer(layer);

                // Playing goes in first: VenueCharacter takes animationClips[0] as the loop it tempo-scales
                var stateNames = manifest.States
                    .Where(s => s.Value.Layer == layerName)
                    .Select(s => s.Key)
                    .OrderBy(s => s == "Playing" ? 0 : 1)
                    .ToList();

                var states = new Dictionary<string, AnimatorState>();
                for (int i = 0; i < stateNames.Count; i++)
                {
                    var name = stateNames[i];
                    var state = stateMachine.AddState(name, new Vector3(300f, 60f * i, 0f));
                    state.motion = clips[name];
                    state.writeDefaultValues = true;

                    if (name == "Playing")
                    {
                        state.speedParameterActive = true;
                        state.speedParameter = SPEED_PARAMETER;
                    }

                    states[name] = state;
                }

                var defaultName = DefaultStateName(layerName, stateNames);
                stateMachine.defaultState = states[defaultName];

                // A one-shot (strum, hit) eases back into the layer's hold state when it has played out
                foreach (var name in stateNames)
                {
                    if (manifest.States[name].Loop || name == defaultName)
                    {
                        continue;
                    }

                    var back = states[name].AddTransition(states[defaultName]);
                    back.hasExitTime = true;
                    back.exitTime = 1f;
                    back.hasFixedDuration = true;
                    back.duration = RETURN_TRANSITION_SECONDS;
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        /// <summary>Base idles; every other layer holds its *Rest state, or HihatClose for the hi-hat foot.</summary>
        public static string DefaultStateName(string layerName, IList<string> stateNames)
        {
            if (layerName == BASE_LAYER)
            {
                return stateNames.Contains("Idle") ? "Idle" : stateNames[0];
            }

            return stateNames.FirstOrDefault(s => s.EndsWith("Rest"))
                ?? stateNames.FirstOrDefault(s => s == "HihatClose")
                ?? stateNames[0];
        }

        private static GameObject BuildPrefab(string path, FlyManifest manifest, GameObject model, Avatar avatar,
            AnimatorController controller, Dictionary<string, Material> materials)
        {
            var instance = (GameObject) PrefabUtility.InstantiatePrefab(model);
            try
            {
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                instance.name = $"fly_{manifest.Slot}";

                // The FBX keeps the character facing Unity +Z, the way VRM bandmates face, so the root needs no turn.
                // BackgroundManager keeps this root rotation as an offset, so a model that faces away can be fixed here.
                instance.transform.localPosition = Vector3.zero;
                instance.transform.localRotation = Quaternion.identity;

                foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
                {
                    var shared = renderer.sharedMaterials;
                    for (int i = 0; i < shared.Length; i++)
                    {
                        var name = shared[i] != null ? shared[i].name : string.Empty;
                        if (materials.TryGetValue(name, out var material))
                        {
                            shared[i] = material;
                        }
                    }
                    renderer.sharedMaterials = shared;
                }

                var animator = instance.GetComponent<Animator>();
                if (animator == null)
                {
                    animator = instance.AddComponent<Animator>();
                }

                animator.runtimeAnimatorController = controller;
                animator.avatar = avatar;
                animator.applyRootMotion = false;

                var character = instance.AddComponent<VenueCharacter>();
                ConfigureCharacter(character, manifest, controller);

                var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
                AssetDatabase.SaveAssets();
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void ConfigureCharacter(VenueCharacter character, FlyManifest manifest, AnimatorController controller)
        {
            character.Type = manifest.Slot switch
            {
                "guitar" => VenueCharacter.CharacterType.Guitar,
                "bass"   => VenueCharacter.CharacterType.Bass,
                "drums"  => VenueCharacter.CharacterType.Drums,
                "vocals" => VenueCharacter.CharacterType.Vocals,
                "keys"   => VenueCharacter.CharacterType.Keys,
                _        => throw new InvalidDataException($"Unknown slot {manifest.Slot}")
            };

            SetPrivate(character, "_framesToFirstHit", manifest.ImpactFrame);
            SetPrivate(character, "_actionsPerAnimationCycle", manifest.PlayingBeats);
            SetPrivate(character, "_enableAnimationStates", true);
            SetPrivate(character, "_idleAnimationName", "Idle");
            SetPrivate(character, "_playingAnimationName", "Playing");
            SetPrivate(character, "_strumUpStates", new List<string> { "StrumUp" });

            character.AnimationStates ??= new AnimationStateMap();
            var map = character.AnimationStates.Dictionary;
            map.Clear();
            foreach (var pair in StateNamesFor(manifest.States.Keys))
            {
                map[pair.Key] = pair.Value;
            }

            character.LayerStates = new AnimationDictionary();
            foreach (var layer in controller.layers)
            {
                foreach (var state in layer.stateMachine.states)
                {
                    character.LayerStates.Add(layer.name, state.state.name);
                }
            }

            EditorUtility.SetDirty(character);
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var info = typeof(VenueCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (info == null)
            {
                throw new MissingFieldException(nameof(VenueCharacter), field);
            }

            info.SetValue(target, value);
        }

        /// <summary>Which of our animator states plays each YARG animation event (only states the fly has).</summary>
        public static Dictionary<AnimationStateType, string> StateNamesFor(IEnumerable<string> available)
        {
            var have = new HashSet<string>(available);
            var result = new Dictionary<AnimationStateType, string>();

            foreach (var (type, name) in StateTable)
            {
                if (have.Contains(name))
                {
                    result[type] = name;
                }
            }

            return result;
        }

        private static readonly (AnimationStateType, string)[] StateTable =
        {
            (AnimationStateType.Idle, "Idle"),
            (AnimationStateType.Playing, "Playing"),
            (AnimationStateType.IdleRealtime, "IdleRealtime"),
            (AnimationStateType.IdleIntense, "IdleIntense"),
            (AnimationStateType.Intense, "Intense"),
            (AnimationStateType.Mellow, "Mellow"),
            (AnimationStateType.PlayingSolo, "PlayingSolo"),

            // Guitar and bass
            (AnimationStateType.StrumUp, "StrumUp"),
            (AnimationStateType.StrumDown, "StrumDown"),
            (AnimationStateType.LeftHandPosition1, "HandPositionOne"),
            (AnimationStateType.LeftHandPosition2, "HandPositionTwo"),
            (AnimationStateType.LeftHandPosition3, "HandPositionThree"),
            (AnimationStateType.LeftHandPosition4, "HandPositionFour"),
            (AnimationStateType.LeftHandPosition5, "HandPositionFive"),
            (AnimationStateType.LeftHandPosition6, "HandPositionSix"),
            (AnimationStateType.LeftHandPosition7, "HandPositionSeven"),
            (AnimationStateType.LeftHandPosition8, "HandPositionEight"),
            (AnimationStateType.Slap, "Slap"),
            (AnimationStateType.Pick, "Pick"),
            (AnimationStateType.Finger, "Finger"),

            // Drums
            (AnimationStateType.Kick, "Kick"),
            (AnimationStateType.OpenHiHat, "HihatOpen"),
            (AnimationStateType.CloseHiHat, "HihatClose"),
            (AnimationStateType.SnareLhHard, "SnareLeft"),
            (AnimationStateType.SnareLhSoft, "SnareLeft"),
            (AnimationStateType.SnareRhHard, "SnareRight"),
            (AnimationStateType.SnareRhSoft, "SnareRight"),
            (AnimationStateType.HihatLeftHand, "HihatLeft"),
            (AnimationStateType.HihatRightHand, "HihatRight"),
            (AnimationStateType.Crash1LhHard, "Crash1Left"),
            (AnimationStateType.Crash1LhSoft, "Crash1Left"),
            (AnimationStateType.Crash1RhHard, "Crash1Right"),
            (AnimationStateType.Crash1RhSoft, "Crash1Right"),
            (AnimationStateType.Crash2LhHard, "Crash2Left"),
            (AnimationStateType.Crash2LhSoft, "Crash2Left"),
            (AnimationStateType.Crash2RhHard, "Crash2Right"),
            (AnimationStateType.Crash2RhSoft, "Crash2Right"),
            (AnimationStateType.RideLh, "RideLeft"),
            (AnimationStateType.RideRh, "RideRight"),
            (AnimationStateType.Tom1LeftHand, "Tom1Left"),
            (AnimationStateType.Tom1RightHand, "Tom1Right"),
            (AnimationStateType.Tom2LeftHand, "Tom2Left"),
            (AnimationStateType.Tom2RightHand, "Tom2Right"),
            (AnimationStateType.FloorTomLeftHand, "FloorTomLeft"),
            (AnimationStateType.FloorTomRightHand, "FloorTomRight"),
        };

        private static void EnsureFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static string GetArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    /// <summary>The fly_&lt;slot&gt;.json manifest Blender writes beside each export.</summary>
    public class FlyManifest
    {
        public class StateInfo
        {
            public string Layer;
            public int    Frames;
            public bool   Loop;
        }

        public string Path;
        public string Name;
        public string Slot;
        public string Fbx;
        public string Albedo;
        public string Mask;
        public int    Fps;
        public int    ImpactFrame;
        public int    PlayingBeats;

        // Insertion order is the manifest's order
        public readonly Dictionary<string, List<string>> Layers = new();
        public readonly Dictionary<string, StateInfo>    States = new();

        public string Resolve(string file) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, file);

        public static FlyManifest Load(string path)
        {
            var json = JObject.Parse(File.ReadAllText(path));
            var slot = (string) json["slot"];
            var manifest = new FlyManifest
            {
                Path = path,
                Name = (string) json["name"],
                Slot = slot,
                Fbx = (string) json["fbx"] ?? $"fly_{slot}.fbx",
                Albedo = (string) json["albedo"] ?? $"fly_{slot}_albedo.png",
                Mask = (string) json["mask"] ?? $"fly_{slot}_mask.png",
                Fps = (int?) json["fps"] ?? 30,
                ImpactFrame = (int) json["impact_frame"],
                PlayingBeats = (int) json["playing_beats"],
            };

            foreach (var layer in (JObject) json["layers"])
            {
                manifest.Layers[layer.Key] = layer.Value!.Select(b => (string) b).ToList();
            }

            foreach (var state in (JObject) json["states"])
            {
                manifest.States[state.Key] = new StateInfo
                {
                    Layer = (string) state.Value!["layer"],
                    Frames = (int) state.Value["frames"],
                    Loop = (bool) state.Value["loop"],
                };
            }

            var strays = manifest.States.Where(s => s.Value.Layer != "Base" && !manifest.Layers.ContainsKey(s.Value.Layer)).ToList();
            if (strays.Count > 0)
            {
                throw new InvalidDataException(
                    $"{path}: state(s) {string.Join(", ", strays.Select(s => s.Key))} name a layer missing from \"layers\"");
            }

            if (!manifest.States.ContainsKey("Playing") || !manifest.States.ContainsKey("Idle"))
            {
                throw new InvalidDataException($"{path} must have Playing and Idle states");
            }

            return manifest;
        }
    }
}
