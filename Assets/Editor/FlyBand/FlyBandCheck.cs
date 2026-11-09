using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using YARG.Venue;
using YARG.Venue.Characters;
using YARG.Venue.VenueCamera;

namespace YARG.Editor.FlyBand
{
    /// <summary>
    /// Checks a built fly the way YARG will use it: the animator has every manifest state on its layer, VenueCharacter finds its advanced animations, the clips move the bones, and the .yargchar loads back. Renders a pose to PNG. CheckVenue does the same for the .yarground: managers, characters on their slots, lights, cameras, and a frame.
    /// </summary>
    public static class FlyBandCheck
    {
        public const string RENDER_DIR = @"P:\output\flyband\yarg\unity";

        private static readonly string[] RequiredGuitar =
        {
            "StrumUp", "StrumDown", "HandPositionOne", "HandPositionTwo", "HandPositionThree", "HandPositionFour",
            "HandPositionFive", "HandPositionSix", "HandPositionSeven", "HandPositionEight"
        };

        private static readonly string[] RequiredDrums =
        {
            "Kick", "HihatLeft", "HihatRight", "Tom1Left", "Tom1Right", "Tom2Left", "Tom2Right", "FloorTomLeft",
            "FloorTomRight", "RideLeft", "RideRight"
        };

        public static void CheckFromCommandLine()
        {
            var dir = FlyBandBuilder.GetArg("-flyManifests") ?? FlyBandBuilder.DEFAULT_MANIFEST_DIR;
            var outDir = FlyBandBuilder.GetArg("-flyOut") ?? dir;
            EditorApplication.Exit(CheckAll(dir, outDir) ? 0 : 2);
        }

        public static bool CheckAll(string manifestDir, string outDir)
        {
            bool ok = true;
            foreach (var path in Directory.GetFiles(manifestDir, "fly_*.json").OrderBy(f => f))
            {
                ok &= Check(FlyManifest.Load(path), outDir);
            }

            Debug.Log(ok ? "[FlyCheck] ALL PASSED" : "[FlyCheck] SOME CHECKS FAILED");
            return ok;
        }

