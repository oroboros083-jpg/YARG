using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using YARG.Core.Chart;
using YARG.Helpers;
using YARG.Venue;
using YARG.Venue.Characters;
using YARG.Venue.VenueCamera;
using CameraLocation = YARG.Venue.VenueCamera.CameraManager.CameraLocation;
using CameraDistance = YARG.Venue.VenueCamera.CameraManager.CameraDistance;
using Subject = YARG.Core.Chart.CameraCutEvent.CameraCutSubject;

namespace YARG.Editor.FlyBand
{
    /// <summary>
    /// The venue half of the fly band build: turns venue.py's stage export (stage.json, stage.fbx and its baked
    /// textures) into a .yarground with YARG's light, camera and character managers and the three fly prefabs on
    /// their slot markers.
    /// </summary>
    public static partial class FlyBandBuilder
    {
        public const string VENUE_FILE_NAME = "flyband.yarground";
        public const string STAGE_FOLDER = ASSET_ROOT + "/stage";
        public const string VENUE_PREFAB_PATH = STAGE_FOLDER + "/flyband_venue.prefab";
        public const string VENUE_LAYER = "Venue";

        private const string EXAMPLE_VENUE_SCENE = "Assets/Authoring/Venue/VenueCreation.unity";

        // Blender's radiometric watts to URP intensity. A Cycles point or spot of P watts has radiant intensity
        // P / 4pi W/sr, and Cycles' diffuse BSDF divides by pi where URP's Lambert does not, so URP gets P / (4 pi^2).
        // Emission maps 1:1 (Blender emission strength 1 = URP emission 1), which this keeps consistent.
        public const float WATTS_TO_URP_POINT = 1f / (4f * Mathf.PI * Mathf.PI);

        // A Lambertian area light of P watts has on-axis intensity P / pi W/sr, so URP gets P / pi^2
        public const float WATTS_TO_URP_AREA = 1f / (Mathf.PI * Mathf.PI);

        // URP has no realtime area lights, so an area light becomes a wide spot with a fully soft edge
        private const float AREA_AS_SPOT_DEGREES = 150f;

        private const float LIGHT_RANGE = 40f;

        // URP's forward renderer lights each object with at most 8 additional lights (4 on Low quality), and the
        // stage has 13, so the stage mesh is cut into tiles that each pick their own nearest lights
        private const int STAGE_TILES_X = 4;
        private const int STAGE_TILES_Z = 3;

        // Lights that cast shadows: the slot keys and the drum top light; 13 shadowed spots would starve the atlas
        private static readonly string[] ShadowedLightPrefixes = { "key_", "drum_top" };

        // Blender renders 16:9, with the 36 mm sensor fitted to the frame's width
        public const float BLENDER_ASPECT = 16f / 9f;

        [MenuItem("YARG/Fly Band/Build Venue From stage.json...")]
        public static void BuildVenueFromMenu()
        {
            var json = EditorUtility.OpenFilePanel("Fly band stage.json", DEFAULT_MANIFEST_DIR, "json");
            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            try
            {
                var outPath = Path.Combine(Path.GetDirectoryName(json)!, VENUE_FILE_NAME);
                BuildVenue(json, outPath);
                EditorUtility.DisplayDialog("Fly band", "Built:\n" + outPath, "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Fly band", "Venue build failed. See console for more info.", "OK");
            }
        }

        /// <summary>Builds the venue prefab from <paramref name="stageJson"/> and exports it to <paramref name="outPath"/>.</summary>
        public static GameObject BuildVenue(string stageJson, string outPath)
        {
            var manifest = StageManifest.Load(stageJson);
            EnsureFolder(STAGE_FOLDER);

            var fbxPath = CopyIn(manifest.Resolve(manifest.Fbx), STAGE_FOLDER);
            var albedoPath = CopyIn(manifest.Resolve(manifest.Albedo), STAGE_FOLDER);
            var maskPath = CopyIn(manifest.Resolve(manifest.Mask), STAGE_FOLDER);
            var emissionPath = CopyIn(manifest.Resolve(manifest.Emission), STAGE_FOLDER);

            ConfigureTexture(albedoPath, true);
            ConfigureTexture(maskPath, false);
            ConfigureTexture(emissionPath, true); // venue.py sRGB-encodes the normalized glow

            var material = BuildStageMaterial($"{STAGE_FOLDER}/stage.mat",
                AssetDatabase.LoadAssetAtPath<Texture2D>(albedoPath),
                AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath),
                AssetDatabase.LoadAssetAtPath<Texture2D>(emissionPath),
                manifest.EmissionScale);

