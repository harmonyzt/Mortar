using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Audio.SpatialSystem;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Networking;

namespace Mortar
{
    /// <summary>
    /// Map-wide sirens played when artillery is called
    /// </summary>
    public static class MapSiren
    {
        private sealed class LoadedSound
        {
            public AudioClip Clip;
            public UnityWebRequest Request;
        }

        private static readonly Dictionary<string, LoadedSound> Loaded =
            new(StringComparer.OrdinalIgnoreCase);

        private static SirenMapDefinition _map;
        private static bool _warnedNotReady;
        private static float _playingUntil = float.NegativeInfinity;

        internal static void ResetRaidState()
        {
            _playingUntil = float.NegativeInfinity;
            _warnedNotReady = false;
        }

        internal static void Dispose()
        {
            foreach (LoadedSound loaded in Loaded.Values)
            {
                loaded.Request?.Dispose();
            }

            Loaded.Clear();
            _map = null;
        }

        internal static void Prepare(string locationId, bool force = false)
        {
            if (!Plugin.Enable.Value)
            {
                return;
            }

            SirenMapDefinition map = SirenRegistry.Find(locationId);
            if (map == null || !map.IsEnabled)
            {
                return;
            }

            if (force)
            {
                // A clip loaded before the raid scene is up is swept by EFT's asset cleanup,
                // so reload everything after OnGameStarted.
                Dispose();
            }

            _map = map;

            foreach (SirenSoundDefinition sound in map.Sounds)
            {
                EnsureLoaded(sound);
            }
        }

        private static void EnsureLoaded(SirenSoundDefinition sound)
        {
            if (string.IsNullOrEmpty(sound.ResourcePath) || Loaded.ContainsKey(sound.ResourcePath))
            {
                return;
            }

            Loaded[sound.ResourcePath] = new LoadedSound();
            Plugin.Instance.StartCoroutine(Load(sound.ResourcePath));
        }

        public static void Trigger(string expectedLocationId = null)
        {
            if (!Plugin.Enable.Value)
            {
                return;
            }

            // Only the host decides whether the event happens
            if (!CoopSupport.IsClient && !CoopSupport.Enabled)
            {
                return;
            }

            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null)
            {
                return;
            }