        public static bool Check(FlyManifest manifest, string outDir)
        {
            var failures = new List<string>();
            void Expect(bool condition, string what)
            {
                Debug.Log($"[FlyCheck] {manifest.Slot}: {(condition ? "PASS" : "FAIL")} {what}");
                if (!condition)
                {
                    failures.Add(what);
                }
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FlyBandBuilder.PrefabPath(manifest.Slot));
            Expect(prefab != null, "prefab exists");
            if (prefab == null)
            {
                return false;
            }

            var prefabAnimator = prefab.GetComponent<Animator>();
            var controller = prefabAnimator != null ? prefabAnimator.runtimeAnimatorController as AnimatorController : null;
            Expect(controller != null, "animator has an AnimatorController");
            if (controller == null)
            {
                return false;
            }

            Expect(controller.animationClips.Length > 0 && controller.animationClips[0].name == "Playing",
                $"animationClips[0] is Playing (it is {controller.animationClips.FirstOrDefault()?.name})");

            // Every manifest state sits on its manifest layer, in the controller asset
            foreach (var (state, info) in manifest.States.Select(p => (p.Key, p.Value)))
            {
                var layer = controller.layers.FirstOrDefault(l => l.name == info.Layer);
                bool found = layer != null && layer.stateMachine.states.Any(s => s.state.name == state);
                Expect(found, $"state {state} on layer {info.Layer}");
            }

            var instance = (GameObject) PrefabUtility.InstantiatePrefab(prefab);
            var bind = CapturePose(instance);
            var animator = instance.GetComponent<Animator>();
            var character = instance.GetComponent<VenueCharacter>();

            // Nothing renders in batch mode, so a culling animator would never move the bones
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.Rebind();
            animator.Update(0f);

            // ...and in the live Animator YARG drives
            foreach (var (state, info) in manifest.States.Select(p => (p.Key, p.Value)))
            {
                int layerIndex = animator.GetLayerIndex(info.Layer);
                Expect(layerIndex >= 0 && animator.HasState(layerIndex, Animator.StringToHash(state)),
                    $"animator.HasState({info.Layer}#{layerIndex}, {state})");
            }

            // VenueCharacter's own setup, as the venue's CharacterManager would run it
            character.Initialize(null);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            bool advanced = (bool) typeof(VenueCharacter).GetField("_hasAdvancedAnimations", flags).GetValue(character);
            var events = typeof(VenueCharacter).GetField("_animationEvents", flags).GetValue(character);
            var hasState = events.GetType().GetMethod("HasState", new[] { typeof(string) });

            var required = character.Type switch
            {
                VenueCharacter.CharacterType.Guitar or VenueCharacter.CharacterType.Bass => RequiredGuitar,
                VenueCharacter.CharacterType.Drums => RequiredDrums,
                _ => Array.Empty<string>()
            };

            foreach (var name in required)
            {
                Expect((bool) hasState.Invoke(events, new object[] { name }), $"CheckAdvancedAnimations finds {name}");
            }
            Expect(advanced, "_hasAdvancedAnimations is true");
            float clipFps = controller.animationClips[0].frameRate;
            Expect(Mathf.Abs(clipFps - manifest.Fps) < 0.01f, $"clips run at the manifest's {manifest.Fps} fps (they run at {clipFps})");
            float timeToFirstHit = character.TimeToFirstHit;
            Expect(Mathf.Abs(timeToFirstHit - manifest.ImpactFrame / clipFps) < 1e-3f,
                $"TimeToFirstHit {timeToFirstHit:F3}s = impact_frame / clip fps");

            // The clips move the bones: sample each at its impact frame (or mid-clip) and compare with the bind pose and with Idle's first frame (a hold may equal one of them, never both)
            var clips = controller.animationClips.ToDictionary(c => c.name);
            clips["Idle"].SampleAnimation(instance, 0f);
            var idle = CapturePose(instance);
            RestorePose(bind);
            foreach (var (state, info) in manifest.States.Select(p => (p.Key, p.Value)))
            {
                var clip = clips[state];
                float t = Mathf.Min(info.Loop ? clip.length * 0.5f : manifest.ImpactFrame / clip.frameRate, clip.length);
                clip.SampleAnimation(instance, t);
                var bones = info.Layer == "Base" ? null : manifest.Layers[info.Layer];
                var fromBind = LargestChange(bind, instance, bones);
                var fromIdle = LargestChange(idle, instance, bones);
                var (bone, degrees, metres) = fromBind.degrees >= fromIdle.degrees ? fromBind : fromIdle;
                Expect(degrees > 1f || metres > 1e-3f,
                    $"{state} moves its bones at {t:F3}s (most: {bone} {degrees:F1} deg, {metres * 1000f:F1} mm; " +
                    $"{fromBind.degrees:F1} deg from bind, {fromIdle.degrees:F1} deg from Idle)");
                RestorePose(bind);
            }

            // Hit on the live Animator through the layer mask: the hitting foot must leave its rest hold
            var hit = manifest.States.ContainsKey("StrumDown") ? "StrumDown"
                : manifest.States.FirstOrDefault(s => !s.Value.Loop && s.Value.Layer != "Base").Key;
            if (hit != null && manifest.Layers.TryGetValue(manifest.States[hit].Layer, out var hitBones))
            {
                animator.Play("Idle", 0, 0f);
                animator.Update(0f);
                animator.Update(0.5f);
                var held = CapturePose(instance);
                animator.Play(hit, animator.GetLayerIndex(manifest.States[hit].Layer), 0f);
                animator.Update(0f);
                animator.Update(manifest.ImpactFrame / clipFps);
                var (bone, degrees, _) = LargestChange(held, instance, hitBones);
                Expect(degrees > 1f, $"Animator {hit} at impact moves {bone} {degrees:F1} deg from the rest hold");
            }

            Render(instance, animator, manifest);

            UnityEngine.Object.DestroyImmediate(instance);

            // The .yargchar loads back with a working character in it
            var bundlePath = Path.Combine(outDir, $"fly_{manifest.Slot}.yargchar");
            Expect(File.Exists(bundlePath), $"{bundlePath} exists ({(File.Exists(bundlePath) ? new FileInfo(bundlePath).Length : 0)} bytes)");
            if (File.Exists(bundlePath))
            {
                var bundle = AssetBundle.LoadFromFile(bundlePath);
                Expect(bundle != null, "bundle loads");
                if (bundle != null)
                {
                    try
                    {
                        var loaded = bundle.LoadAsset<GameObject>(BundleBackgroundManager.CHARACTER_PREFAB_PATH.ToLowerInvariant());
                        Expect(loaded != null, $"bundle has {BundleBackgroundManager.CHARACTER_PREFAB_PATH}");
                        if (loaded != null)
                        {
                            var copy = UnityEngine.Object.Instantiate(loaded);
                            var loadedCharacter = copy.GetComponent<VenueCharacter>();
                            var loadedAnimator = copy.GetComponent<Animator>();
                            Expect(loadedCharacter != null && loadedCharacter.Type == character.Type,
                                $"bundled VenueCharacter type {loadedCharacter?.Type}");
                            Expect(loadedAnimator != null && loadedAnimator.runtimeAnimatorController != null &&
                                loadedAnimator.runtimeAnimatorController.animationClips.Length == manifest.States.Count,
                                $"bundled controller has {(loadedAnimator != null && loadedAnimator.runtimeAnimatorController != null ? loadedAnimator.runtimeAnimatorController.animationClips.Length : 0)} clips");
                            if (loadedCharacter != null && loadedAnimator != null)
                            {
                                loadedAnimator.Rebind();
                                loadedAnimator.Update(0f);
                                loadedCharacter.Initialize(null);
                                bool bundledAdvanced = (bool) typeof(VenueCharacter).GetField("_hasAdvancedAnimations", flags).GetValue(loadedCharacter);
                                Expect(bundledAdvanced, "bundled character has advanced animations");
                            }

                            int renderers = copy.GetComponentsInChildren<SkinnedMeshRenderer>().Length;
                            var mats = copy.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Select(m => m ? $"{m.name}({m.shader.name})" : "null");
                            Expect(renderers > 0, $"bundled skinned renderers {renderers}, materials {string.Join(", ", mats)}");
                            UnityEngine.Object.DestroyImmediate(copy);
                        }
                    }
                    finally
                    {
                        bundle.Unload(true);
                    }
                }
            }

            Debug.Log($"[FlyCheck] {manifest.Slot}: {(failures.Count == 0 ? "OK" : "FAILED: " + string.Join("; ", failures))}");
            return failures.Count == 0;
        }