            ConfigureStageModel(fbxPath);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("Venue");
            try
            {
                var stage = (GameObject) PrefabUtility.InstantiatePrefab(model);
                PrefabUtility.UnpackPrefabInstance(stage, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                stage.name = "Stage";
                stage.transform.SetParent(root.transform, false);

                var nodes = stage.GetComponentsInChildren<Transform>(true)
                    .Where(t => t != stage.transform)
                    .GroupBy(t => t.name)
                    .ToDictionary(g => g.Key, g => g.First());

                BuildStageMeshes(stage, nodes, manifest, material);

                var lightManager = AddManager<LightManager>(root, "Light Manager");
                CopyExampleLightManager(lightManager);
                ConvertLights(nodes, manifest);

                var cameraManager = AddManager<CameraManager>(root, "Camera Manager");
                SetSerialized(cameraManager, "_venue", root);
                var wide = ConvertCameras(nodes, manifest, cameraManager);

                var bundleManager = root.AddComponent<BundleBackgroundManager>();
                SetSerialized(bundleManager, "mainCamera", wide);

                var characterManager = AddManager<CharacterManager>(root, "Character Manager");
                SetSerialized(characterManager, "_venue", root);
                PlaceCharacters(root, nodes, manifest);

                SetLayerRecursively(root, LayerMask.NameToLayer(VENUE_LAYER));

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, VENUE_PREFAB_PATH);
                AssetDatabase.SaveAssets();

                BundleBackgroundManager.ExportBundle(prefab, BackgroundHelper.ExportType.Background, outPath);
                Debug.Log($"[FlyBand] Wrote {outPath}");
                return prefab;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static Material BuildStageMaterial(string path, Texture2D albedo, Texture2D mask, Texture2D emission,
            float emissionScale)
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
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_MetallicGlossMap", mask);
            material.SetFloat("_WorkflowMode", 1f); // metallic
            material.SetFloat("_Metallic", 1f);
            material.SetFloat("_Smoothness", 1f); // scales the mask's alpha
            material.SetFloat("_SmoothnessTextureChannel", 0f); // smoothness from the metallic map's alpha
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_Cull", 2f);
            material.SetFloat("_AlphaClip", 0f);
            material.SetFloat("_ZWrite", 1f);

            // The emission PNG is normalized to its brightest texel; emission_scale restores Blender's strength
            material.SetTexture("_EmissionMap", emission);
            material.SetColor("_EmissionColor", new Color(emissionScale, emissionScale, emissionScale, 1f));
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            material.EnableKeyword("_EMISSION");

            ValidateUrpMaterial(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void ConfigureStageModel(string fbxPath)
        {
            var importer = (ModelImporter) AssetImporter.GetAtPath(fbxPath);
            importer.animationType = ModelImporterAnimationType.None;
            importer.importAnimation = false;
            importer.importBlendShapes = false;
            importer.importCameras = true;
            importer.importLights = true;
            importer.importVisibility = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.None; // the prefab assigns stage.mat
            importer.isReadable = true; // BuildStageMeshes cuts the stage mesh into tiles
            importer.SaveAndReimport();
        }

        private static void BuildStageMeshes(GameObject stage, Dictionary<string, Transform> nodes, StageManifest manifest,
            Material material)
        {
            foreach (var renderer in stage.GetComponentsInChildren<Renderer>(true))
            {
                renderer.sharedMaterials = Enumerable.Repeat(material, renderer.sharedMaterials.Length).ToArray();
                renderer.shadowCastingMode = manifest.NoShadowMeshes.Contains(renderer.name)
                    ? ShadowCastingMode.Off
                    : ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }

            if (!nodes.TryGetValue("stage", out var stageNode) || stageNode.GetComponent<MeshFilter>() == null)
            {
                throw new InvalidDataException("stage.fbx has no mesh named stage");
            }

            var source = stageNode.GetComponent<MeshFilter>().sharedMesh;
            var tiles = SplitMesh(source, STAGE_TILES_X, STAGE_TILES_Z, $"{STAGE_FOLDER}/stage_tiles.asset");

            // The tiles replace the whole mesh, so the FBX's copy stays out of the bundle
            UnityEngine.Object.DestroyImmediate(stageNode.GetComponent<MeshRenderer>());
            UnityEngine.Object.DestroyImmediate(stageNode.GetComponent<MeshFilter>());

            foreach (var mesh in tiles)
            {
                var tile = new GameObject(mesh.name);
                tile.transform.SetParent(stageNode, false);
                tile.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = tile.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }

            Debug.Log($"[FlyBand] stage mesh {source.bounds.size:F2} m cut into {tiles.Count} tiles");
        }

        /// <summary>Cuts <paramref name="source"/> into a grid of meshes by triangle centroid (in its own X and Z).</summary>
        private static List<Mesh> SplitMesh(Mesh source, int tilesX, int tilesZ, string assetPath)
        {
            var vertices = source.vertices;
            var normals = source.normals;
            var tangents = source.tangents;
            var colors = source.colors;
            var uvs = new List<List<Vector4>>();
            for (int ch = 0; ch < 8; ch++)
            {
                if (!source.HasVertexAttribute(VertexAttribute.TexCoord0 + ch))
                {
                    break;
                }
                var list = new List<Vector4>();
                source.GetUVs(ch, list);
                uvs.Add(list);
            }

            var triangles = source.triangles;
            var bounds = source.bounds;
            var buckets = new List<int>[tilesX * tilesZ];
            for (int i = 0; i < buckets.Length; i++)
            {
                buckets[i] = new List<int>();
            }

            for (int t = 0; t < triangles.Length; t += 3)
            {
                var c = (vertices[triangles[t]] + vertices[triangles[t + 1]] + vertices[triangles[t + 2]]) / 3f;
                int x = Mathf.Clamp((int) ((c.x - bounds.min.x) / bounds.size.x * tilesX), 0, tilesX - 1);
                int z = Mathf.Clamp((int) ((c.z - bounds.min.z) / bounds.size.z * tilesZ), 0, tilesZ - 1);
                buckets[z * tilesX + x].Add(t);
            }

            var meshes = new List<Mesh>();
            for (int b = 0; b < buckets.Length; b++)
            {
                if (buckets[b].Count == 0)
                {
                    continue;
                }

                var remap = new Dictionary<int, int>();
                var indices = new List<int>();
                foreach (var t in buckets[b])
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int old = triangles[t + k];
                        if (!remap.TryGetValue(old, out int index))
                        {
                            index = remap.Count;
                            remap[old] = index;
                        }
                        indices.Add(index);
                    }
                }

                var order = remap.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
                var mesh = new Mesh
                {
                    name = $"stage_{b % tilesX}_{b / tilesX}",
                    indexFormat = order.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
                };
                mesh.SetVertices(order.Select(i => vertices[i]).ToArray());
                if (normals.Length > 0) mesh.SetNormals(order.Select(i => normals[i]).ToArray());
                if (tangents.Length > 0) mesh.SetTangents(order.Select(i => tangents[i]).ToArray());
                if (colors.Length > 0) mesh.SetColors(order.Select(i => colors[i]).ToArray());
                for (int ch = 0; ch < uvs.Count; ch++)
                {
                    mesh.SetUVs(ch, order.Select(i => uvs[ch][i]).ToList());
                }
                mesh.SetTriangles(indices, 0);
                mesh.RecalculateBounds();
                meshes.Add(mesh);
            }

            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(meshes[0], assetPath);
            foreach (var mesh in meshes.Skip(1))
            {
                AssetDatabase.AddObjectToAsset(mesh, assetPath);
            }
            AssetDatabase.SaveAssets();
            return meshes;
        }