            string locationId = world.LocationId;
            if (!string.IsNullOrEmpty(expectedLocationId) &&
                !string.Equals(locationId, expectedLocationId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SirenMapDefinition map = _map;
            if (map == null || !string.Equals(map.LocationId, locationId, StringComparison.OrdinalIgnoreCase))
            {
                map = SirenRegistry.Find(locationId);
            }

            if (map == null || !map.IsEnabled)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (now < _playingUntil)
            {
                Plugin.LogSource.LogInfo($"[Mortar] {map.Name} siren skipped: still playing.");

                return;
            }

            // The host owns the event. Set the guard first so a warning fallback or a
            // duplicate shelling entry cannot broadcast it twice.
            _playingUntil = now + 2f;
            if (!CoopSupport.IsClient && CoopSupport.Network != null)
            {
                Plugin.LogSource.LogInfo($"[Mortar] Broadcasting {map.Name} siren.");
                CoopSupport.Network.BroadcastSiren(map.LocationId);
            }

            List<(SirenSoundDefinition Definition, AudioClip Clip)> ready = new();
            foreach (SirenSoundDefinition sound in map.Sounds)
            {
                if (!Loaded.TryGetValue(sound.ResourcePath, out LoadedSound loaded) || loaded.Clip == null)
                {
                    continue;
                }

                AudioClip clip = loaded.Clip;

                // EFT's raid load can sweep runtime clips; recover from the kept request if so.
                if (clip.channels == 0 && loaded.Request != null)
                {
                    AudioClip recovered = DownloadHandlerAudioClip.GetContent(loaded.Request);
                    if (recovered != null && recovered.channels > 0)
                    {
                        recovered.name = "Mortar_" + Path.GetFileNameWithoutExtension(sound.ResourcePath);
                        recovered.hideFlags = HideFlags.DontUnloadUnusedAsset;

                        loaded.Clip = recovered;
                        clip = recovered;
                    }
                }

                if (clip.channels > 0)
                {
                    ready.Add((sound, clip));
                }
            }

            if (ready.Count == 0)
            {
                if (!_warnedNotReady)
                {
                    _warnedNotReady = true;
                    Plugin.LogSource.LogInfo($"[Mortar] {map.Name} siren warning arrived but no clip is loaded.");
                }

                return;
            }

            float longest = 0f;
            foreach ((SirenSoundDefinition definition, AudioClip clip) in ready)
            {
                longest = Mathf.Max(longest, Play(map, definition, clip));
            }

            _playingUntil = now + Mathf.Max(longest, 2f) + 2f;
        }

        private static float Play(SirenMapDefinition map, SirenSoundDefinition definition, AudioClip clip)
        {
            float duration = definition.Loop
                ? Mathf.Max(definition.LoopSeconds, clip.length)
                : clip.length;

            float delay = Mathf.Max(0f, definition.DelaySeconds);

            if (delay > 0f)
            {
                Plugin.Instance.StartCoroutine(PlayDelayed(map, definition, clip, delay, duration));
            }
            else
            {
                PlayNow(map, definition, clip, duration);
            }

            return delay + duration;
        }

        private static IEnumerator PlayDelayed(
            SirenMapDefinition map,
            SirenSoundDefinition definition,
            AudioClip clip,
            float delay,
            float duration)
        {
            yield return new WaitForSecondsRealtime(delay);

            if (!Plugin.Enable.Value)
            {
                yield break;
            }

            // A delayed sound must not fire after the raid ended or after a map change.
            GameWorld world = Singleton<GameWorld>.Instance;
            if (world == null ||
                !string.Equals(world.LocationId, map.LocationId, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            PlayNow(map, definition, clip, duration);
        }

        private static void PlayNow(
            SirenMapDefinition map,
            SirenSoundDefinition definition,
            AudioClip clip,
            float duration)
        {
            try
            {
                BetterAudio audio = Singleton<BetterAudio>.Instance;
                if (audio == null || !MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated)
                {
                    Plugin.LogSource.LogWarning("[Mortar] EFT audio is not available, siren skipped.");

                    return;
                }

                Vector3 origin = definition.Origin ?? map.Origin;

                BetterSource source = audio.PlayAtPoint(
                    origin,
                    clip,
                    BetterAudio.AudioSourceGroupType.Environment,
                    2000,
                    definition.Volume,
                    EOcclusionTest.None,
                    null,
                    spatialize: definition.Spatial,
                    oneShot: false,
                    autoReleaseSource: false);

                if (source == null)
                {
                    Plugin.LogSource.LogWarning($"[Mortar] Could not borrow a source for '{definition.ResourcePath}'.");

                    return;
                }

                // Cull at 2 km so the source starts even when we are far away
                // Then apply real hearing distance
                if (definition.Spatial && definition.MaxDistance > 0f)
                {
                    source.SetRolloff(definition.MaxDistance);
                }

                // PlayAtPoint never sets base volume and we drive occlusion ourselves
                source.Loop = definition.Loop;
                source.SetBaseVolume(definition.Volume);
                source.UpdateSourceVolume(1f);
                Plugin.Instance.StartCoroutine(DriveSource(source, definition, origin, duration));
            }
            catch (Exception ex)
            {
                Plugin.LogSource.LogWarning($"[Mortar] Siren playback failed: {ex}");
            }
        }

        /// <summary>
        /// Custom outdoor / indoor / isolated support and muffling sound
        /// Releases the source when the (possibly looped) sound is done
        /// </summary>
        private static IEnumerator DriveSource(
            BetterSource source,
            SirenSoundDefinition definition,
            Vector3 origin,
            float duration)
        {
            float cutoff = 22000f;
            float volumeFactor = 1f;
            float elapsed = 0f;

            while (elapsed < duration && source != null)
            {
                float targetCutoff = 22000f;
                float targetVolume = 1f;

                SpatialAudioSystem system = MonoBehaviourSingleton<SpatialAudioSystem>.Instantiated
                    ? MonoBehaviourSingleton<SpatialAudioSystem>.Instance
                    : null;

                ISpatialAudioRoom room = system?.ListenerCurrentRoom;
                if (room != null && !room.IsOutdoor)
                {
                    targetCutoff = room.IsIsolated ? 600f : 1100f;
                    targetVolume = room.IsIsolated ? 0.55f : 0.75f;
                }

                float k = 1f - Mathf.Exp(-Time.deltaTime / 0.6f);
                cutoff = Mathf.Lerp(cutoff, targetCutoff, k);
                volumeFactor = Mathf.Lerp(volumeFactor, targetVolume, k);

                // Flat sources have no Unity 3D attenuation, so fade them by hand
                float distanceFactor = 1f;
                if (!definition.Spatial && definition.MaxDistance > 0f &&
                    system?.ListenerTransform != null)
                {
                    float distance = Vector3.Distance(system.ListenerTransform.position, origin);
                    float t = Mathf.Clamp01(distance / definition.MaxDistance);
                    distanceFactor = 1f - t * t;
                }

                source.SetLowPassFilterParameters(cutoff, 1f);
                source.SetOcclusionVolumeFactor(volumeFactor * distanceFactor);
                source.UpdateSourceVolume(1f);

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (source != null)
            {
                source.SetLowPassFilterParameters(22000f, 1f);
                source.SetOcclusionVolumeFactor(1f);
                source.UpdateSourceVolume(1f);
                source.Release();
            }
        }

        private static IEnumerator Load(string resourcePath)
        {
            string filePath = ResolveAudioFile(resourcePath);
            if (filePath == null)
            {
                Loaded.Remove(resourcePath);
                yield break;
            }

            UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(
                new Uri(filePath).AbsoluteUri, GetAudioType(filePath));
            bool keepRequest = false;

            try
            {
                if (request.downloadHandler is DownloadHandlerAudioClip handler)
                {
                    handler.streamAudio = false;
                }

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Plugin.LogSource.LogWarning(
                        $"[Mortar] Siren '{resourcePath}' failed to load: {request.error}");
                }
                else
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip == null)
                    {
                        Plugin.LogSource.LogWarning($"[Mortar] Siren '{resourcePath}' is empty.");
                    }
                    else
                    {
                        clip.name = "Mortar_" + Path.GetFileNameWithoutExtension(resourcePath);
                        clip.hideFlags = HideFlags.DontUnloadUnusedAsset;

                        if (Loaded.TryGetValue(resourcePath, out LoadedSound loaded))
                        {
                            loaded.Clip = clip;
                            loaded.Request = request;
                            keepRequest = true;
                        }

                        Plugin.LogSource.LogInfo(
                            $"[Mortar] Siren prewarmed: '{resourcePath}' " +
                            $"({clip.length:0.0}s, channels={clip.channels}, freq={clip.frequency}).");
                    }
                }
            }
            finally
            {
                if (!keepRequest)
                {
                    request.Dispose();
                    Loaded.Remove(resourcePath);
                }
            }
        }

        /// <summary>
        /// Prefer an external Resources/ next to the DLL
        /// </summary>
        private static string ResolveAudioFile(string resourcePath)
        {
            string relative = resourcePath.Replace('\\', '/');
            Assembly assembly = typeof(MapSiren).Assembly;

            string assemblyDir = Path.GetDirectoryName(assembly.Location);
            if (!string.IsNullOrEmpty(assemblyDir))
            {
                string external = Path.Combine(
                    assemblyDir, "Resources", relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(external))
                {
                    return external;
                }
            }

            string suffix = relative.Replace('/', '.');
            string resourceName = null;
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = name;
                    break;
                }
            }

            if (resourceName == null)
            {
                Plugin.LogSource.LogWarning(
                    $"[Mortar] Siren '{resourcePath}' is missing from Resources and the assembly.");
                return null;
            }

            using Stream stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return null;
            }

            string cacheDir = Path.Combine(Path.GetTempPath(), "Mortar.Sirens");
            string file = Path.Combine(cacheDir, relative.Replace('/', Path.DirectorySeparatorChar));
            string directory = Path.GetDirectoryName(file);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(file) || new FileInfo(file).Length != stream.Length)
            {
                using FileStream output = File.Create(file);

                stream.CopyTo(output);
            }

            return file;
        }

        private static AudioType GetAudioType(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".mp3":
                    return AudioType.MPEG;
                case ".wav":
                    return AudioType.WAV;
                default:
                    return AudioType.OGGVORBIS;
            }
        }
    }
}