        // How far a light or camera may point from what venue.py aimed it at
        private const float KEY_AIM_DEGREES = 3f;
        private const float CAMERA_AIM_DEGREES = 15f;

        // venue.py aims each key light at its slot 1.2 m up, and band.py frames the cameras about 1.3 m up
        private const float KEY_TARGET_HEIGHT = 1.2f;
        private const float CAMERA_TARGET_HEIGHT = 1.3f;

        /// <summary>Loads the .yarground back the way BackgroundManager does and checks what YARG will look for.</summary>
        public static bool CheckVenue(string stageJson, string venuePath)
        {
            var manifest = StageManifest.Load(stageJson);
            var failures = new List<string>();
            void Expect(bool condition, string what)
            {
                Debug.Log($"[FlyCheck] venue: {(condition ? "PASS" : "FAIL")} {what}");
                if (!condition)
                {
                    failures.Add(what);
                }
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            bool exists = File.Exists(venuePath);
            Expect(exists, $"{venuePath} exists ({(exists ? new FileInfo(venuePath).Length : 0)} bytes)");
            var bundle = exists ? AssetBundle.LoadFromFile(venuePath) : null;
            Expect(bundle != null, "bundle loads");
            if (bundle == null)
            {
                return false;
            }

            try
            {
                var bg = bundle.LoadAsset<GameObject>(BundleBackgroundManager.BACKGROUND_PREFAB_PATH.ToLowerInvariant());
                Expect(bg != null, $"bundle has {BundleBackgroundManager.BACKGROUND_PREFAB_PATH}");
                if (bg == null)
                {
                    return false;
                }

                var venue = UnityEngine.Object.Instantiate(bg);
                var nodes = venue.GetComponentsInChildren<Transform>(true)
                    .GroupBy(t => t.name)
                    .ToDictionary(g => g.Key, g => g.First());

                // Managers
                var bundleManager = venue.GetComponent<BundleBackgroundManager>();
                Expect(bundleManager != null, "root has BundleBackgroundManager");
                var mainCamera = bundleManager != null ? Serialized(bundleManager, "mainCamera") as Camera : null;
                Expect(mainCamera != null, $"BundleBackgroundManager.mainCamera is set ({(mainCamera ? mainCamera.name : "none")})");

                var characterManager = venue.GetComponentInChildren<CharacterManager>(true);
                Expect(characterManager != null && Serialized(characterManager, "_venue") == venue,
                    "CharacterManager whose _venue is the root");
                var cameraManager = venue.GetComponentInChildren<CameraManager>(true);
                Expect(cameraManager != null && Serialized(cameraManager, "_venue") == venue,
                    "CameraManager whose _venue is the root");
                var lightManager = venue.GetComponentInChildren<LightManager>(true);
                Expect(lightManager != null, "LightManager");

                // Characters, each on its slot marker
                var characters = venue.GetComponentsInChildren<VenueCharacter>(true);
                var types = characters.Select(c => c.Type).OrderBy(t => t).ToList();
                var wanted = new[]
                {
                    VenueCharacter.CharacterType.Guitar, VenueCharacter.CharacterType.Bass, VenueCharacter.CharacterType.Drums
                }.OrderBy(t => t);
                Expect(characters.Length == 3 && types.SequenceEqual(wanted), $"3 characters: {string.Join(", ", types)}");

                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                foreach (var character in characters)
                {
                    var slot = character.Type.ToString().ToLowerInvariant();
                    var animator = character.GetComponent<Animator>();
                    Expect(animator != null && animator.runtimeAnimatorController != null, $"{slot} has an animator controller");
                    if (animator == null)
                    {
                        continue;
                    }

                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.Rebind();
                    animator.Update(0f);
                    character.Initialize(null);
                    bool advanced = (bool) typeof(VenueCharacter).GetField("_hasAdvancedAnimations", flags).GetValue(character);
                    Expect(advanced, $"{slot} has advanced animations");

                    if (nodes.TryGetValue($"slot_{slot}", out var marker))
                    {
                        float off = (character.transform.position - marker.position).magnitude;
                        Expect(off < 0.01f, $"{slot} stands on slot_{slot} at {marker.position:F2} (off by {off * 1000f:F0} mm), " +
                            $"yaw {character.transform.eulerAngles.y:F1}");
                    }
                    else
                    {
                        Expect(false, $"venue has slot_{slot}");
                    }
                }

                // Lights: every stage.json light, as a VenueLight with its converted intensity, keys on their slots
                var lights = venue.GetComponentsInChildren<Light>(true);
                Expect(lights.Length == manifest.Lights.Count, $"{lights.Length} lights (stage.json has {manifest.Lights.Count})");
                foreach (var light in lights)
                {
                    bool known = manifest.Lights.TryGetValue(light.name, out var info);
                    Expect(known, $"light {light.name} is in stage.json");
                    if (!known)
                    {
                        continue;
                    }

                    float factor = info.Type == "AREA" ? FlyBandBuilder.WATTS_TO_URP_AREA : FlyBandBuilder.WATTS_TO_URP_POINT;
                    var venueLight = light.GetComponent<VenueLight>();
                    var location = venueLight != null ? venueLight.Location.ToString() : "none";
                    Expect(venueLight != null && venueLight.Location == FlyBandBuilder.LightLocationFor(light.name),
                        $"light {light.name} is a VenueLight in {location}");
                    Expect(Mathf.Abs(light.intensity - info.Watts * factor) < 1e-3f * light.intensity + 1e-4f,
                        $"light {light.name} intensity {light.intensity:F1} from {info.Watts} W");
                }

                foreach (var slot in manifest.Slots)
                {
                    if (nodes.TryGetValue($"key_{slot}", out var key) && nodes.TryGetValue($"slot_{slot}", out var marker))
                    {
                        float angle = Vector3.Angle(key.forward, marker.position + Vector3.up * KEY_TARGET_HEIGHT - key.position);
                        Expect(angle < KEY_AIM_DEGREES, $"key_{slot} aims at its slot ({angle:F1} deg off)");
                    }
                }

                // Cameras: every stage.json camera, as a VenueCamera, aimed at the band
                var cameras = venue.GetComponentsInChildren<VenueCamera>(true);
                Expect(cameras.Length == manifest.Cameras.Count, $"{cameras.Length} venue cameras (stage.json has {manifest.Cameras.Count})");
                Expect(cameras.Count(c => c.CameraLocation == CameraManager.CameraLocation.Stage) == 1, "one Stage camera");
                var markers = manifest.Slots.Where(s => nodes.ContainsKey($"slot_{s}")).Select(s => nodes[$"slot_{s}"].position).ToList();
                var slotCentre = markers.Aggregate(Vector3.zero, (a, b) => a + b) / Mathf.Max(1, markers.Count);
                foreach (var venueCamera in cameras)
                {
                    var camera = venueCamera.GetComponent<Camera>();
                    bool known = manifest.Cameras.TryGetValue(venueCamera.name, out var info);
                    Expect(known && Mathf.Abs(camera.fieldOfView - FlyBandBuilder.VerticalFov(info.LensMm, info.SensorMm)) < 0.01f,
                        $"camera {venueCamera.name} vertical fov {camera.fieldOfView:F1}");

                    var subject = venueCamera.name.Replace("cam_", string.Empty).Replace("_close", string.Empty);
                    var target = nodes.TryGetValue($"slot_{subject}", out var marker) ? marker.position : slotCentre;
                    float angle = Vector3.Angle(venueCamera.transform.forward,
                        target + Vector3.up * CAMERA_TARGET_HEIGHT - venueCamera.transform.position);
                    Expect(angle < CAMERA_AIM_DEGREES, $"camera {venueCamera.name} aims at {subject} ({angle:F1} deg off)");
                }

                // Renderers: the fixtures enclose their spots, so they cast no shadows; everything is on the Venue layer
                var fixtures = venue.GetComponentsInChildren<Renderer>(true).Where(r => manifest.NoShadowMeshes.Contains(r.name)).ToList();
                Expect(fixtures.Count == manifest.NoShadowMeshes.Count && fixtures.All(r => r.shadowCastingMode == ShadowCastingMode.Off),
                    $"{string.Join(", ", manifest.NoShadowMeshes)} cast no shadows ({string.Join(", ", fixtures.Select(r => $"{r.name}: {r.shadowCastingMode}"))})");

                int venueLayer = LayerMask.NameToLayer(FlyBandBuilder.VENUE_LAYER);
                var offLayer = venue.GetComponentsInChildren<Transform>(true).Where(t => t.gameObject.layer != venueLayer).ToList();
                Expect(offLayer.Count == 0, $"every object is on the {FlyBandBuilder.VENUE_LAYER} layer ({offLayer.Count} are not)");

                var stageRenderers = nodes.TryGetValue("stage", out var stageNode)
                    ? stageNode.GetComponentsInChildren<MeshRenderer>(true)
                    : Array.Empty<MeshRenderer>();
                var stageBounds = new Bounds();
                for (int i = 0; i < stageRenderers.Length; i++)
                {
                    if (i == 0) stageBounds = stageRenderers[i].bounds;
                    else stageBounds.Encapsulate(stageRenderers[i].bounds);
                }
                Expect(stageRenderers.Length > 1 && stageBounds.size.x > 10f && stageBounds.size.x < 16f,
                    $"stage is {stageRenderers.Length} tiles spanning {stageBounds.size:F2} m (venue.py's deck is 13 m wide)");
                var materials = stageRenderers.Select(r => r.sharedMaterial).Where(m => m != null).Distinct()
                    .Select(m => $"{m.name}({m.shader.name}, emission {m.IsKeywordEnabled("_EMISSION")})");
                Debug.Log($"[FlyCheck] venue: stage materials {string.Join(", ", materials)}");

                if (mainCamera != null)
                {
                    RenderVenue(venue, mainCamera, Path.Combine(RENDER_DIR, "venue_wide.png"));
                }

                UnityEngine.Object.DestroyImmediate(venue);
            }
            finally
            {
                bundle.Unload(true);
            }

            Debug.Log($"[FlyCheck] venue: {(failures.Count == 0 ? "OK" : "FAILED: " + string.Join("; ", failures))}");
            return failures.Count == 0;
        }

        private static UnityEngine.Object Serialized(UnityEngine.Object target, string field)
        {
            return new SerializedObject(target).FindProperty(field)?.objectReferenceValue;
        }

        // One frame from the venue's own camera, with the bandmates in their Playing loop
        private static void RenderVenue(GameObject venue, Camera camera, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                foreach (var animator in venue.GetComponentsInChildren<Animator>(true))
                {
                    animator.Play("Playing", 0, 0f);
                    animator.Update(0f);
                    animator.Update(0.3f);
                }

                // band.py's world is nearly black, so the venue gets next to no ambient light either
                RenderSettings.skybox = null;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.002f, 0.002f, 0.004f).gamma;

                var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                camera.targetTexture = rt;
                camera.enabled = true;
                camera.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;

                var pixels = tex.GetPixels();
                float mean = pixels.Average(p => p.grayscale);
                float lit = pixels.Count(p => p.grayscale > 0.1f) / (float) pixels.Length;
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Debug.Log($"[FlyCheck] venue: rendered {path} (mean grey {mean:F3}, {lit:P0} of pixels above 0.1)");

                UnityEngine.Object.DestroyImmediate(tex);
                camera.targetTexture = null;
                rt.Release();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FlyCheck] venue: render skipped: {e}");
            }
        }

        private static Dictionary<Transform, (Vector3 pos, Quaternion rot)> CapturePose(GameObject root)
        {
            return root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => (t.localPosition, t.localRotation));
        }

        private static void RestorePose(Dictionary<Transform, (Vector3 pos, Quaternion rot)> pose)
        {
            foreach (var (t, (pos, rot)) in pose.Select(p => (p.Key, p.Value)))
            {
                t.localPosition = pos;
                t.localRotation = rot;
            }
        }

        private static (string bone, float degrees, float metres) LargestChange(
            Dictionary<Transform, (Vector3 pos, Quaternion rot)> reference, GameObject root, ICollection<string> only)
        {
            string best = "-";
            float bestDeg = 0f, bestM = 0f;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (only != null && !only.Contains(t.name))
                {
                    continue;
                }

                var (pos, rot) = reference[t];
                float deg = Quaternion.Angle(rot, t.localRotation);
                float m = (pos - t.localPosition).magnitude;
                if (deg > bestDeg || (deg == bestDeg && m > bestM))
                {
                    best = t.name;
                    bestDeg = deg;
                    bestM = m;
                }
            }

            return (best, bestDeg, bestM);
        }

        // Renders the fly at its strum impact from the front (+Z, where a VRM bandmate's face points) and the side
        private static void Render(GameObject instance, Animator animator, FlyManifest manifest)
        {
            try
            {
                Directory.CreateDirectory(RENDER_DIR);

                animator.Play("Playing", 0, 0f);
                if (manifest.States.ContainsKey("StrumDown"))
                {
                    animator.Play("StrumDown", animator.GetLayerIndex(manifest.States["StrumDown"].Layer), 0f);
                }
                if (manifest.States.ContainsKey("HandPositionThree"))
                {
                    animator.Play("HandPositionThree", animator.GetLayerIndex(manifest.States["HandPositionThree"].Layer), 0f);
                }
                animator.Update(0f);
                animator.Update(manifest.ImpactFrame / (float) manifest.Fps);

                var bounds = new Bounds(instance.transform.position, Vector3.zero);
                foreach (var r in instance.GetComponentsInChildren<Renderer>())
                {
                    bounds.Encapsulate(r.bounds);
                }
                Debug.Log($"[FlyCheck] {manifest.Slot}: posed bounds center {bounds.center}, size {bounds.size}");
                LogFacing(instance);

                var lightGo = new GameObject("Key light");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.5f;
                lightGo.transform.rotation = Quaternion.Euler(35f, 160f, 0f);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.5f);

                var camGo = new GameObject("Check camera");
                var cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.12f, 0.16f);
                cam.fieldOfView = 30f;
                float distance = bounds.extents.magnitude / Mathf.Tan(15f * Mathf.Deg2Rad) * 1.1f;
                cam.nearClipPlane = distance * 0.01f;
                cam.farClipPlane = distance * 10f;

                var rt = new RenderTexture(768, 1024, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;

                foreach (var (label, dir) in new[] { ("front", Vector3.forward), ("side", Vector3.right) })
                {
                    camGo.transform.position = bounds.center + dir * distance;
                    camGo.transform.LookAt(bounds.center);
                    lightGo.transform.rotation = Quaternion.LookRotation(-dir + Vector3.down * 0.6f + Vector3.left * 0.3f);
                    cam.Render();

                    RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply();
                    RenderTexture.active = null;

                    var path = Path.Combine(RENDER_DIR, $"fly_{manifest.Slot}_strum_{label}.png");
                    File.WriteAllBytes(path, tex.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(tex);
                    Debug.Log($"[FlyCheck] {manifest.Slot}: rendered {path}");
                }

                cam.targetTexture = null;
                rt.Release();
                UnityEngine.Object.DestroyImmediate(camGo);
                UnityEngine.Object.DestroyImmediate(lightGo);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FlyCheck] {manifest.Slot}: render skipped: {e}");
            }
        }

        // Which way the face points, read from the bones: the head should sit on the +Z side of the abdomen
        private static void LogFacing(GameObject instance)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (var t in instance.GetComponentsInChildren<Transform>(true))
            {
                bones.TryAdd(t.name, t);
            }
            foreach (var name in new[] { "root", "pelvis", "chest", "head", "abdomen", "fore1_r", "fore1_l" })
            {
                if (bones.TryGetValue(name, out var t))
                {
                    Debug.Log($"[FlyCheck] bone {name} world {t.position:F3} forward {t.forward:F2} up {t.up:F2}");
                }
            }

            var mesh = instance.GetComponentInChildren<SkinnedMeshRenderer>();
            if (mesh != null)
            {
                Debug.Log($"[FlyCheck] mesh {mesh.name} bounds {mesh.bounds.center:F3} size {mesh.bounds.size:F3}");
            }
        }
    }
}