        private static T AddManager<T>(GameObject root, string name) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            return go.AddComponent<T>();
        }

        // The example venue's LightManager holds YARG's cue palettes (warm, cool, ...); the fly venue uses the same
        private static void CopyExampleLightManager(LightManager target)
        {
            var example = EditorSceneManager.OpenScene(EXAMPLE_VENUE_SCENE, OpenSceneMode.Additive);
            try
            {
                var source = example.GetRootGameObjects()
                    .Select(g => g.GetComponentInChildren<LightManager>(true))
                    .FirstOrDefault(m => m != null);
                if (source == null)
                {
                    Debug.LogWarning($"[FlyBand] {EXAMPLE_VENUE_SCENE} has no LightManager; YARG's fallback palettes apply");
                    return;
                }

                EditorUtility.CopySerialized(source, target);
            }
            finally
            {
                EditorSceneManager.CloseScene(example, true);
            }
        }

        /// <summary>Which YARG light group a venue.py light belongs to, by its name.</summary>
        public static VenueLightLocation LightLocationFor(string name)
        {
            if (name.StartsWith("key_") || name == "front_fill") return VenueLightLocation.Front;
            if (name.StartsWith("back_")) return VenueLightLocation.Back;
            if (name == "wash_l") return VenueLightLocation.Left;
            if (name == "wash_r") return VenueLightLocation.Right;
            if (name == "drum_top") return VenueLightLocation.Center;
            return VenueLightLocation.Generic;
        }

        private static void ConvertLights(Dictionary<string, Transform> nodes, StageManifest manifest)
        {
            foreach (var (name, info) in manifest.Lights.Select(p => (p.Key, p.Value)))
            {
                if (!nodes.TryGetValue(name, out var node))
                {
                    throw new InvalidDataException($"stage.json names light {name}, which stage.fbx does not have");
                }

                // Unity's fake null for a missing component defeats ??, so these test == null
                var light = node.GetComponent<Light>();
                if (light == null)
                {
                    light = node.gameObject.AddComponent<Light>();
                }
                bool area = info.Type == "AREA";
                if (info.Type != "SPOT" && !area)
                {
                    light.type = LightType.Point;
                }
                else
                {
                    light.type = LightType.Spot;
                    light.spotAngle = area ? AREA_AS_SPOT_DEGREES : info.SpotDegrees;
                    light.innerSpotAngle = area ? 0f : info.SpotDegrees * (1f - info.SpotBlend);
                }

                // Blender colors are linear; URP reads Light.color as sRGB
                light.color = new Color(info.Color[0], info.Color[1], info.Color[2]).gamma;
                light.intensity = info.Watts * (area ? WATTS_TO_URP_AREA : WATTS_TO_URP_POINT);
                light.range = LIGHT_RANGE;
                light.lightmapBakeType = LightmapBakeType.Realtime;
                light.renderMode = LightRenderMode.Auto;
                light.shadows = ShadowedLightPrefixes.Any(p => name.StartsWith(p)) ? LightShadows.Soft : LightShadows.None;
                light.shadowNearPlane = 0.2f;

                var venueLight = node.gameObject.AddComponent<VenueLight>();
                var serialized = new SerializedObject(venueLight);
                serialized.FindProperty("<Location>k__BackingField").enumValueIndex = (int) LightLocationFor(name);
                serialized.FindProperty("<IsSpotlight>k__BackingField").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Debug.Log($"[FlyBand] light {name}: {light.type} {light.intensity:F1} ({info.Watts} W) {light.spotAngle:F0} deg, " +
                    $"{LightLocationFor(name)}, at {node.position:F2} facing {node.forward:F2}");
            }

            // Lights the FBX has that stage.json does not describe are left as imported, and said
            foreach (var light in nodes.Values.Select(n => n.GetComponent<Light>()).Where(l => l))
            {
                if (!manifest.Lights.ContainsKey(light.name))
                {
                    Debug.LogWarning($"[FlyBand] stage.fbx light {light.name} is not in stage.json; left as imported");
                }
            }
        }

        /// <summary>Which YARG camera slot a venue.py camera fills, and the cut subjects it answers.</summary>
        public static (CameraLocation location, CameraDistance distance, Subject[] subjects) CameraRoleFor(string name)
        {
            return name switch
            {
                "cam_wide" => (CameraLocation.Stage, CameraDistance.Far,
                    new[] { Subject.Stage, Subject.AllFar, Subject.AllNear, Subject.Crowd }),
                "cam_guitar_close" => (CameraLocation.Guitar, CameraDistance.Near,
                    new[] { Subject.Guitar, Subject.GuitarCloseup, Subject.GuitarCloseupHead, Subject.NearNoDrum }),
                "cam_bass_close" => (CameraLocation.Bass, CameraDistance.Near,
                    new[] { Subject.Bass, Subject.BassCloseup, Subject.BassCloseupHead, Subject.BassGuitar }),
                "cam_drums_close" => (CameraLocation.Drums, CameraDistance.Near,
                    new[] { Subject.Drums, Subject.DrumsKick, Subject.DrumsCloseupHand, Subject.DrumsCloseupHead }),
                _ => (CameraLocation.Random, CameraDistance.Far, Array.Empty<Subject>()),
            };
        }

        /// <summary>Unity's vertical FOV for a Blender lens on a sensor fitted to a 16:9 frame's width.</summary>
        public static float VerticalFov(float lensMm, float sensorMm)
        {
            float horizontal = 2f * Mathf.Atan(sensorMm / (2f * lensMm)) * Mathf.Rad2Deg;
            return Camera.HorizontalToVerticalFieldOfView(horizontal, BLENDER_ASPECT);
        }

        // Returns the stage-wide camera, which BundleBackgroundManager holds as its main camera
        private static Camera ConvertCameras(Dictionary<string, Transform> nodes, StageManifest manifest,
            CameraManager cameraManager)
        {
            Camera wide = null;
            foreach (var (name, info) in manifest.Cameras.Select(p => (p.Key, p.Value)))
            {
                if (!nodes.TryGetValue(name, out var node))
                {
                    throw new InvalidDataException($"stage.json names camera {name}, which stage.fbx does not have");
                }

                var camera = node.GetComponent<Camera>();
                if (camera == null)
                {
                    camera = node.gameObject.AddComponent<Camera>();
                }
                camera.usePhysicalProperties = false;
                camera.fieldOfView = VerticalFov(info.LensMm, info.SensorMm);
                camera.nearClipPlane = 0.05f;
                camera.farClipPlane = 200f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.002f, 0.002f, 0.004f).gamma; // band.py's world color
                camera.cullingMask = LayerMask.GetMask(VENUE_LAYER);
                camera.allowHDR = true;

                var data = camera.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.renderShadows = true;
                data.volumeLayerMask = LayerMask.GetMask(VENUE_LAYER);

                var (location, distance, subjects) = CameraRoleFor(name);
                var venueCamera = node.gameObject.AddComponent<VenueCamera>();
                venueCamera.CameraLocation = location;
                venueCamera.CameraDistance = distance;
                venueCamera.CameraOrientation = CameraManager.CameraOrientation.Front;
                venueCamera.CameraCutSubjects = subjects.ToList();
                SetSerialized(venueCamera, "_cameraManager", cameraManager);

                if (location == CameraLocation.Stage && wide == null)
                {
                    wide = camera;
                }

                Debug.Log($"[FlyBand] camera {name}: {location} {distance}, vertical fov {camera.fieldOfView:F1} " +
                    $"(lens {info.LensMm} mm), at {node.position:F2} facing {node.forward:F2}");
            }

            if (wide == null)
            {
                throw new InvalidDataException("stage.json has no cam_wide, and YARG starts every song on the Stage camera");
            }

            return wide;
        }

        private static void PlaceCharacters(GameObject root, Dictionary<string, Transform> nodes, StageManifest manifest)
        {
            var parent = new GameObject("Characters").transform;
            parent.SetParent(root.transform, false);

            foreach (var slot in manifest.Slots)
            {
                if (!nodes.TryGetValue($"slot_{slot}", out var marker))
                {
                    throw new InvalidDataException($"stage.fbx has no slot_{slot} marker");
                }

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(slot));
                if (prefab == null || prefab.GetComponent<VenueCharacter>() == null)
                {
                    throw new FileNotFoundException($"No fly character prefab with a VenueCharacter for slot {slot}", PrefabPath(slot));
                }

                var instance = (GameObject) PrefabUtility.InstantiatePrefab(prefab, parent);
                instance.transform.SetPositionAndRotation(marker.position, Quaternion.Euler(0f, SlotYaw(marker), 0f));
                Debug.Log($"[FlyBand] {slot} at {marker.position:F2}, yaw {SlotYaw(marker):F1} deg");
            }
        }

        /// <summary>
        /// The Unity yaw of a slot marker, taken from its axes rather than its Euler angles so it does not depend on
        /// how the FBX importer converts axes: the marker's local axis nearest world up is its up, and the
        /// horizontal axis nearest +Z (the audience side; every slot faces within 45 degrees of it) is its facing.
        /// </summary>
        public static float SlotYaw(Transform marker)
        {
            var axes = new[] { marker.right, -marker.right, marker.up, -marker.up, marker.forward, -marker.forward };
            var up = axes.OrderByDescending(a => Vector3.Dot(a, Vector3.up)).First();
            var facing = axes.Where(a => Mathf.Abs(Vector3.Dot(a, up)) < 0.5f)
                .OrderByDescending(a => Vector3.Dot(a, Vector3.forward))
                .First();
            return Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
        }

        private static void SetSerialized(UnityEngine.Object target, string field, UnityEngine.Object value)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(field)
                ?? throw new MissingFieldException(target.GetType().Name, field);
            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
            }
        }
    }

    /// <summary>The stage.json venue.py writes beside stage.fbx.</summary>
    public class StageManifest
    {
        public class LightInfo
        {
            public string  Type;
            public float[] Color;
            public float   Watts;
            public float   SpotDegrees;
            public float   SpotBlend;
            public float   Size;
        }

        public class CameraInfo
        {
            public float LensMm;
            public float SensorMm;
        }

        public string Path;
        public string Fbx;
        public string Albedo;
        public string Mask;
        public string Emission;
        public float  EmissionScale;
        public readonly HashSet<string>                NoShadowMeshes = new();
        public readonly Dictionary<string, LightInfo>  Lights = new();
        public readonly Dictionary<string, CameraInfo> Cameras = new();
        public readonly List<string>                   Slots = new();

        public string Resolve(string file) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, file);

        public static StageManifest Load(string path)
        {
            var json = JObject.Parse(File.ReadAllText(path));
            var manifest = new StageManifest
            {
                Path = path,
                Fbx = (string) json["fbx"] ?? "stage.fbx",
                Albedo = (string) json["albedo"] ?? "stage_albedo.png",
                Mask = (string) json["mask"] ?? "stage_mask.png",
                Emission = (string) json["emission"] ?? "stage_emission.png",
                EmissionScale = (float?) json["emission_scale"] ?? 1f,
            };

            foreach (var name in (JArray) json["no_shadow_meshes"] ?? new JArray())
            {
                manifest.NoShadowMeshes.Add((string) name);
            }

            foreach (var light in (JObject) json["lights"])
            {
                var v = light.Value!;
                manifest.Lights[light.Key] = new LightInfo
                {
                    Type = (string) v["type"],
                    Color = v["color"]!.Select(c => (float) c).ToArray(),
                    Watts = (float) v["watts"],
                    SpotDegrees = (float?) v["spot_deg"] ?? 0f,
                    SpotBlend = (float?) v["spot_blend"] ?? 0f,
                    Size = (float?) v["size"] ?? 0f,
                };
            }

            foreach (var camera in (JObject) json["cameras"])
            {
                manifest.Cameras[camera.Key] = new CameraInfo
                {
                    LensMm = (float) camera.Value!["lens_mm"],
                    SensorMm = (float?) camera.Value["sensor_mm"] ?? 36f,
                };
            }

            foreach (var slot in (JArray) json["slots"] ?? new JArray { "guitar", "bass", "drums" })
            {
                manifest.Slots.Add((string) slot);
            }

            return manifest;
        }
    }
}
